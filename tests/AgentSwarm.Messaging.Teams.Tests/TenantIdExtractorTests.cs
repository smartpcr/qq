using System.Text;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Focused unit tests for the shared <see cref="TenantIdExtractor"/> helper. Both
/// <see cref="TenantValidationMiddleware"/> and <see cref="RateLimitMiddleware"/> route
/// through this helper, so a single source of truth covers the parsing rules previously
/// duplicated across the two middleware classes.
/// </summary>
public sealed class TenantIdExtractorTests
{
    [Fact]
    public async Task ChannelDataTenantId_TakesPrecedenceOverConversationTenantId()
    {
        // Per the Teams payload contract, when both paths are populated, channelData wins.
        const string body = """
            {
              "channelData": { "tenant": { "id": "from-channel-data" } },
              "conversation": { "tenantId": "from-conversation" }
            }
            """;

        var tenantId = await ExtractAsync(body);

        Assert.Equal("from-channel-data", tenantId);
    }

    [Fact]
    public async Task ConversationTenantId_UsedAsFallback_WhenChannelDataAbsent()
    {
        const string body = """{ "conversation": { "tenantId": "from-conversation" } }""";

        var tenantId = await ExtractAsync(body);

        Assert.Equal("from-conversation", tenantId);
    }

    [Fact]
    public async Task EmptyBody_ReturnsNull()
    {
        var tenantId = await ExtractAsync(body: string.Empty);

        Assert.Null(tenantId);
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull_NoThrow()
    {
        var tenantId = await ExtractAsync(body: "{not-json");

        Assert.Null(tenantId);
    }

    [Fact]
    public async Task NonObjectRoot_ReturnsNull()
    {
        // A bare JSON array or string is not a valid Activity payload — extractor returns
        // null so the caller can reject with the appropriate HTTP code.
        var array = await ExtractAsync(body: "[1,2,3]");
        var str = await ExtractAsync(body: "\"a-string\"");

        Assert.Null(array);
        Assert.Null(str);
    }

    [Fact]
    public async Task ChannelDataPresent_ButTenantIdMissing_FallsBackToConversation()
    {
        const string body = """
            {
              "channelData": { "team": { "id": "team-1" } },
              "conversation": { "tenantId": "from-conversation" }
            }
            """;

        var tenantId = await ExtractAsync(body);

        Assert.Equal("from-conversation", tenantId);
    }

    [Fact]
    public async Task ChannelDataTenantIdEmpty_FallsBackToConversation()
    {
        const string body = """
            {
              "channelData": { "tenant": { "id": "   " } },
              "conversation": { "tenantId": "from-conversation" }
            }
            """;

        var tenantId = await ExtractAsync(body);

        Assert.Equal("from-conversation", tenantId);
    }

    [Fact]
    public async Task ChannelDataTenantId_NotAString_IsIgnored()
    {
        // Defensive: if a malformed payload places a number where a string is expected,
        // the extractor must not throw — it falls through to the conversation path.
        const string body = """
            {
              "channelData": { "tenant": { "id": 12345 } },
              "conversation": { "tenantId": "from-conversation" }
            }
            """;

        var tenantId = await ExtractAsync(body);

        Assert.Equal("from-conversation", tenantId);
    }

    [Fact]
    public async Task NoTenantOnEitherPath_ReturnsNull()
    {
        const string body = """{ "type": "message", "id": "act-1" }""";

        var tenantId = await ExtractAsync(body);

        Assert.Null(tenantId);
    }

    [Fact]
    public async Task NonSeekableStream_ReturnsNull()
    {
        // Caller contract requires EnableBuffering() before invoking the extractor; if the
        // stream is not seekable, we return null rather than consume the body and starve
        // CloudAdapter downstream.
        var context = new DefaultHttpContext();
        context.Request.Body = new NonSeekableStream("{\"channelData\":{\"tenant\":{\"id\":\"t\"}}}");
        context.Request.ContentType = "application/json";

        var tenantId = await TenantIdExtractor.TryExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Null(tenantId);
    }

    [Fact]
    public async Task ExtractDoesNotConsumeBody_PositionRemainsUsable()
    {
        // Callers reset Position to 0 themselves; this test pins that the helper leaves the
        // stream in a state where a Position = 0 reset still yields the original payload.
        const string body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-x\"}}}";
        var context = NewBufferedContext(body);

        var tenantId = await TenantIdExtractor.TryExtractFromBodyAsync(context, CancellationToken.None);
        Assert.Equal("tenant-x", tenantId);

        context.Request.Body.Position = 0;
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var roundtrip = await reader.ReadToEndAsync();
        Assert.Equal(body, roundtrip);
    }

    private static Task<string?> ExtractAsync(string body)
    {
        var context = NewBufferedContext(body);
        return TenantIdExtractor.TryExtractFromBodyAsync(context, CancellationToken.None);
    }

    private static HttpContext NewBufferedContext(string body)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(body))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var ms = new MemoryStream(bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;
            context.Request.Body = ms;
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        else
        {
            context.Request.Body = new MemoryStream();
            context.Request.ContentLength = 0;
            context.Request.ContentType = "application/json";
        }
        return context;
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(string content)
        {
            _inner = new MemoryStream(Encoding.UTF8.GetBytes(content));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
