using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Per-<see cref="OutboundSourceType"/> field contract tests.
///
/// Each <see cref="OutboundSourceType"/> value has a distinct contract for
/// <see cref="OutboundMessage.Payload"/> and
/// <see cref="OutboundMessage.SourceEnvelopeJson"/> per architecture.md §3.1.
/// This class is a Theory-driven matrix that pins all four source-types in
/// a single place, complementing the scenario-focused tests in
/// <see cref="TelegramMessengerConnectorTests"/>. The matrix shape makes
/// future drift loud: if a new <see cref="OutboundSourceType"/> is added
/// without updating these contracts, the missing row stands out.
///
/// Contract pinned (architecture.md §3.1):
///   - <see cref="OutboundSourceType.Question"/>: Payload is a short
///     diagnostic preview <c>[Severity] Title</c>; SourceEnvelopeJson is
///     the full <see cref="AgentQuestionEnvelope"/> JSON.
///   - <see cref="OutboundSourceType.Alert"/>: Payload is the
///     pre-rendered text; SourceEnvelopeJson is the serialized
///     <see cref="MessengerMessage"/> at the connector boundary (acting as
///     the projected alert source envelope).
///   - <see cref="OutboundSourceType.StatusUpdate"/>: Payload is the
///     pre-rendered text; SourceEnvelopeJson is null (no upstream
///     envelope to preserve).
///   - <see cref="OutboundSourceType.CommandAck"/>: Payload is the
///     pre-rendered text; SourceEnvelopeJson is null.
/// </summary>
public class OutboundMessageSourceTypeContractTests
{
    // ============================================================
    // Payload + SourceEnvelopeJson matrix — non-question source types
    // ============================================================

    [Theory]
    [InlineData(
        "StatusUpdate",
        "status text body",
        OutboundSourceType.StatusUpdate,
        false,
        "s:agent-x:trace-1")]
    [InlineData(
        "CommandAck",
        "ack body",
        OutboundSourceType.CommandAck,
        false,
        "c:trace-1")]
    public async Task SendMessageAsync_NonAlertSourceTypes_PreservePayloadAndOmitEnvelope(
        string sourceTypeMetadata,
        string text,
        OutboundSourceType expectedSourceType,
        bool expectEnvelope,
        string expectedIdempotencyKey)
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var message = new MessengerMessage
        {
            MessageId = "msg-1",
            CorrelationId = "trace-1",
            ConversationId = "conv-1",
            AgentId = "agent-x",
            Timestamp = DateTimeOffset.UtcNow,
            Text = text,
            Severity = MessageSeverity.Normal,
            Metadata = new Dictionary<string, string>
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "42",
                [TelegramMessengerConnector.SourceTypeMetadataKey] = sourceTypeMetadata,
            },
        };

        await connector.SendMessageAsync(message, CancellationToken.None);

        var enqueued = queue.Enqueued.Should().ContainSingle().Subject;
        enqueued.SourceType.Should().Be(expectedSourceType);
        enqueued.IdempotencyKey.Should().Be(expectedIdempotencyKey);
        enqueued.Payload.Should().Be(
            text,
            "architecture.md §3.1: for {0}, Payload is the pre-rendered Telegram text passed verbatim to IMessageSender.SendTextAsync",
            expectedSourceType);

        if (expectEnvelope)
        {
            enqueued.SourceEnvelopeJson.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            enqueued.SourceEnvelopeJson.Should().BeNull(
                "architecture.md §3.1: SourceEnvelopeJson is null for {0} — these message types are self-describing through Payload and have no upstream envelope to preserve",
                expectedSourceType);
        }
    }

    [Fact]
    public async Task SendMessageAsync_AlertSourceType_PreservesPayloadAndSerialisesEnvelope()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var message = new MessengerMessage
        {
            MessageId = "msg-alert-99",
            CorrelationId = "trace-alert-99",
            ConversationId = "conv-1",
            AgentId = "monitor-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = "disk full",
            Severity = MessageSeverity.Critical,
            Metadata = new Dictionary<string, string>
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "999",
                [TelegramMessengerConnector.SourceTypeMetadataKey] = "Alert",
                [TelegramMessengerConnector.AlertIdMetadataKey] = "alert-99",
            },
        };

        await connector.SendMessageAsync(message, CancellationToken.None);

        var enqueued = queue.Enqueued.Should().ContainSingle().Subject;
        enqueued.SourceType.Should().Be(OutboundSourceType.Alert);
        enqueued.Payload.Should().Be(
            "disk full",
            "architecture.md §3.1: for Alert, Payload carries the pre-rendered Telegram text (the SourceEnvelopeJson sidecar preserves the original source envelope independently)");

        enqueued.SourceEnvelopeJson.Should().NotBeNullOrWhiteSpace(
            "architecture.md §3.1: SourceEnvelopeJson is populated for Alert so dead-letter replay and audit can reconstruct the original alert without re-querying the swarm event source");

        using var doc = JsonDocument.Parse(enqueued.SourceEnvelopeJson!);
        var root = doc.RootElement;
        root.GetProperty("MessageId").GetString().Should().Be("msg-alert-99");
        root.GetProperty("CorrelationId").GetString().Should().Be("trace-alert-99");
        root.GetProperty("AgentId").GetString().Should().Be("monitor-1");
        root.GetProperty("Text").GetString().Should().Be("disk full");
        root.GetProperty("Severity").GetInt32().Should().Be((int)MessageSeverity.Critical);
    }

    // ============================================================
    // Question source-type — Payload is a preview, NOT the rendered text
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_QuestionSourceType_PayloadIsDiagnosticPreview_AndEnvelopeJsonIsFullSerialisation()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var question = new AgentQuestion
        {
            QuestionId = "q-pin-1",
            CorrelationId = "trace-q-1",
            AgentId = "release-agent",
            TaskId = "task-1",
            Title = "Promote release to prod?",
            Body = "Build is green; staging soak passed at 99.8%.",
            Severity = MessageSeverity.High,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "yes" },
                new HumanAction { ActionId = "reject",  Label = "Reject",  Value = "no" },
            },
        };

        var envelope = new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = "approve",
            RoutingMetadata = new Dictionary<string, string>
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "12345",
            },
        };

        await connector.SendQuestionAsync(envelope, CancellationToken.None);

        var enqueued = queue.Enqueued.Should().ContainSingle().Subject;
        enqueued.SourceType.Should().Be(OutboundSourceType.Question);
        enqueued.IdempotencyKey.Should().Be(
            "q:release-agent:q-pin-1",
            "architecture.md §3.1 question idempotency key is q:{AgentId}:{QuestionId}");
        enqueued.ChatId.Should().Be(12345L);

        enqueued.Payload.Should().Be(
            "[High] Promote release to prod?",
            "architecture.md §3.1: for Question, Payload is a debug/dead-letter preview formatted as '[Severity] Title' — NOT the actual MarkdownV2 send content (that is rendered at send time by TelegramMessageSender.SendQuestionAsync from SourceEnvelopeJson)");

        enqueued.SourceEnvelopeJson.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(enqueued.SourceEnvelopeJson!);
        var root = doc.RootElement;
        root.GetProperty("Question").GetProperty("QuestionId").GetString().Should().Be("q-pin-1");
        root.GetProperty("Question").GetProperty("AgentId").GetString().Should().Be("release-agent");
        root.GetProperty("Question").GetProperty("Title").GetString().Should().Be("Promote release to prod?");
        root.GetProperty("ProposedDefaultActionId").GetString().Should().Be(
            "approve",
            "QuestionRecoverySweep (Stage 3.6 / architecture.md §3.1 Gap B) rehydrates ProposedDefaultActionId from this exact JSON path to backfill PendingQuestionRecord.DefaultActionId/DefaultActionValue");
    }

    // ============================================================
    // OutboundMessage record validation invariants
    // (independent of the connector — pins the abstraction-layer contract)
    // ============================================================

    [Theory]
    [InlineData(OutboundSourceType.Question)]
    [InlineData(OutboundSourceType.Alert)]
    [InlineData(OutboundSourceType.StatusUpdate)]
    [InlineData(OutboundSourceType.CommandAck)]
    public void OutboundMessage_AnyValidSourceType_AcceptsNullSourceEnvelopeJson(OutboundSourceType sourceType)
    {
        // The Abstractions-layer record does NOT enforce per-SourceType
        // SourceEnvelopeJson population — that is a producer-side
        // contract enforced by TelegramMessengerConnector. The record
        // itself is intentionally permissive so a future producer (e.g.
        // SlackMessengerConnector) can adopt the same contract without
        // record-level breaking changes. This test pins the
        // permissiveness so an accidental [Required] on
        // SourceEnvelopeJson would fail loudly.
        var act = () => _ = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "key-1",
            ChatId = 1L,
            Payload = "p",
            Severity = MessageSeverity.Low,
            SourceType = sourceType,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1",
            SourceEnvelopeJson = null,
        };

        act.Should().NotThrow(
            "the abstraction record is intentionally permissive on SourceEnvelopeJson; per-SourceType contract enforcement lives in the connector layer");
    }

    [Fact]
    public void OutboundMessage_EmptyCorrelationId_ThrowsAtConstruction()
    {
        // "All messages include trace/correlation ID" acceptance
        // criterion — guarded by CorrelationIdValidation.Require in
        // OutboundMessage's init-only setter.
        var act = () => _ = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "key-1",
            ChatId = 1L,
            Payload = "p",
            Severity = MessageSeverity.Low,
            SourceType = OutboundSourceType.StatusUpdate,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = "   ",
        };

        act.Should().Throw<ArgumentException>(
            "a whitespace-only CorrelationId would silently drop the trace at the send boundary; the validator is the last guard before the durable outbox row");
    }

    [Theory]
    [InlineData(OutboundSourceType.Question)]
    [InlineData(OutboundSourceType.Alert)]
    [InlineData(OutboundSourceType.StatusUpdate)]
    [InlineData(OutboundSourceType.CommandAck)]
    public void OutboundMessage_AnyValidSourceType_DefaultStatusIsPending(OutboundSourceType sourceType)
    {
        // Architecture.md §3.1: every newly-constructed OutboundMessage
        // starts at OutboundMessageStatus.Pending. The state machine
        // (Pending → Sending → Sent | Failed | DeadLettered) is
        // owned by IOutboundQueue; the record's default lets producers
        // omit Status without accidentally landing in Sending.
        var outbound = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = $"key-{sourceType}",
            ChatId = 1L,
            Payload = "p",
            Severity = MessageSeverity.Low,
            SourceType = sourceType,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1",
        };

        outbound.Status.Should().Be(OutboundMessageStatus.Pending);
        outbound.AttemptCount.Should().Be(0);
        outbound.MaxAttempts.Should().Be(5, "default retry budget per architecture.md §3.1");
        outbound.SentAt.Should().BeNull();
        outbound.TelegramMessageId.Should().BeNull();
        outbound.NextRetryAt.Should().BeNull();
        outbound.ErrorDetail.Should().BeNull();
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static TelegramMessengerConnector BuildConnector(RecordingOutboundQueue queue)
        => new(
            queue,
            new ProcessedMessengerEventChannel(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-05-16T05:50:00Z")),
            NullLogger<TelegramMessengerConnector>.Instance);

    private sealed class RecordingOutboundQueue : IOutboundQueue
    {
        public List<OutboundMessage> Enqueued { get; } = new();

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Enqueued.Add(message);
            return Task.CompletedTask;
        }

        public Task<OutboundMessage?> DequeueAsync(CancellationToken ct)
            => Task.FromResult<OutboundMessage?>(null);

        public Task MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeadLetterAsync(Guid messageId, string reason, CancellationToken ct)
            => Task.CompletedTask;
    }
}
