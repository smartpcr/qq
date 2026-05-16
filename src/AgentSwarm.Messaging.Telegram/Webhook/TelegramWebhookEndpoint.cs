using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram.Webhook;

/// <summary>
/// Stage 2.4 webhook receiver — registered as a minimal-API endpoint via
/// <see cref="TelegramWebhookEndpointExtensions.MapTelegramWebhook"/>.
/// Reads the raw <see cref="Update"/> body, persists an
/// <see cref="InboundUpdate"/> durable row (with the verbatim JSON in
/// <see cref="InboundUpdate.RawPayload"/>) BEFORE returning HTTP 200,
/// and enqueues the durable row id on
/// <see cref="InboundUpdateChannel"/> for the
/// <c>InboundUpdateDispatcher</c> background consumer to process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order of operations per architecture.md §5.1 invariant 1.</b>
/// (a) secret-filter validates the
/// <c>X-Telegram-Bot-Api-Secret-Token</c> header BEFORE the controller
/// runs; (b) controller buffers the request body to a string; (c)
/// controller persists <see cref="InboundUpdate"/> with
/// <see cref="IdempotencyStatus.Received"/> via
/// <see cref="IInboundUpdateStore.PersistAsync"/>; (d) duplicate
/// detection — if the store returns <c>false</c>, return 200 without
/// further work (acceptance criterion: "Duplicate webhook delivery does
/// not execute the same human command twice"); (e) enqueue the
/// <see cref="InboundUpdate.UpdateId"/> on the channel for async
/// processing; (f) return 200.
/// </para>
/// <para>
/// <b>Body buffering and deserialization.</b> The controller reads the
/// body to a string FIRST (so the durable
/// <see cref="InboundUpdate.RawPayload"/> is the verbatim wire bytes)
/// and only THEN deserializes — this guarantees the
/// <c>InboundRecoverySweep</c> always has a faithful replay payload
/// for any update that survived the validation gate. A malformed
/// body (empty body, non-JSON, or JSON missing the required
/// <c>update_id</c>) is REJECTED with HTTP 400 and is intentionally
/// NOT persisted: without a usable <c>update_id</c> the row would
/// have no primary key, would collide with future rows on the
/// auto-generated default, and would pollute the durable queue with
/// payloads the dispatcher cannot replay. The malformed body is
/// instead logged at <c>Warning</c> level with its byte length and
/// the request's correlation id so an operator can correlate the
/// failure with upstream traces; Telegram retries malformed bodies
/// independently of our durable store. The dedup / replay invariants
/// therefore only apply to bodies that successfully parse and carry
/// a non-zero <see cref="Telegram.Bot.Types.Update.Id"/>.
/// </para>
/// <para>
/// <b>Correlation id.</b> Honours an inbound
/// <c>X-Correlation-ID</c> header when present; otherwise generates a
/// new <see cref="Activity"/> id (W3C trace-id when an ambient
/// activity is in scope, else a <see cref="Guid"/>) so every durable
/// row, log line, and outbound reply shares the same trace identifier
/// per the "All messages include trace/correlation ID" acceptance
/// criterion.
/// </para>
/// </remarks>
public sealed class TelegramWebhookEndpoint
{
    /// <summary>Route the webhook is bound to (architecture.md §11.3).</summary>
    public const string RoutePattern = "/api/telegram/webhook";

    /// <summary>Optional correlation-id propagation header.</summary>
    public const string CorrelationHeaderName = "X-Correlation-ID";

    private readonly IInboundUpdateStore _store;
    private readonly InboundUpdateChannel _channel;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramWebhookEndpoint> _logger;

    public TelegramWebhookEndpoint(
        IInboundUpdateStore store,
        InboundUpdateChannel channel,
        TimeProvider timeProvider,
        ILogger<TelegramWebhookEndpoint> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles a single webhook POST. Returns an <see cref="IResult"/>
    /// the framework converts to an HTTP response.
    /// </summary>
    /// <remarks>
    /// Response bodies are cast to <see cref="object"/> before flowing
    /// into <see cref="Results.Ok(object?)"/> /
    /// <see cref="Results.BadRequest(object?)"/> so the inferred
    /// <c>TValue</c> is always <see cref="object"/> rather than a
    /// compiler-generated anonymous type. Two consequences: (a) the
    /// runtime result type is the stable
    /// <see cref="Microsoft.AspNetCore.Http.HttpResults.Ok{T}"/> with
    /// <c>T = object</c>, which makes the assertion surface in the
    /// Stage 2.4 endpoint tests deterministic; (b) the JSON serializer
    /// uses the runtime type of the anonymous payload, so the
    /// on-wire shape (camelCase property names) is unchanged.
    /// </remarks>
    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var ct = httpContext.RequestAborted;

        var correlationId = ResolveCorrelationId(httpContext);
        var rawJson = await ReadBodyAsync(httpContext.Request.Body, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _logger.LogWarning(
                "Webhook received empty body. CorrelationId={CorrelationId}", correlationId);
            return Results.BadRequest((object)new { error = "empty_body" });
        }

        Update? update;
        try
        {
            update = JsonSerializer.Deserialize<Update>(rawJson, JsonBotAPI.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Webhook received malformed Update JSON. CorrelationId={CorrelationId} BodyBytes={BodyBytes}",
                correlationId,
                rawJson.Length);
            return Results.BadRequest((object)new { error = "malformed_update_json" });
        }

        if (update is null || update.Id == 0)
        {
            _logger.LogWarning(
                "Webhook received Update with no usable Id. CorrelationId={CorrelationId}", correlationId);
            return Results.BadRequest((object)new { error = "missing_update_id" });
        }

        var row = new InboundUpdate
        {
            UpdateId = update.Id,
            RawPayload = rawJson,
            ReceivedAt = _timeProvider.GetUtcNow(),
            IdempotencyStatus = IdempotencyStatus.Received,
            CorrelationId = correlationId,
        };

        var persisted = await _store.PersistAsync(row, ct).ConfigureAwait(false);
        if (!persisted)
        {
            // Duplicate webhook delivery — UNIQUE constraint short-
            // circuit. Returning 200 (not 409) is deliberate: Telegram
            // treats anything other than 2xx as a retry signal and we
            // do NOT want it to retry. The first delivery's persisted
            // row is the source of truth and the InboundUpdateDispatcher
            // / sweep will drive it to completion exactly once.
            _logger.LogInformation(
                "Webhook duplicate suppressed. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                update.Id,
                correlationId);
            return Results.Ok((object)new { status = "duplicate", updateId = update.Id });
        }

        // Non-blocking enqueue for async processing. We deliberately do
        // NOT await WaitToWriteAsync here: the durable InboundUpdate row
        // is already persisted, so a full-channel state is recoverable
        // by InboundRecoverySweep and Telegram's fast-ACK boundary
        // (implementation-plan.md §183) is more important than draining
        // every burst through this single in-process channel. TryWrite
        // returns false when the bounded channel is at capacity OR has
        // been closed (host shutdown); in either case the row stays in
        // Received and the sweep replays it on the next interval.
        var enqueued = _channel.Writer.TryWrite(update.Id);
        if (!enqueued)
        {
            _logger.LogWarning(
                "InboundUpdateChannel rejected enqueue (full or closed); row persisted, sweep will recover. UpdateId={UpdateId} CorrelationId={CorrelationId}",
                update.Id,
                correlationId);
        }

        _logger.LogInformation(
            "Webhook accepted. UpdateId={UpdateId} CorrelationId={CorrelationId}",
            update.Id,
            correlationId);
        return Results.Ok((object)new { status = "accepted", updateId = update.Id });
    }

    private static string ResolveCorrelationId(HttpContext httpContext)
    {
        var supplied = httpContext.Request.Headers[CorrelationHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }
        return Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
    }

    private static async Task<string> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body, leaveOpen: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="IEndpointRouteBuilder"/> extension that wires the Stage 2.4
/// webhook endpoint with the <see cref="TelegramWebhookSecretFilter"/>.
/// </summary>
public static class TelegramWebhookEndpointExtensions
{
    /// <summary>
    /// Maps <see cref="TelegramWebhookEndpoint.RoutePattern"/> →
    /// <see cref="TelegramWebhookEndpoint.HandleAsync"/> with the
    /// <see cref="TelegramWebhookSecretFilter"/> attached.
    /// </summary>
    public static IEndpointConventionBuilder MapTelegramWebhook(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost(
            TelegramWebhookEndpoint.RoutePattern,
            (HttpContext httpContext, TelegramWebhookEndpoint endpoint) => endpoint.HandleAsync(httpContext))
            .AddEndpointFilter<TelegramWebhookSecretFilter>()
            .WithName("TelegramWebhook")
            .WithDisplayName("Telegram Webhook Receiver");
    }
}
