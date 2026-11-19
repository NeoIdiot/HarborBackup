using System.Net;
using System.Security.Authentication;

namespace HarborBackup.OneDrive.Transport;

internal sealed class NetworkDiagnosticHandler(HttpMessageHandler inner, bool authentication = false, Func<bool>? networkAvailable = null) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (networkAvailable is not null) CloudNetworkAvailability.EnsureAvailable(token, networkAvailable);
        var context = NetworkRequestContext.Capture(request, authentication);
        request.Options.Set(NetworkRequestContext.ContextKey, context);
        try
        {
            var response = await base.SendAsync(request, token);
            response.RequestMessage ??= request;
            response.Content = new DiagnosticContent(response.Content, context);
            return response;
        }
        catch (OperationCanceledException) { throw new NetworkCanceledException(context, token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException)
        { throw context.Failure(ex, "Send/Headers"); }
    }

    private sealed class DiagnosticContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly NetworkRequestContext context;
        public DiagnosticContent(HttpContent inner, NetworkRequestContext context)
        {
            this.inner = inner; this.context = context;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(default);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        {
            try { return new DiagnosticReadStream(await inner.ReadAsStreamAsync(token), context); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { throw context.Failure(ex, "ResponseBodyTimeout"); }
            catch (OperationCanceledException) { throw new NetworkCanceledException(context, token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException)
            { throw context.Failure(ex, "ResponseBody"); }
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? transport) =>
            SerializeToStreamAsync(stream, transport, default);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? transport, CancellationToken token)
        {
            using var source = await CreateContentReadStreamAsync(token);
            // Read failures are wrapped by the source. Destination disk errors stay local I/O errors.
            await source.CopyToAsync(stream, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class DiagnosticReadStream(Stream inner, NetworkRequestContext context) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return inner.Read(buffer, offset, count); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException)
            { throw context.Failure(ex, "ResponseBody"); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { return await inner.ReadAsync(buffer, token); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { throw context.Failure(ex, "ResponseBodyTimeout"); }
            catch (OperationCanceledException) { throw new NetworkCanceledException(context, token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException)
            { throw context.Failure(ex, "ResponseBody"); }
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
