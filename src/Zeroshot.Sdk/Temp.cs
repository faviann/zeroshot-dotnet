using System.Net.Http.Headers;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

// TEMP: removed once every capability binds through the module.
public sealed partial class NativeClient
{
    internal Task<NativeHeadResult> ExecuteHeadAsync(OperationDescriptor operation, Uri requestUri,
        TargetControlCredentials? credentials, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
        => ExecuteHttpAsync(operation, HttpMethod.Head, requestUri, null, credentials,
            (response, _) => Task.FromResult(new NativeHeadResult(response.StatusCode,
                response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.MediaType)),
            cancellationToken, configure: configure);
    internal static void NoStore(HttpRequestMessage request) => request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
    internal Task<TStream> OpenStreamAsync<TRecord, TStream>(OperationDescriptor operation, Uri requestUri,
        TargetControlCredentials? credentials, Func<HttpResponseMessage, bool> admits, Action<HttpRequestMessage> configure,
        Func<ObservationQueue<TRecord, Cursor>, HttpResponseMessage, Stream, TStream> create, CancellationToken cancellationToken)
        => StartStreamAsync(operation, requestUri, credentials, admits, configure, create, cancellationToken);
    internal int MessageBytesTemp => limits.MessageBytes;
}
