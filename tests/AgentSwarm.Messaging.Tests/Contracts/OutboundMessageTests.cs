using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Contracts;

/// <summary>
/// Stage 1.3 test scenarios for the shared <see cref="OutboundMessage"/> envelope.
/// Pins the deterministic <see cref="OutboundMessage.IdempotencyKeys"/>
/// derivations that the persistence UNIQUE constraint depends on. The exact
/// formulas are pinned by architecture.md Section 3.2 (Idempotency key
/// derivation table).
/// </summary>
public class OutboundMessageTests
{
    /// <summary>
    /// Required test scenario from the implementation plan:
    /// "Given a Question-type OutboundMessage with AgentId 'build-agent-3' and
    /// QuestionId 'Q-42', When IdempotencyKey is computed, Then it equals
    /// 'q:build-agent-3:Q-42'".
    /// </summary>
    [Fact]
    public void IdempotencyKey_QuestionType_DerivesAsQColonAgentColonQuestionId()
    {
        var key = OutboundMessage.IdempotencyKeys.ForQuestion(
            agentId: "build-agent-3",
            questionId: "Q-42");

        key.Should().Be("q:build-agent-3:Q-42");
    }

    [Fact]
    public void IdempotencyKey_Alert_UsesArchitectureFormula()
    {
        // architecture.md Section 3.2: alert:{AgentId}:{AlertId}.
        OutboundMessage.IdempotencyKeys.ForAlert("monitor-1", "alert-77")
            .Should().Be("alert:monitor-1:alert-77");
    }

    [Fact]
    public void IdempotencyKey_StatusUpdate_UsesAgentAndCorrelationId()
    {
        // architecture.md Section 3.2: s:{AgentId}:{CorrelationId}. Status
        // updates collapse per (agent, trace) pair.
        OutboundMessage.IdempotencyKeys.ForStatusUpdate("deploy-2", "trace-def")
            .Should().Be("s:deploy-2:trace-def");
    }

    [Fact]
    public void IdempotencyKey_CommandAck_UsesCorrelationIdOnly_NoAgentSegment()
    {
        // architecture.md Section 3.2: c:{CorrelationId} (no agent segment).
        // Slash-command invocations are already globally unique by trace.
        OutboundMessage.IdempotencyKeys.ForCommandAck("trace-ghi")
            .Should().Be("c:trace-ghi");
    }

    [Fact]
    public void IdempotencyKey_IsDeterministic()
    {
        var first = OutboundMessage.IdempotencyKeys.ForQuestion("build-agent-3", "Q-42");
        var second = OutboundMessage.IdempotencyKeys.ForQuestion("build-agent-3", "Q-42");

        first.Should().Be(second);
    }

    [Theory]
    [InlineData(null, "Q-42")]
    [InlineData("", "Q-42")]
    [InlineData("   ", "Q-42")]
    [InlineData("agent-1", null)]
    [InlineData("agent-1", "")]
    [InlineData("agent-1", "   ")]
    public void IdempotencyKey_Question_NullOrWhitespaceSegment_Throws(string? agentId, string? questionId)
    {
        var act = () => OutboundMessage.IdempotencyKeys.ForQuestion(agentId!, questionId!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("bad:agent", "Q-42")]
    [InlineData("agent", "bad:question")]
    public void IdempotencyKey_Question_SegmentContainingSeparator_Throws(string agentId, string questionId)
    {
        var act = () => OutboundMessage.IdempotencyKeys.ForQuestion(agentId, questionId);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*':'*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdempotencyKey_CommandAck_NullOrWhitespace_Throws(string? correlationId)
    {
        var act = () => OutboundMessage.IdempotencyKeys.ForCommandAck(correlationId!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IdempotencyKey_CommandAck_SegmentContainingSeparator_Throws()
    {
        var act = () => OutboundMessage.IdempotencyKeys.ForCommandAck("bad:trace");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*':'*");
    }

    [Fact]
    public void DefaultMaxAttempts_IsFive()
    {
        // Pinned by architecture.md Section 10.3 (Outbound Durability).
        OutboundMessage.DefaultMaxAttempts.Should().Be(5);
    }

    [Fact]
    public void Construction_PopulatesAllFields()
    {
        var messageId = Guid.NewGuid();
        var nextRetry = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
        var created = new DateTimeOffset(2026, 5, 15, 11, 0, 0, TimeSpan.Zero);
        var sent = new DateTimeOffset(2026, 5, 15, 11, 0, 5, TimeSpan.Zero);

        var msg = new OutboundMessage(
            MessageId: messageId,
            IdempotencyKey: "q:agent-1:Q-1",
            ChatId: 123456789L,
            Severity: MessageSeverity.High,
            Status: OutboundMessageStatus.Sent,
            SourceType: OutboundMessageSource.Question,
            Payload: "{\"text\":\"hello\"}",
            SourceEnvelopeJson: "{\"Question\":{}}",
            SourceId: "Q-1",
            AttemptCount: 1,
            MaxAttempts: OutboundMessage.DefaultMaxAttempts,
            NextRetryAt: nextRetry,
            PlatformMessageId: 987654321L,
            CorrelationId: "trace-1",
            CreatedAt: created,
            SentAt: sent,
            ErrorDetail: null);

        msg.MessageId.Should().Be(messageId);
        msg.IdempotencyKey.Should().Be("q:agent-1:Q-1");
        msg.ChatId.Should().Be(123456789L);
        msg.Severity.Should().Be(MessageSeverity.High);
        msg.Status.Should().Be(OutboundMessageStatus.Sent);
        msg.SourceType.Should().Be(OutboundMessageSource.Question);
        msg.Payload.Should().Be("{\"text\":\"hello\"}");
        msg.SourceEnvelopeJson.Should().Be("{\"Question\":{}}");
        msg.SourceId.Should().Be("Q-1");
        msg.AttemptCount.Should().Be(1);
        msg.MaxAttempts.Should().Be(5);
        msg.NextRetryAt.Should().Be(nextRetry);
        msg.PlatformMessageId.Should().Be(987654321L);
        msg.CorrelationId.Should().Be("trace-1");
        msg.CreatedAt.Should().Be(created);
        msg.SentAt.Should().Be(sent);
        msg.ErrorDetail.Should().BeNull();
    }

    [Fact]
    public void Construction_PendingNeverSent_AllowsNullPlatformMessageIdAndSentAt()
    {
        var msg = new OutboundMessage(
            MessageId: Guid.NewGuid(),
            IdempotencyKey: "q:a:Q",
            ChatId: 1L,
            Severity: MessageSeverity.Normal,
            Status: OutboundMessageStatus.Pending,
            SourceType: OutboundMessageSource.Question,
            Payload: "p",
            SourceEnvelopeJson: null,
            SourceId: null,
            AttemptCount: 0,
            MaxAttempts: OutboundMessage.DefaultMaxAttempts,
            NextRetryAt: null,
            PlatformMessageId: null,
            CorrelationId: "t",
            CreatedAt: DateTimeOffset.UnixEpoch,
            SentAt: null,
            ErrorDetail: null);

        msg.PlatformMessageId.Should().BeNull();
        msg.SentAt.Should().BeNull();
        msg.NextRetryAt.Should().BeNull();
        msg.ErrorDetail.Should().BeNull();
    }

    // -----------------------------------------------------------------
    // OutboundMessage.Create factory — verifies the spec-mandated default
    // for MaxAttempts (and the other producer-friendly defaults) actually
    // takes effect when callers do not specify the retry budget. The
    // positional record cannot carry a default for MaxAttempts directly
    // (subsequent positional parameters are required-non-default), so the
    // ergonomic Create factory is the path that satisfies the
    // implementation-plan requirement "MaxAttempts (int, default 5)".
    // -----------------------------------------------------------------

    [Fact]
    public void Create_WithoutMaxAttempts_DefaultsToFive()
    {
        var msg = OutboundMessage.Create(
            idempotencyKey: "q:agent-1:Q-1",
            chatId: 42L,
            severity: MessageSeverity.Normal,
            sourceType: OutboundMessageSource.Question,
            payload: "{}",
            correlationId: "trace-x");

        msg.MaxAttempts.Should().Be(5);
        msg.MaxAttempts.Should().Be(OutboundMessage.DefaultMaxAttempts);
    }

    [Fact]
    public void Create_AppliesProducerDefaults_PendingZeroAttemptsNoRetry()
    {
        var msg = OutboundMessage.Create(
            idempotencyKey: "alert:monitor-1:alert-77",
            chatId: 99L,
            severity: MessageSeverity.High,
            sourceType: OutboundMessageSource.Alert,
            payload: "{\"embed\":{}}",
            correlationId: "trace-y");

        msg.Status.Should().Be(OutboundMessageStatus.Pending);
        msg.AttemptCount.Should().Be(0);
        msg.NextRetryAt.Should().BeNull();
        msg.PlatformMessageId.Should().BeNull();
        msg.SentAt.Should().BeNull();
        msg.ErrorDetail.Should().BeNull();
        msg.SourceEnvelopeJson.Should().BeNull();
        msg.SourceId.Should().BeNull();
        msg.MessageId.Should().NotBeEmpty();
        msg.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Create_AcceptsExplicitMaxAttemptsOverride()
    {
        var msg = OutboundMessage.Create(
            idempotencyKey: "s:agent-1:trace-1",
            chatId: 1L,
            severity: MessageSeverity.Low,
            sourceType: OutboundMessageSource.StatusUpdate,
            payload: "p",
            correlationId: "trace-1",
            maxAttempts: 10);

        msg.MaxAttempts.Should().Be(10);
    }

    [Fact]
    public void Create_PreservesExplicitMessageIdAndCreatedAt()
    {
        var fixedId = Guid.NewGuid();
        var fixedTime = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);

        var msg = OutboundMessage.Create(
            idempotencyKey: "c:trace-z",
            chatId: 1L,
            severity: MessageSeverity.Normal,
            sourceType: OutboundMessageSource.CommandAck,
            payload: "ack",
            correlationId: "trace-z",
            messageId: fixedId,
            createdAt: fixedTime);

        msg.MessageId.Should().Be(fixedId);
        msg.CreatedAt.Should().Be(fixedTime);
    }
}
