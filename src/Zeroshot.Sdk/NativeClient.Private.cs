using System.Net;
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

/// <summary>Private target bootstrap, which native accepts once per target process.</summary>
public sealed class NativePrivateClient
{
    internal static readonly OperationDescriptor BootstrapOperation = new("private.bootstrap", OperationTransport.Http,
        responseBytes: 64 * 1024);
    private readonly NativeClient client;
    internal NativePrivateClient(NativeClient client) => this.client = client;

    /// <summary>
    /// Sends one caller-prepared envelope. An invalid envelope (400) or a closed bootstrap (404) is
    /// <see cref="NativeAttemptOutcome.Rejected"/> with its status and problem retained. Discovery that is not
    /// private throws before sending. After an unknown outcome the key may be consumed; do not resend blindly.
    /// </summary>
    public Task<NativeAttempt<EmptyResponse>> BootstrapAsync(TargetDiscoveryDocument discovery,
        TargetPrivateBootstrapRequest request, CancellationToken cancellationToken = default)
        => client.BootstrapPrivateAsync(discovery, request, cancellationToken);
}
