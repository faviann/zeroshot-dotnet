using System.Net;
using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativePrivateClient? privateClient;
    /// <summary>Private-target integration routes. They require operator-held material, not run credentials.</summary>
    public NativePrivateClient Private => privateClient ??= new NativePrivateClient(this);

    internal async Task<NativeAttempt<EmptyResponse>> BootstrapPrivateAsync(TargetDiscoveryDocument discovery,
        TargetPrivateBootstrapRequest request, CancellationToken cancellationToken)
    {
        ValidateHttpUse();
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        var body = NativeJson.SerializeUtf8(request);
        _ = NativeJson.SerializeUtf8(discovery);
        // A closed bootstrap and a nonprivate target return the same 404, so the mode comes from discovery.
        // A target that changes mode after this discovery is still reported as closed.
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            discovery.Authentication != TargetAuthentication.PrivateCapability || discovery.PrivateBootstrapPath is null)
            throw new ArgumentException("Private bootstrap requires private-capability discovery.", nameof(discovery));
        var endpoint = NativeRoutes.SameOriginPath(Origin, discovery.PrivateBootstrapPath);
        // The route is unauthenticated: no bearer is sent, and acceptance issues no client credential.
        var (correlationId, outcome, response, failure) = await AttemptAsync(NativePrivateClient.BootstrapOperation, endpoint, body,
            null, IsBootstrapRefusal, cancellationToken, readSuccess: ReadEmptyAsync).ConfigureAwait(false);
        return new NativeAttempt<EmptyResponse>(Origin, NativePrivateClient.BootstrapOperation.Name, correlationId, outcome, response, failure);
    }

    private static async Task<EmptyResponse> ReadEmptyAsync(HttpResponseMessage response, OperationContext context)
    {
        var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
        var bytes = await context.ReadResponseAsync(stream).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent || bytes.Length != 0)
            throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode);
        return EmptyResponse.Instance;
    }

    // Native transport.rs: an invalid envelope or malformed request (400), a closed bootstrap (404) and a
    // request-read timeout (408) are all answered before the key can be consumed.
    private static bool IsBootstrapRefusal(HttpStatusCode? status, string code) =>
        (status, code) is
            (HttpStatusCode.BadRequest, "request.invalid") or
            (HttpStatusCode.NotFound, "request.not_found") or
            (HttpStatusCode.RequestTimeout, "request.timeout");
}

/// <summary>
/// Private-target routes: the one-time bootstrap and the operator exports. The exports need the private
/// capability; a target in another mode answers 404 <c>request.not_found</c>, which is kept as received.
/// </summary>
public sealed class NativePrivateClient
{
    internal static readonly OperationDescriptor BootstrapOperation = new("private.bootstrap", OperationTransport.Http,
        responseBytes: 64 * 1024);
    // Two retained diagnostics of at most 2 x 4 KiB text each, escaped, fit well inside native's normal 64 KiB bound.
    private static readonly OperationDescriptor DiagnosticsOperation = new("private.operatorDiagnostics", OperationTransport.Http,
        responseBytes: 64 * 1024);
    // Native transport_history.rs admits at most 4096 request bytes; responses keep #35's history bounds.
    private static readonly OperationDescriptor DefinitionOperation = new("private.history.definition", OperationTransport.Http,
        requestBytes: 4096, responseBytes: 8 * 1024 * 1024, problemBytes: 64 * 1024, historyProblems: true);
    private static readonly OperationDescriptor PageOperation = new("private.history.page", OperationTransport.Http,
        requestBytes: 4096, responseBytes: 8 * 1024 * 1024, problemBytes: 64 * 1024, historyProblems: true);
    private const int DiagnosticTextBytes = 4 * 1024;
    private readonly NativeClient client;
    internal NativePrivateClient(NativeClient client) => this.client = client;

    /// <summary>
    /// Sends one caller-prepared envelope. An invalid envelope (400), a closed bootstrap (404) or a native
    /// request-read timeout (408) is <see cref="NativeAttemptOutcome.Rejected"/> with its status and problem retained. Discovery that is not
    /// private throws before sending. After an unknown outcome the key may be consumed; do not resend blindly.
    /// </summary>
    public Task<NativeAttempt<EmptyResponse>> BootstrapAsync(TargetDiscoveryDocument discovery,
        TargetPrivateBootstrapRequest request, CancellationToken cancellationToken = default)
        => client.BootstrapPrivateAsync(discovery, request, cancellationToken);

    /// <summary>
    /// Reads the target's retained operator diagnostics for one run. Native keeps only two diagnostics in
    /// memory across all runs, so an empty list says nothing about whether the run exists.
    /// </summary>
    public Task<TargetOperatorDiagnostics> GetOperatorDiagnosticsAsync(RunId runId, TargetControlCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        Require(runId, credentials);
        return client.ExecuteJsonAsync<TargetOperatorDiagnostics>(DiagnosticsOperation,
            new Uri(client.Origin, "/native-v2/operator-diagnostics/" + runId.Value), null, credentials, snapshot =>
            {
                if (snapshot.Diagnostics.Any(d => d.RunId != runId ||
                    Encoding.UTF8.GetByteCount(d.Stdout) > DiagnosticTextBytes || Encoding.UTF8.GetByteCount(d.Stderr) > DiagnosticTextBytes))
                    throw new JsonException();
            }, cancellationToken);
    }

    /// <summary>Reads the admitted definition through the private export, with the public history checks.</summary>
    public Task<RunDefinition> GetHistoryDefinitionAsync(RunId runId, TargetControlCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        Require(runId, credentials);
        var body = NativeJson.SerializeUtf8(new PrivateHistoryDefinitionRequest { RunId = runId });
        return client.ExecuteJsonAsync<RunDefinition>(DefinitionOperation, new Uri(client.Origin, "/native-v2/history/definition"),
            body, credentials, definition => RunHistoryRules.Definition(definition, runId), cancellationToken);
    }

    /// <summary>
    /// Reads one bounded page strictly after <paramref name="after"/>. Omitting it sends no cursor, which
    /// native reads from <c>v2:0</c>.
    /// </summary>
    public Task<HistoryPage> GetHistoryPageAsync(RunId runId, TargetControlCredentials credentials, Cursor? after = null,
        CancellationToken cancellationToken = default)
    {
        Require(runId, credentials);
        if (after is not null) NativeHistoryClient.RequireCursor(after, nameof(after));
        var body = NativeJson.SerializeUtf8(new PrivateHistoryPageRequest { RunId = runId, After = after });
        return client.ExecuteJsonAsync<HistoryPage>(PageOperation, new Uri(client.Origin, "/native-v2/history/page"),
            body, credentials, page => RunHistoryRules.Page(page, after ?? NativeHistoryClient.InitialCursor), cancellationToken);
    }

    // Ordinary run or hosted credentials never become operator authority.
    private static void Require(RunId runId, TargetControlCredentials credentials)
    {
        NativeHistoryClient.RequireRunId(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.Authentication != TargetAuthentication.PrivateCapability)
            throw new ArgumentException("Private exports require the private capability.", nameof(credentials));
    }
}
