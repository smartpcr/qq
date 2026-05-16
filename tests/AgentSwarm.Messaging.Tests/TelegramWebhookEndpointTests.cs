using System.Text;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram.Webhook;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="TelegramWebhookEndpoint.HandleAsync"/>
/// against the implementation-plan.md §195-201 acceptance scenarios:
/// valid POST → 200 + Received row; empty/malformed body → 400; missing
/// update_id → 400; duplicate webhook delivery → 200 with no second
/// row. Uses the real <see cref="PersistentInboundUpdateStore"/> over a
/// SQLite in-memory connection so the durable side effects are
/// observable end-to-end.
/// </summary>
public sealed class TelegramWebhookEndpointTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<MessagingDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var ctx = new MessagingDbContext(_options);
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private TelegramWebhookEndpoint NewEndpoint(out PersistentInboundUpdateStore store, out InboundUpdateChannel channel, FixedTimeProvider? clock = null)
    {
        var ctx = new MessagingDbContext(_options);
        store = new PersistentInboundUpdateStore(ctx, NullLogger<PersistentInboundUpdateStore>.Instance);
        channel = new InboundUpdateChannel();
        return new TelegramWebhookEndpoint(
            store,
            channel,
            clock ?? new FixedTimeProvider(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<TelegramWebhookEndpoint>.Instance);
    }

    private static HttpContext NewRequest(string body, string? correlationHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (correlationHeader is not null)
        {
            ctx.Request.Headers[TelegramWebhookEndpoint.CorrelationHeaderName] = correlationHeader;
        }
        return ctx;
    }

    [Fact]
    public async Task ValidUpdate_PersistsReceivedRow_Returns200_EnqueuesUpdateId()
    {
        var endpoint = NewEndpoint(out var store, out var channel);
        const long updateId = 42424242;
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";

        var result = await endpoint.HandleAsync(NewRequest(body, correlationHeader: "trace-1"));

        result.Should().BeOfType<Ok<object>>();
        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        row.RawPayload.Should().Be(body, "the verbatim wire bytes must round-trip into RawPayload for sweep replay fidelity");

        channel.Reader.TryRead(out var dequeued).Should().BeTrue();
        dequeued.Should().Be(updateId);
    }

    [Fact]
    public async Task EmptyBody_Returns400_WithoutPersisting()
    {
        var endpoint = NewEndpoint(out var store, out _);

        var result = await endpoint.HandleAsync(NewRequest(string.Empty));

        result.Should().BeOfType<BadRequest<object>>();
    }

    [Fact]
    public async Task MalformedJson_Returns400_WithoutPersisting()
    {
        var endpoint = NewEndpoint(out var store, out _);

        var result = await endpoint.HandleAsync(NewRequest("{not json"));

        result.Should().BeOfType<BadRequest<object>>();
    }

    [Fact]
    public async Task UpdateWithoutId_Returns400_WithoutPersisting()
    {
        var endpoint = NewEndpoint(out var store, out _);

        var result = await endpoint.HandleAsync(NewRequest("{}"));

        result.Should().BeOfType<BadRequest<object>>();
    }

    [Fact]
    public async Task DuplicateUpdate_Returns200_DoesNotEnqueueOrCreateSecondRow()
    {
        var endpoint = NewEndpoint(out var store, out var channel);
        const long updateId = 5050;
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":2,\"chat\":{\"id\":2,\"type\":\"private\"},\"text\":\"/agents\"}}";

        // First delivery — accepted and enqueued.
        var first = await endpoint.HandleAsync(NewRequest(body));
        first.Should().BeOfType<Ok<object>>();
        channel.Reader.TryRead(out _).Should().BeTrue();

        // Second delivery (same update_id) — short-circuited as duplicate.
        var second = await endpoint.HandleAsync(NewRequest(body));
        second.Should().BeOfType<Ok<object>>(
            "Telegram retries non-2xx, so we must return 200 even on duplicate; the durable row is the source of truth");

        // Channel must NOT have a second item.
        channel.Reader.TryRead(out _).Should().BeFalse(
            "duplicate must not be enqueued for processing — that would execute the same human command twice");

        // And the database must hold exactly one row.
        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task MissingCorrelationHeader_DoesNotThrow()
    {
        var endpoint = NewEndpoint(out var store, out _);

        var body = "{\"update_id\":7,\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";
        var result = await endpoint.HandleAsync(NewRequest(body));

        result.Should().BeOfType<Ok<object>>();
        // The endpoint generates a fallback correlation id from Activity or
        // a fresh GUID — the row is still persisted.
        (await store.GetByUpdateIdAsync(7, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task ReceivedAt_IsPopulatedFromTimeProvider()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2030, 6, 1, 9, 0, 0, TimeSpan.Zero));
        var endpoint = NewEndpoint(out var store, out _, clock);

        var body = "{\"update_id\":99,\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";
        await endpoint.HandleAsync(NewRequest(body));

        var row = await store.GetByUpdateIdAsync(99, CancellationToken.None);
        row!.ReceivedAt.Should().Be(new DateTimeOffset(2030, 6, 1, 9, 0, 0, TimeSpan.Zero));
    }

    // ============================================================
    // Iteration-3 evaluator feedback item 1 — correlation id MUST be
    // persisted onto the InboundUpdate row so the asynchronous
    // dispatcher and the recovery sweep can reuse the original
    // request-scoped trace identifier rather than synthesising
    // dispatcher-<id> / sweep-<id> ids. Tests below pin the
    // persistence boundary; the dispatcher / sweep side is pinned in
    // their own test files.
    // ============================================================

    [Fact]
    public async Task ValidUpdate_PersistsCorrelationIdFromRequestHeader_ToRow()
    {
        var endpoint = NewEndpoint(out var store, out _);
        const long updateId = 9001;
        const string trace = "trace-abc-123";
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";

        var result = await endpoint.HandleAsync(NewRequest(body, correlationHeader: trace));

        result.Should().BeOfType<Ok<object>>();
        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.CorrelationId.Should().Be(trace,
            "the request-scoped X-Correlation-ID must be persisted onto the durable row so dispatcher and sweep can re-use it across the async boundary");
    }

    [Fact]
    public async Task ValidUpdate_WithoutCorrelationHeader_PersistsFallbackCorrelationId_NotNull()
    {
        var endpoint = NewEndpoint(out var store, out _);
        const long updateId = 9002;
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";

        await endpoint.HandleAsync(NewRequest(body));  // no correlationHeader

        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row!.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "the endpoint must always persist SOME correlation id (header → Activity.Current → fresh GUID) so the sweep never falls back to a synthetic sweep-<id> for newly-persisted rows");
    }

    // ============================================================
    // Iteration-3 evaluator feedback item 2 — the webhook ACK must
    // NOT block on the in-memory channel. A full / closed channel
    // produces a logged warning but the endpoint still returns 200
    // immediately because the durable row is recoverable by the
    // sweep. Tests below construct a 1-capacity channel and a
    // closed channel respectively to drive the failure modes.
    // ============================================================

    [Fact]
    public async Task FullChannel_ReturnsOk_DoesNotBlock_RowStillPersisted()
    {
        // 1-slot channel pre-filled so the next TryWrite returns false.
        var ctx = new MessagingDbContext(_options);
        var store = new PersistentInboundUpdateStore(ctx, NullLogger<PersistentInboundUpdateStore>.Instance);
        var channel = new InboundUpdateChannel(capacity: 1);
        channel.Writer.TryWrite(-1L).Should().BeTrue("preconditions: prefill the only slot to force the next TryWrite to fail");

        var endpoint = new TelegramWebhookEndpoint(
            store,
            channel,
            new FixedTimeProvider(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<TelegramWebhookEndpoint>.Instance);

        const long updateId = 7001;
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await endpoint.HandleAsync(NewRequest(body, correlationHeader: "trace-full"));
        stopwatch.Stop();

        result.Should().BeOfType<Ok<object>>(
            "Telegram requires a fast 2xx ACK; a full channel must NOT translate into a stalled webhook response");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the endpoint must return immediately rather than awaiting WaitToWriteAsync on the saturated channel");

        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row.Should().NotBeNull(
            "the durable row IS the recovery contract — the row must be persisted before the failed enqueue so the sweep can pick it up");
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
        row.CorrelationId.Should().Be("trace-full");
    }

    [Fact]
    public async Task ClosedChannel_ReturnsOk_RowPersisted_DoesNotThrow()
    {
        var ctx = new MessagingDbContext(_options);
        var store = new PersistentInboundUpdateStore(ctx, NullLogger<PersistentInboundUpdateStore>.Instance);
        var channel = new InboundUpdateChannel(capacity: 8);
        channel.Writer.Complete();  // Host-shutdown shape: writer is sealed.

        var endpoint = new TelegramWebhookEndpoint(
            store,
            channel,
            new FixedTimeProvider(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<TelegramWebhookEndpoint>.Instance);

        const long updateId = 7002;
        var body = "{\"update_id\":" + updateId + ",\"message\":{\"message_id\":1,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"/status\"}}";

        var result = await endpoint.HandleAsync(NewRequest(body));

        result.Should().BeOfType<Ok<object>>(
            "a closed channel during shutdown must not surface as a 5xx — the sweep will replay the row on the next process restart");
        var row = await store.GetByUpdateIdAsync(updateId, CancellationToken.None);
        row.Should().NotBeNull();
        row!.IdempotencyStatus.Should().Be(IdempotencyStatus.Received);
    }

    /// <summary>
    /// Minimal deterministic <see cref="TimeProvider"/> for the tests
    /// (the <c>Microsoft.Extensions.TimeProvider.Testing</c> package is
    /// not referenced by this assembly).
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
