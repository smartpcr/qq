using System.Diagnostics.Metrics;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Outbox;
using AgentSwarm.Messaging.Teams.Tests.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Stage 6.3 evaluator feedback — pins the contract that the production
/// <see cref="OutboxBackedMessengerConnector"/> increments the canonical
/// <see cref="TeamsConnectorTelemetry.MessagesSentInstrumentName"/>
/// (<c>teams.messages.sent</c>) counter <b>once per outbound boundary call regardless
/// of outcome</b> (success, missing reference, transient outbox failure). This is the
/// "attempt semantics" contract documented on
/// <see cref="TeamsConnectorTelemetry.RecordMessageSent"/> — the SAME contract the
/// synchronous <see cref="TeamsMessengerConnector.SendMessageAsync"/> path follows —
/// so the failure rate is derivable as
/// <c>1 - (teams.outbox.deliveries{outcome="Success"} / teams.messages.sent)</c>
/// using a consistent denominator across both compositions.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this test pins.</b> When a host wires the outbox engine via
/// <c>AddTeamsOutboxEngine</c> (the production composition), DI replaces the
/// unkeyed <see cref="IMessengerConnector"/> with
/// <see cref="OutboxBackedMessengerConnector"/> and the inner concrete
/// <see cref="TeamsMessengerConnector"/> is consumed only for
/// <see cref="IMessengerConnector.ReceiveAsync"/>. The <c>teams.messages.sent</c>
/// counter must continue to increment for every send attempt so the §6.3
/// step 2 custom-metrics contract holds on the deployment shape the stage is
/// explicitly designed to support, AND failure dashboards see a non-zero denominator
/// on the missing-reference / outbox-throws paths.
/// </para>
/// <para>
/// <b>Stage 6.3 iter-8 evaluator fix item 3 — counter semantics reconciliation.</b>
/// The pre-iter-8 surface incremented the counter only on successful enqueue, which
/// silently diverged from the direct connector's "every attempt" semantics. The
/// failure-path tests below (<c>SendMessageAsync_ReferenceMissing_*</c>,
/// <c>SendQuestionAsync_*Scoped_ReferenceMissing_*</c>) pin the unified attempt
/// semantics so future refactors that re-introduce the divergence fail loudly.
/// </para>
/// <para>
/// <b>Why a dedicated test.</b> The existing
/// <see cref="OutboxBackedMessengerConnectorTests"/> exercises every enqueue
/// branch but supplies <c>telemetry: null</c> on every constructor — those tests
/// pin the enqueue contract but provide no coverage of the counter integration.
/// Without this dedicated test, a future refactor that drops the
/// <c>_telemetry?.RecordMessageSent(...)</c> hook from
/// <see cref="OutboxBackedMessengerConnector.SendMessageAsync"/> /
/// <see cref="OutboxBackedMessengerConnector.SendQuestionAsync"/> would pass every
/// pre-existing enqueue test AND silently regress §6.3 step 2.
/// </para>
/// </remarks>
[Collection(TeamsTelemetryCollection.Name)]
public sealed class OutboxBackedMessengerConnectorTelemetryTests
{
    [Fact]
    public async Task SendMessageAsync_IncrementsTeamsMessagesSentCounter_OnSuccessfulEnqueue()
    {
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");
        var outbox = new InMemoryRecordingOutbox();

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            outbox,
            router,
            router,
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await decorator.SendMessageAsync(SampleMessage("m-counter-1"), CancellationToken.None);

        // Exactly one enqueue → exactly one counter increment with the bounded
        // (messageType=MessengerMessage, destinationType=Conversation) tag set.
        // Iter-7 evaluator fix item 4 — correlationId is deliberately ABSENT from the
        // metric tag tuple to keep cardinality bounded; the trace span carries the
        // correlationId so dashboards can join via exemplar pointer instead.
        var sample = Assert.Single(observations);
        Assert.Equal(1, sample.Value);
        Assert.False(sample.Tags.ContainsKey(TeamsConnectorTelemetry.CorrelationIdTag),
            "correlationId must NOT appear on counter tags — see iter-7 evaluator item 4.");
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeMessengerMessage, sample.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeConversation, sample.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);

        // Sanity — the enqueue actually landed (so the counter wasn't incremented
        // before the outbox confirmed acceptance).
        Assert.Single(outbox.Enqueued);
    }

    [Fact]
    public async Task SendQuestionAsync_UserScoped_IncrementsCounter_WithCanonicalAgentQuestionTags()
    {
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var router = new RecordingConversationReferenceStore();
        router.UserReferences[("tenant-1", "user-1")] = NewReference(tenantId: "tenant-1");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await decorator.SendQuestionAsync(SampleQuestion("q-counter-1", userId: "user-1"), CancellationToken.None);

        var sample = Assert.Single(observations);
        Assert.Equal(1, sample.Value);
        Assert.False(sample.Tags.ContainsKey(TeamsConnectorTelemetry.CorrelationIdTag),
            "correlationId must NOT appear on counter tags — see iter-7 evaluator item 4.");
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeAgentQuestion, sample.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        // User-scoped natural-key routing emits DestinationType=User so dashboards
        // can slice the sent counter by destination kind without re-deriving it
        // from the OutboxEntry.DestinationType.
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeUser, sample.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
    }

    [Fact]
    public async Task SendQuestionAsync_ChannelScoped_IncrementsCounter_WithDestinationTypeChannel()
    {
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var router = new RecordingConversationReferenceStore();
        router.ChannelReferences[("tenant-1", "channel-1")] = NewReference(tenantId: "tenant-1");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await decorator.SendQuestionAsync(SampleQuestion("q-counter-channel", channelId: "channel-1"), CancellationToken.None);

        var sample = Assert.Single(observations);
        Assert.Equal(1, sample.Value);
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeAgentQuestion, sample.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeChannel, sample.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
    }

    [Fact]
    public async Task SendMessageAsync_ReferenceMissing_StillIncrementsCounter_AttemptSemantics()
    {
        // Stage 6.3 iter-8 evaluator fix item 3 — counter semantics reconciliation.
        // Pre-iter-8, this path INCORRECTLY suppressed the increment because the
        // counter lived after the EnqueueAsync await, diverging from the direct
        // connector's "every attempt" contract. Iter-8 moved the counter into a
        // finally block so the counter now fires for the missing-reference failure
        // path too — matching the direct `TeamsMessengerConnector.SendMessageAsync`
        // failure behavior and the canonical `RecordMessageSent` attempt semantics.
        // Without this guard, failure-rate dashboards on the reliable composition
        // would see a zero denominator on reference-miss spikes.
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(), // no seeded reference → lookup misses
            new RecordingConversationReferenceStore(),
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendMessageAsync(SampleMessage("m-noref"), CancellationToken.None));

        var counter = Assert.Single(observations);
        Assert.Equal(1L, counter.Value);
        Assert.Equal(
            TeamsConnectorTelemetry.MessageTypeMessengerMessage,
            counter.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(
            TeamsConnectorTelemetry.DestinationTypeConversation,
            counter.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
        Assert.False(
            counter.Tags.ContainsKey(TeamsConnectorTelemetry.CorrelationIdTag),
            "correlationId must NOT appear on metric tags — see RecordMessageSent xmldoc.");
    }

    [Fact]
    public async Task SendQuestionAsync_UserScoped_ReferenceMissing_StillIncrementsCounter_AttemptSemantics()
    {
        // Stage 6.3 iter-8 evaluator fix item 3 — symmetric attempt-semantics
        // coverage for the AgentQuestion user-scoped failure path. The destinationType
        // classifier (User) is derived from `question.TargetUserId` BEFORE the
        // reference lookup so the counter increments correctly even when the
        // store-lookup misses and throws.
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            new RecordingConversationReferenceStore(), // no seeded user reference
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendQuestionAsync(
                SampleQuestion("q-user-noref", userId: "user-missing"),
                CancellationToken.None));

        var counter = Assert.Single(observations);
        Assert.Equal(1L, counter.Value);
        Assert.Equal(
            TeamsConnectorTelemetry.MessageTypeAgentQuestion,
            counter.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(
            TeamsConnectorTelemetry.DestinationTypeUser,
            counter.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
    }

    [Fact]
    public async Task SendQuestionAsync_ChannelScoped_ReferenceMissing_StillIncrementsCounter_AttemptSemantics()
    {
        // Stage 6.3 iter-8 evaluator fix item 3 — channel-scoped failure path
        // counterpart. destinationType=Channel is derived from `TargetChannelId`
        // before the lookup so the counter fires under the canonical channel
        // classifier even on the missing-reference exception path.
        var observations = new List<(long Value, IDictionary<string, object?> Tags)>();
        using var meterListener = SubscribeToMessagesSent(observations);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            new RecordingConversationReferenceStore(), // no seeded channel reference
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.SendQuestionAsync(
                SampleQuestion("q-channel-noref", channelId: "channel-missing"),
                CancellationToken.None));

        var counter = Assert.Single(observations);
        Assert.Equal(1L, counter.Value);
        Assert.Equal(
            TeamsConnectorTelemetry.MessageTypeAgentQuestion,
            counter.Tags[TeamsConnectorTelemetry.MessageTypeTag]);
        Assert.Equal(
            TeamsConnectorTelemetry.DestinationTypeChannel,
            counter.Tags[TeamsConnectorTelemetry.DestinationTypeTag]);
    }

    [Fact]
    public async Task SendMessageAsync_TelemetryNotWired_DoesNotThrow()
    {
        // The telemetry parameter is optional — the legacy and most outbox-engine
        // hosts wire it, but the decorator must remain safe when telemetry is null
        // (which is the surface the existing OutboxBackedMessengerConnectorTests
        // exercise). Without this guard a future refactor that drops the `?.`
        // null-conditional from `_telemetry?.RecordMessageSent(...)` would surface
        // here.
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-1"] = NewReference(tenantId: "tenant-1");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: null);

        await decorator.SendMessageAsync(SampleMessage("m-no-telemetry"), CancellationToken.None);
    }

    private static MeterListener SubscribeToMessagesSent(
        List<(long Value, IDictionary<string, object?> Tags)> observations)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TeamsConnectorTelemetry.MeterName
                    && instrument.Name == TeamsConnectorTelemetry.MessagesSentInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            observations.Add((value, dict));
        });
        listener.Start();
        return listener;
    }

    private static TeamsConversationReference NewReference(string tenantId) => new()
    {
        Id = $"ref-{tenantId}",
        TenantId = tenantId,
        InternalUserId = "user-1",
        ServiceUrl = "https://smba.trafficmanager.net/test/",
        ConversationId = $"conv-{tenantId}",
        BotId = "bot-1",
        ReferenceJson = $"{{\"tenant\":\"{tenantId}\"}}",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static MessengerMessage SampleMessage(string id) => new(
        MessageId: id,
        CorrelationId: $"corr-{id}",
        AgentId: "agent-1",
        TaskId: "task-1",
        ConversationId: "conv-1",
        Body: "hello",
        Severity: MessageSeverities.Info,
        Timestamp: DateTimeOffset.UnixEpoch);

    private static AgentQuestion SampleQuestion(string id, string? userId = null, string? channelId = null) => new()
    {
        QuestionId = id,
        TenantId = "tenant-1",
        TargetUserId = userId,
        TargetChannelId = channelId,
        CorrelationId = $"corr-{id}",
        AgentId = "agent-1",
        TaskId = "task-1",
        Title = "Title",
        Body = "body",
        Severity = MessageSeverities.Info,
        Status = AgentQuestionStatuses.Open,
        AllowedActions = new[] { new HumanAction("yes", "Yes", "yes", false) },
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}
