using System.Net;
using System.Net.Http.Headers;
using L2Mod.Core.Updates;

namespace L2Mod.Core.Tests.Updates;

/// <summary><see cref="HttpDownloadClient"/> over an in-process handler; no network.</summary>
public class HttpDownloadClientTests
{
    private static readonly Uri Address = new Uri("https://cdn.example.invalid/cp/launcher/CpLauncher-1.0.1.exe");

    [Fact]
    public async Task OpenAsync_StreamsTheBodyWithItsLength()
    {
        byte[] body = UpdateTestData.Executable(5, 5000);
        var handler = new StubHandler(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient(handler);

        using (DownloadResponse response = await new HttpDownloadClient(httpClient).OpenAsync(Address, CancellationToken.None))
        {
            var copy = new MemoryStream();
            await response.Content.CopyToAsync(copy);

            Assert.Equal(body.Length, response.ContentLength);
            Assert.Equal(body, copy.ToArray());
        }

        Assert.Equal(Address, handler.RequestedUri);
        Assert.True(handler.LastResponseDisposed);
    }

    [Fact]
    public async Task OpenAsync_ErrorStatus_ThrowsWithTheStatusAndDisposesTheResponse()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, Array.Empty<byte>());
        using var httpClient = new HttpClient(handler);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => new HttpDownloadClient(httpClient).OpenAsync(Address, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.True(handler.LastResponseDisposed);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private TrackingContent? _content;

        public StubHandler(HttpStatusCode status, byte[] body)
        {
            _status = status;
            _body = body;
        }

        public Uri? RequestedUri { get; private set; }

        public bool LastResponseDisposed
        {
            get
            {
                return _content is not null && _content.Disposed;
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            _content = new TrackingContent(_body);
            var response = new HttpResponseMessage(_status)
            {
                Content = _content,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent(byte[] body)
            : base(body)
        {
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
