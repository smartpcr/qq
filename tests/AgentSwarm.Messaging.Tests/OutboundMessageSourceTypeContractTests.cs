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
    // Idempotency-key derivation matrix — architecture.md §3.1
    // (single Theory pinning all four per-SourceType formulas so a
    // future regression on any one row stands out next to the others)
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_StatusUpdate_IdempotencyKeyFollowsS_AgentId_CorrelationId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-7",
                correlationId: "trace-status-1",
                text: "status",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = "StatusUpdate",
                }),
            CancellationToken.None);

        queue.Enqueued.Single().IdempotencyKey.Should().Be(
            "s:agent-7:trace-status-1",
            "architecture.md §3.1: StatusUpdate idempotency key is 's:{AgentId}:{CorrelationId}'");
    }

    [Fact]
    public async Task SendMessageAsync_CommandAck_IdempotencyKeyFollowsC_CorrelationId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-7",
                correlationId: "trace-ack-1",
                text: "ack",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = "CommandAck",
                }),
            CancellationToken.None);

        queue.Enqueued.Single().IdempotencyKey.Should().Be(
            "c:trace-ack-1",
            "architecture.md §3.1: CommandAck idempotency key is 'c:{CorrelationId}' (agent-agnostic — acks belong to a command, not an agent)");
    }

    [Fact]
    public async Task SendMessageAsync_Alert_IdempotencyKeyFollowsAlert_AgentId_AlertId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "monitor-3",
                correlationId: "trace-alert-7",
                text: "alert body",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = "Alert",
                    [TelegramMessengerConnector.AlertIdMetadataKey] = "alrt-7",
                }),
            CancellationToken.None);

        queue.Enqueued.Single().IdempotencyKey.Should().Be(
            "alert:monitor-3:alrt-7",
            "architecture.md §3.1: Alert idempotency key is 'alert:{AgentId}:{AlertId}' (CorrelationId is NOT part of the key — distinct trace ids of the same logical alert collapse to one outbox row, which is the desired dedup semantics for monitor-emitted alerts)");
    }

    [Fact]
    public async Task SendQuestionAsync_IdempotencyKeyFollowsQ_AgentId_QuestionId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var envelope = BuildQuestionEnvelope(agentId: "release-bot", questionId: "q-42");

        await connector.SendQuestionAsync(envelope, CancellationToken.None);

        queue.Enqueued.Single().IdempotencyKey.Should().Be(
            "q:release-bot:q-42",
            "architecture.md §3.1: Question idempotency key is 'q:{AgentId}:{QuestionId}'");
    }

    // ============================================================
    // SourceEnvelopeJson round-trip — Question envelopes MUST
    // deserialize losslessly so QuestionRecoverySweep (Stage 3.6 /
    // architecture.md §3.1 Gap B) can rehydrate PendingQuestionRecord
    // after a crash between MarkSentAsync and store persistence.
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_SourceEnvelopeJson_RoundTripsToAgentQuestionEnvelope()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var question = new AgentQuestion
        {
            QuestionId = "q-roundtrip-1",
            CorrelationId = "trace-rt-1",
            AgentId = "rt-agent",
            TaskId = "task-rt",
            Title = "Round-trip pin",
            Body = "Confirm the envelope deserializes losslessly.",
            Severity = MessageSeverity.High,
            ExpiresAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "yes", Label = "Yes", Value = "1" },
                new HumanAction { ActionId = "no",  Label = "No",  Value = "0" },
            },
        };

        var original = new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = "yes",
            RoutingMetadata = new Dictionary<string, string>
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "7777",
                ["TenantId"] = "tenant-A",
            },
        };

        await connector.SendQuestionAsync(original, CancellationToken.None);

        var enqueued = queue.Enqueued.Single();
        enqueued.SourceEnvelopeJson.Should().NotBeNullOrWhiteSpace();

        // The connector serializes with default (PascalCase) PropertyNamingPolicy
        // so the same default-options deserializer round-trips. If a future
        // edit flips PropertyNamingPolicy to camelCase, this test fails
        // loudly — pinning the on-wire shape that QuestionRecoverySweep
        // and TelegramMessageSender.SendQuestionAsync depend on.
        var rehydrated = JsonSerializer.Deserialize<AgentQuestionEnvelope>(enqueued.SourceEnvelopeJson!);
        rehydrated.Should().NotBeNull();
        rehydrated!.ProposedDefaultActionId.Should().Be("yes");
        rehydrated.Question.QuestionId.Should().Be("q-roundtrip-1");
        rehydrated.Question.AgentId.Should().Be("rt-agent");
        rehydrated.Question.CorrelationId.Should().Be("trace-rt-1");
        rehydrated.Question.TaskId.Should().Be("task-rt");
        rehydrated.Question.Title.Should().Be("Round-trip pin");
        rehydrated.Question.Severity.Should().Be(MessageSeverity.High);
        rehydrated.Question.AllowedActions.Should().HaveCount(2);
        rehydrated.Question.AllowedActions[0].ActionId.Should().Be("yes");
        rehydrated.Question.AllowedActions[1].ActionId.Should().Be("no");
        rehydrated.RoutingMetadata.Should().ContainKey(TelegramMessengerConnector.TelegramChatIdMetadataKey)
            .WhoseValue.Should().Be("7777");
        rehydrated.RoutingMetadata.Should().ContainKey("TenantId")
            .WhoseValue.Should().Be("tenant-A");
    }

    [Fact]
    public async Task SendMessageAsync_Alert_SourceEnvelopeJson_RoundTripsToMessengerMessage()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var alert = BuildMessage(
            agentId: "monitor-rt",
            correlationId: "trace-alert-rt-1",
            text: "disk usage 98%",
            severity: MessageSeverity.Critical,
            extraMetadata: new Dictionary<string, string>
            {
                [TelegramMessengerConnector.SourceTypeMetadataKey] = "Alert",
                [TelegramMessengerConnector.AlertIdMetadataKey] = "alrt-rt-1",
            });

        await connector.SendMessageAsync(alert, CancellationToken.None);

        var enqueued = queue.Enqueued.Single();
        enqueued.SourceEnvelopeJson.Should().NotBeNullOrWhiteSpace();

        // Architecture.md §3.1: Alert preserves the source envelope so
        // dead-letter replay and audit can reconstruct the original
        // alert. At the connector boundary the envelope is the inbound
        // MessengerMessage projection, so the round-trip target type
        // is MessengerMessage itself.
        var rehydrated = JsonSerializer.Deserialize<MessengerMessage>(enqueued.SourceEnvelopeJson!);
        rehydrated.Should().NotBeNull();
        rehydrated!.AgentId.Should().Be("monitor-rt");
        rehydrated.CorrelationId.Should().Be("trace-alert-rt-1");
        rehydrated.Text.Should().Be("disk usage 98%");
        rehydrated.Severity.Should().Be(MessageSeverity.Critical);
    }

    // ============================================================
    // Severity propagation — connector copies MessengerMessage.Severity
    // through verbatim so the outbound queue / sender / audit see the
    // same severity the caller emitted (architecture.md §3.1 Severity
    // column).
    // ============================================================

    [Theory]
    [InlineData(MessageSeverity.Low)]
    [InlineData(MessageSeverity.Normal)]
    [InlineData(MessageSeverity.High)]
    [InlineData(MessageSeverity.Critical)]
    public async Task SendMessageAsync_AllSeverities_PropagateVerbatim(MessageSeverity severity)
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-sev",
                correlationId: $"trace-sev-{severity}",
                text: "body",
                severity: severity),
            CancellationToken.None);

        queue.Enqueued.Single().Severity.Should().Be(
            severity,
            "the connector must preserve caller-supplied severity verbatim — otherwise the Stage 4.1 sender / audit / alerting layers see a different severity than the producer emitted");
    }

    [Theory]
    [InlineData(MessageSeverity.Low)]
    [InlineData(MessageSeverity.Normal)]
    [InlineData(MessageSeverity.High)]
    [InlineData(MessageSeverity.Critical)]
    public async Task SendQuestionAsync_AllSeverities_PropagateFromQuestion(MessageSeverity severity)
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        var envelope = BuildQuestionEnvelope(
            agentId: "agent-qsev",
            questionId: $"q-sev-{severity}",
            severity: severity);

        await connector.SendQuestionAsync(envelope, CancellationToken.None);

        queue.Enqueued.Single().Severity.Should().Be(
            severity,
            "questions carry their own severity (AgentQuestion.Severity) which the connector must copy onto OutboundMessage.Severity for downstream rendering and audit");
    }

    // ============================================================
    // CreatedAt uses the injected TimeProvider — pins that the
    // connector is deterministic under FakeTimeProvider so reliability
    // tests (retry/backoff) don't drift against wall clock.
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_CreatedAt_ReadsFromInjectedTimeProvider()
    {
        var queue = new RecordingOutboundQueue();
        var fixedNow = DateTimeOffset.Parse("2026-05-16T05:50:00Z");
        var connector = BuildConnector(queue, fixedNow);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-time",
                correlationId: "trace-time-1",
                text: "body"),
            CancellationToken.None);

        queue.Enqueued.Single().CreatedAt.Should().Be(
            fixedNow,
            "the connector must read the wall clock through the injected TimeProvider so unit/integration tests can pin deterministic timestamps via FakeTimeProvider");
    }

    // ============================================================
    // CorrelationId propagation — story acceptance criterion
    // "All messages include trace/correlation ID." This pins that
    // the caller-supplied trace id reaches OutboundMessage verbatim
    // for every SourceType the connector enqueues, so the durable
    // outbox row and downstream audit (Stage 5.2) see the same id
    // the agent producer emitted.
    // ============================================================

    [Theory]
    [InlineData("StatusUpdate")]
    [InlineData("CommandAck")]
    public async Task SendMessageAsync_NonAlert_PropagatesCorrelationIdVerbatim(string sourceTypeMetadata)
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-corr",
                correlationId: "trace-propagate-non-alert",
                text: "body",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = sourceTypeMetadata,
                }),
            CancellationToken.None);

        queue.Enqueued.Single().CorrelationId.Should().Be(
            "trace-propagate-non-alert",
            "story acceptance criterion 'All messages include trace/correlation ID' requires the caller-supplied trace id to reach OutboundMessage verbatim for SourceType={0}",
            sourceTypeMetadata);
    }

    [Fact]
    public async Task SendMessageAsync_Alert_PropagatesCorrelationIdVerbatim()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-corr-alert",
                correlationId: "trace-propagate-alert",
                text: "alert body",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = "Alert",
                    [TelegramMessengerConnector.AlertIdMetadataKey] = "alrt-corr-1",
                }),
            CancellationToken.None);

        queue.Enqueued.Single().CorrelationId.Should().Be(
            "trace-propagate-alert",
            "Alert is the only SourceType whose IdempotencyKey omits CorrelationId (key is 'alert:{AgentId}:{AlertId}'); CorrelationId still propagates onto OutboundMessage.CorrelationId so the trace survives even when it isn't part of the dedup primitive");
    }

    [Fact]
    public async Task SendQuestionAsync_PropagatesQuestionCorrelationIdVerbatim()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        // Question's CorrelationId comes from AgentQuestion.CorrelationId
        // (architecture.md §3.1) — pinning that the question's own trace
        // id, not the envelope's RoutingMetadata, becomes the outbox row's
        // CorrelationId. BuildQuestionEnvelope derives the trace as
        // "trace-{questionId}".
        var envelope = BuildQuestionEnvelope(agentId: "agent-q-corr", questionId: "q-corr-1");

        await connector.SendQuestionAsync(envelope, CancellationToken.None);

        queue.Enqueued.Single().CorrelationId.Should().Be(
            "trace-q-corr-1",
            "story acceptance criterion 'All messages include trace/correlation ID' applies to questions too — the question's own CorrelationId must reach OutboundMessage.CorrelationId verbatim");
    }

    // ============================================================
    // SourceId derivation — architecture.md §3.1 mandates a
    // distinct, source-type-specific domain identifier on every
    // OutboundMessage row so the outbox can be joined back to the
    // originating envelope without re-parsing the payload:
    //   • Alert        → AlertId (from Metadata[AlertId])
    //   • StatusUpdate → CorrelationId (status updates have no
    //                    separate domain id beyond the trace)
    //   • CommandAck   → CorrelationId (ack belongs to the
    //                    correlated command)
    //   • Question     → QuestionId (the envelope's own id)
    // Pinning each one here so a future refactor of
    // BuildOutboundKey cannot silently drop or swap the field.
    // ============================================================

    [Theory]
    [InlineData("StatusUpdate", "trace-srcid-status")]
    [InlineData("CommandAck", "trace-srcid-ack")]
    public async Task SendMessageAsync_NonAlert_SetsSourceIdToCorrelationId(
        string sourceTypeMetadata,
        string correlationId)
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-srcid",
                correlationId: correlationId,
                text: "body",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = sourceTypeMetadata,
                }),
            CancellationToken.None);

        queue.Enqueued.Single().SourceId.Should().Be(
            correlationId,
            "{0} has no domain id beyond the trace, so SourceId must mirror CorrelationId per architecture.md §3.1",
            sourceTypeMetadata);
    }

    [Fact]
    public async Task SendMessageAsync_Alert_SetsSourceIdToAlertId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        await connector.SendMessageAsync(
            BuildMessage(
                agentId: "agent-srcid-alert",
                correlationId: "trace-srcid-alert",
                text: "alert body",
                extraMetadata: new Dictionary<string, string>
                {
                    [TelegramMessengerConnector.SourceTypeMetadataKey] = "Alert",
                    [TelegramMessengerConnector.AlertIdMetadataKey] = "alrt-srcid-7",
                }),
            CancellationToken.None);

        queue.Enqueued.Single().SourceId.Should().Be(
            "alrt-srcid-7",
            "Alert.SourceId must be the AlertId (not the CorrelationId) per architecture.md §3.1 so the outbox can be joined back to the originating alert without re-parsing the payload");
    }

    [Fact]
    public async Task SendQuestionAsync_SetsSourceIdToQuestionId()
    {
        var queue = new RecordingOutboundQueue();
        var connector = BuildConnector(queue);

        // Brief: "SendQuestionAsync ... SourceId=QuestionId" — pinning
        // that the envelope's own QuestionId, not its CorrelationId
        // (which is "trace-{questionId}" here) or AgentId, becomes
        // OutboundMessage.SourceId so the question recovery sweep
        // (Stage 2.5 Gap-B) can rehydrate by QuestionId alone.
        var envelope = BuildQuestionEnvelope(agentId: "agent-srcid-q", questionId: "q-srcid-9");

        await connector.SendQuestionAsync(envelope, CancellationToken.None);

        queue.Enqueued.Single().SourceId.Should().Be(
            "q-srcid-9",
            "the brief step for SendQuestionAsync requires SourceId=QuestionId verbatim; this pins the connector against a refactor that switches to CorrelationId or AgentId");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static MessengerMessage BuildMessage(
        string agentId,
        string correlationId,
        string text,
        MessageSeverity severity = MessageSeverity.Normal,
        IReadOnlyDictionary<string, string>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "42",
        };
        if (extraMetadata is not null)
        {
            foreach (var kv in extraMetadata)
            {
                metadata[kv.Key] = kv.Value;
            }
        }

        return new MessengerMessage
        {
            MessageId = $"msg-{correlationId}",
            CorrelationId = correlationId,
            ConversationId = "conv-1",
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow,
            Text = text,
            Severity = severity,
            Metadata = metadata,
        };
    }

    private static AgentQuestionEnvelope BuildQuestionEnvelope(
        string agentId,
        string questionId,
        MessageSeverity severity = MessageSeverity.Normal)
    {
        var question = new AgentQuestion
        {
            QuestionId = questionId,
            CorrelationId = $"trace-{questionId}",
            AgentId = agentId,
            TaskId = "task-1",
            Title = "Pin matrix",
            Body = "body",
            Severity = severity,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "yes" },
            },
        };

        return new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = "approve",
            RoutingMetadata = new Dictionary<string, string>
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = "12345",
            },
        };
    }

    private static TelegramMessengerConnector BuildConnector(RecordingOutboundQueue queue)
        => BuildConnector(queue, DateTimeOffset.Parse("2026-05-16T05:50:00Z"));

    private static TelegramMessengerConnector BuildConnector(RecordingOutboundQueue queue, DateTimeOffset fixedNow)
        => new(
            queue,
            new ProcessedMessengerEventChannel(),
            new FakeTimeProvider(fixedNow),
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

        public Task DeadLetterAsync(Guid messageId, CancellationToken ct)
            => Task.CompletedTask;
    }
}
