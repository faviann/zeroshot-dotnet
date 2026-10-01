using System.Net;
using System.Net.Http.Headers;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>The headers a native caller sends for an operation beyond those every request carries.</summary>
internal enum RequestHeaders
{
    None,
    /// <summary>Cache-Control: no-store.</summary>
    NoStore,
    /// <summary>Accept: application/json.</summary>
    AcceptJson,
    /// <summary>Accept: application/json with Cache-Control: no-store (native hosted_get_json and history transport).</summary>
    Json,
    /// <summary>A form body with reqwest's default Accept: */*.</summary>
    Form
}

/// <summary>
/// One HTTP operation, declared once: its limits, request headers, response policy, the refusals that prove an
/// attempt had no effect, and the identity a response must carry for the ID the call addressed. A capability served
/// either by a direct target's UI router or by a host-owned API declares both policies; the route says which one it reached.
/// </summary>
internal sealed class HttpBinding<T>(OperationDescriptor operation, RequestHeaders headers = RequestHeaders.None,
    Func<HttpStatusCode?, string, bool>? refusals = null, Action<T, RunId>? identity = null, HttpResponsePolicy? response = null,
    HttpResponsePolicy? hostOwned = null)
{
    internal OperationDescriptor Operation => operation;
    internal HttpResponsePolicy Response(HttpCall call) => call.HostOwned ? hostOwned! : response ?? HttpResponsePolicy.Target;
    internal Func<HttpStatusCode?, string, bool> Refusals => refusals!;

    internal Action<HttpRequestMessage>? Configure(Action<HttpRequestMessage>? extra = null) => headers switch
    {
        RequestHeaders.None => extra,
        RequestHeaders.NoStore => request => { NoStore(request); extra?.Invoke(request); },
        RequestHeaders.AcceptJson => request => { AcceptJson(request); extra?.Invoke(request); },
        RequestHeaders.Json => request => { AcceptJson(request); NoStore(request); extra?.Invoke(request); },
        RequestHeaders.Form => request => { Form(request); extra?.Invoke(request); },
        _ => throw new InvalidOperationException()
    };

    internal Action<T> Validate(RunId? id, Action<T>? validate) => result =>
    {
        identity?.Invoke(result, id!);
        validate?.Invoke(result);
    };

    private static void NoStore(HttpRequestMessage request) => request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
    private static void AcceptJson(HttpRequestMessage request) => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    private static void Form(HttpRequestMessage request)
    {
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }
}

/// <summary>A routed request: its URL, its body (GET when absent) and whether a host-owned API serves it.</summary>
internal readonly record struct HttpCall(Uri Uri, byte[]? Body = null, bool HostOwned = false)
{
    public static implicit operator HttpCall(Uri uri) => new(uri);
}

// The capability binding: every HTTP operation enters through one of these, which checks the client may still
// be used before the route runs, then sends the routed call once. Routes validate caller input and discovery,
// so invalid use throws before anything is sent. Reads throw synchronously; attempts fault their task.
public sealed partial class NativeClient
{
    internal const string DiscoveryKind = "zeroshot.native-v2-target/v2";

    internal Task<T> ReadAsync<T>(HttpBinding<T> binding, Func<HttpCall> route, TargetControlCredentials? credentials,
        CancellationToken cancellationToken, RunId? id = null, Action<T>? validate = null)
    {
        ValidateHttpUse();
        var call = route();
        return ExecuteJsonAsync(binding.Operation, binding.Response(call), call.Uri, call.Body, credentials, binding.Validate(id, validate),
            cancellationToken, configure: binding.Configure());
    }

    internal async Task<NativeAttempt<T>> MutateAsync<T>(HttpBinding<T> binding, Func<HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken, RunId? id = null, Action<T>? validate = null,
        Action<HttpRequestMessage>? configure = null, Func<HttpResponseMessage, OperationContext, Task<T>>? readSuccess = null) where T : class
    {
        ValidateHttpUse();
        var call = route();
        return await AttemptAsync(binding.Operation, binding.Response(call), call.Uri, call.Body!, credentials, binding.Refusals, cancellationToken,
            binding.Configure(configure), readSuccess, binding.Validate(id, validate)).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens one streaming GET whose response becomes a bounded observation. Queue admission precedes dispatch,
    /// and the opening token also cancels the observation later. A refusal before the stream starts is a
    /// <see cref="NativeHttpException"/>; <paramref name="admits"/> checks a successful response. Native bounds
    /// frames at <paramref name="frameBytes"/>; the configured message ceiling can only lower it.
    /// </summary>
    internal Task<TStream> OpenStreamAsync<TRecord, TStream>(HttpBinding<TStream> binding, Func<HttpCall> route,
        TargetControlCredentials? credentials, Func<HttpResponseMessage, bool> admits, Action<HttpRequestMessage> configure, int frameBytes,
        Func<ObservationQueue<TRecord, Cursor>, HttpResponseMessage, Stream, int, TStream> create, CancellationToken cancellationToken)
    {
        ValidateHttpUse();
        var call = route();
        return StartStreamAsync<TRecord, TStream>(binding.Operation, binding.Response(call), call.Uri, credentials, admits, binding.Configure(configure),
            (queue, response, body) => create(queue, response, body, Math.Min(limits.MessageBytes, frameBytes)), cancellationToken);
    }

    /// <summary>A body-less HEAD; refusals keep their status like any other HTTP operation.</summary>
    internal Task<NativeHeadResult> HeadAsync(HttpBinding<NativeHeadResult> binding, Func<HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => ExchangeAsync(binding, HttpMethod.Head, route, credentials, (response, _) => Task.FromResult(new NativeHeadResult(
            response.StatusCode, response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.MediaType)), cancellationToken);

    /// <summary>A body-less exchange whose success response is not JSON. Some browser routes answer with a redirect as their result.</summary>
    internal Task<T> ExchangeAsync<T>(HttpBinding<T> binding, HttpMethod method, Func<HttpCall> route,
        TargetControlCredentials? credentials, Func<HttpResponseMessage, OperationContext, Task<T>> readSuccess,
        CancellationToken cancellationToken, bool redirectIsResult = false)
    {
        ValidateHttpUse();
        var call = route();
        return ExecuteHttpAsync(binding.Operation, binding.Response(call), method, call.Uri, null, credentials, readSuccess, cancellationToken,
            configure: binding.Configure(), redirectIsResult: redirectIsResult);
    }

    // Before any argument or discovery check. SendAsync repeats the authorization check: a supplied client's
    // default headers can still change before dispatch.
    private void ValidateHttpUse()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (http.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Supply credentials per operation, not as HTTP default headers.");
    }

    internal static bool IsControllerDiscovery(TargetDiscoveryDocument discovery)
        => discovery.Kind == DiscoveryKind && discovery.Audience == "controller";

    // Every discovery gate: no remote descriptor may influence dispatch until validated.
    internal static void Admit(TargetDiscoveryDocument discovery, Func<TargetDiscoveryDocument, bool> admits, string message,
        string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        _ = NativeJson.SerializeUtf8(discovery);
        if (!IsControllerDiscovery(discovery) || !admits(discovery)) throw new ArgumentException(message, paramName);
    }

    // Native refuses direct targets; only hosted OAuth discovery can carry host-owned capabilities.
    internal static void AdmitHosted(TargetDiscoveryDocument discovery, TargetControlCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(credentials);
        Admit(discovery, d => d.Authentication == TargetAuthentication.HostedOauth && credentials.Authentication == TargetAuthentication.HostedOauth,
            "Hosted operations require hosted OAuth discovery and matching credentials.");
    }

    // A hosted management call's body, after its gate.
    internal static byte[] HostedBody(TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        AdmitHosted(discovery, credentials);
        return NativeJson.SerializeUtf8(request);
    }

    // Native refuses a missing or foreign capability descriptor before compiling any of its routes.
    internal static TWire Advertised<TWire>(TWire? wire, Func<TWire, string> kind, string expected, string missing, string incompatible)
        where TWire : class
        => wire is null ? throw new ArgumentException(missing)
            : kind(wire) != expected ? throw new ArgumentException(incompatible) : wire;

    // Native's status-derived default codes (default_http_error_code in contract/http_error.rs), which native
    // itself uses only when a response has no parseable problem. A hosted server's own codes are not pinned
    // and native serves no connection, profile or OAuth routes; every other received failure leaves the effect unknown.
    internal static bool IsHostedRefusal(HttpStatusCode? status, string code) =>
        (status, code) is
            (HttpStatusCode.BadRequest, "invalid_request") or
            (HttpStatusCode.Unauthorized, "unauthorized") or
            (HttpStatusCode.Forbidden, "forbidden") or
            (HttpStatusCode.NotFound, "not_found");
}
