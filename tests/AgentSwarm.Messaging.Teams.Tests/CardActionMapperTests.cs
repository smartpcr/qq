using AgentSwarm.Messaging.Teams.Cards;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies implementation-plan §3.1 step 6 (<see cref="CardActionMapper"/>) and the third
/// test scenario from the plan: "Given an Adaptive Card <c>Action.Submit</c> payload with
/// <c>questionId</c>, <c>actionValue</c>, and <c>comment</c>, When mapped by
/// <see cref="CardActionMapper"/>, Then the resulting <c>HumanDecisionEvent</c> has all
/// fields correctly populated."
/// </summary>
public sealed class CardActionMapperTests
{
    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse(
        "2025-05-01T10:15:30Z", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Card-action round-trip — the canonical Stage 3.1 test scenario 3, extended
    /// with the architecture §2.10 <c>ActionId</c> field that <see cref="CardActionMapper"/>
    /// also requires on every inbound payload.
    /// </summary>
    [Fact]
    public void Map_PayloadWithQuestionIdActionIdActionValueAndComment_ReturnsFullyPopulatedHumanDecisionEvent()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q-mapper-001",
            [CardActionDataKeys.ActionId] = "reject-action",
            [CardActionDataKeys.ActionValue] = "reject",
            [CardActionDataKeys.CorrelationId] = "corr-mapper-001",
            [CardActionDataKeys.Comment] = "Insufficient test coverage",
        };

        var mapper = new CardActionMapper();
        var decision = mapper.Map(
            payload,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-555",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-mapper-001", decision.QuestionId);
        Assert.Equal("reject", decision.ActionValue);
        Assert.Equal("Insufficient test coverage", decision.Comment);
        Assert.Equal("Teams", decision.Messenger);
        Assert.Equal("aad-alice", decision.ExternalUserId);
        Assert.Equal("act-555", decision.ExternalMessageId);
        Assert.Equal(ReceivedAt, decision.ReceivedAt);
        Assert.Equal("corr-mapper-001", decision.CorrelationId);
    }

    [Fact]
    public void Map_PayloadWithoutComment_ReturnsHumanDecisionEventWithNullComment()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q-mapper-002",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr-mapper-002",
        };

        var decision = new CardActionMapper().Map(
            payload,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-556",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-mapper-002", decision.QuestionId);
        Assert.Equal("approve", decision.ActionValue);
        Assert.Null(decision.Comment);
    }

    /// <summary>
    /// When a comment Input.Text was rendered on the card but the user did not type
    /// anything, Activity.Value still carries the key with an empty string. The mapper
    /// normalises the empty value to null so downstream consumers can treat
    /// "no comment" uniformly.
    /// </summary>
    [Fact]
    public void Map_PayloadWithEmptyComment_NormalisesToNullOnHumanDecisionEvent()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q-mapper-003",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr-mapper-003",
            [CardActionDataKeys.Comment] = string.Empty,
        };

        var decision = new CardActionMapper().Map(
            payload,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-557",
            receivedAt: ReceivedAt);

        Assert.Null(decision.Comment);
    }

    /// <summary>
    /// Bot Framework deserialises the inbound activity body using Newtonsoft.Json; tests
    /// validate the typed object branch as well so the mapper is robust to either shape.
    /// </summary>
    [Fact]
    public void Map_PayloadAsTypedObject_RoundTripsThroughJObjectFromObject()
    {
        var payload = new
        {
            questionId = "Q-mapper-004",
            actionId = "approve-action",
            actionValue = "approve",
            correlationId = "corr-mapper-004",
            comment = "Approved with note",
        };

        var decision = new CardActionMapper().Map(
            payload,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-558",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-mapper-004", decision.QuestionId);
        Assert.Equal("approve", decision.ActionValue);
        Assert.Equal("Approved with note", decision.Comment);
        Assert.Equal("corr-mapper-004", decision.CorrelationId);
    }

    /// <summary>
    /// The exact outbound shape <see cref="AdaptiveCardBuilder"/> emits is a
    /// <see cref="Dictionary{TKey, TValue}"/> with string keys and string values. The
    /// mapper must accept that shape too — it boxes through <see cref="JObject.FromObject"/>.
    /// </summary>
    [Fact]
    public void Map_PayloadAsStringDictionary_RoundTripsThroughJObjectFromObject()
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CardActionDataKeys.QuestionId] = "Q-mapper-dict",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr-mapper-dict",
        };

        var decision = new CardActionMapper().Map(
            payload,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-dict",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-mapper-dict", decision.QuestionId);
        Assert.Equal("approve", decision.ActionValue);
        Assert.Equal("corr-mapper-dict", decision.CorrelationId);
        Assert.Null(decision.Comment);
    }

    [Fact]
    public void Map_MissingQuestionId_ThrowsInvalidOperationException()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CardActionMapper().Map(payload, "Teams", "aad-alice", "act-id", ReceivedAt));

        Assert.Contains(CardActionDataKeys.QuestionId, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The architecture §2.10 contract requires <c>ActionId</c> on every inbound payload
    /// so Stage 3.3's <c>CardActionHandler</c> can resolve the originating button
    /// unambiguously. The mapper enforces that contract at the gateway boundary.
    /// </summary>
    [Fact]
    public void Map_MissingActionId_ThrowsInvalidOperationException()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CardActionMapper().Map(payload, "Teams", "aad-alice", "act-id", ReceivedAt));

        Assert.Contains(CardActionDataKeys.ActionId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_MissingActionValue_ThrowsInvalidOperationException()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.CorrelationId] = "corr",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CardActionMapper().Map(payload, "Teams", "aad-alice", "act-id", ReceivedAt));

        Assert.Contains(CardActionDataKeys.ActionValue, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_MissingCorrelationId_ThrowsInvalidOperationException()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CardActionMapper().Map(payload, "Teams", "aad-alice", "act-id", ReceivedAt));

        Assert.Contains(CardActionDataKeys.CorrelationId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_NullPayload_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CardActionMapper().Map(null!, "Teams", "aad-alice", "act-id", ReceivedAt));
    }

    [Theory]
    [InlineData("", "aad-alice", "act-id")]
    [InlineData("Teams", "", "act-id")]
    [InlineData("Teams", "aad-alice", "")]
    public void Map_BlankRequiredArgument_ThrowsArgumentNullException(
        string messenger,
        string externalUserId,
        string externalMessageId)
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q",
            [CardActionDataKeys.ActionId] = "approve-action",
            [CardActionDataKeys.ActionValue] = "approve",
            [CardActionDataKeys.CorrelationId] = "corr",
        };

        Assert.Throws<ArgumentNullException>(() =>
            new CardActionMapper().Map(payload, messenger, externalUserId, externalMessageId, ReceivedAt));
    }

    [Fact]
    public void ReadPayload_ReturnsTypedCardActionPayload()
    {
        var payload = new JObject
        {
            [CardActionDataKeys.QuestionId] = "Q-rp",
            [CardActionDataKeys.ActionId] = "reject-action",
            [CardActionDataKeys.ActionValue] = "reject",
            [CardActionDataKeys.CorrelationId] = "corr-rp",
            [CardActionDataKeys.Comment] = "Reason",
        };

        var result = new CardActionMapper().ReadPayload(payload);

        Assert.Equal("Q-rp", result.QuestionId);
        Assert.Equal("reject-action", result.ActionId);
        Assert.Equal("reject", result.ActionValue);
        Assert.Equal("corr-rp", result.CorrelationId);
        Assert.Equal("Reason", result.Comment);
    }

    /// <summary>
    /// End-to-end round-trip: render a question card via <see cref="AdaptiveCardBuilder"/>,
    /// pluck the data dictionary out of the rendered <c>Action.Submit</c>, simulate the
    /// inbound activity by merging the user's <c>comment</c> input, and verify the mapper
    /// reconstructs the exact <c>HumanDecisionEvent</c> the renderer originally encoded.
    /// </summary>
    [Fact]
    public void RenderThenMap_RoundTripPayload_ProducesHumanDecisionEventWithRenderedFields()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = new AgentSwarm.Messaging.Abstractions.AgentQuestion
        {
            QuestionId = "Q-roundtrip-1",
            AgentId = "agent-build",
            TaskId = "task-42",
            TenantId = "contoso",
            TargetUserId = "internal-alice",
            Title = "Promote?",
            Body = "Promote to staging.",
            Severity = AgentSwarm.Messaging.Abstractions.MessageSeverities.Info,
            AllowedActions = new[]
            {
                new AgentSwarm.Messaging.Abstractions.HumanAction("approve-action", "Approve", "approve", false),
                new AgentSwarm.Messaging.Abstractions.HumanAction("reject-action", "Reject", "reject", true),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = "corr-roundtrip-1",
        };

        var attachment = renderer.RenderQuestionCard(question);
        var card = (JObject)attachment.Content;
        var rejectAction = ((JArray)card["actions"]!)
            .Single(a => (string?)a["title"] == "Reject");
        var rejectData = (JObject)rejectAction["data"]!;

        // The renderer must already have stamped the architecture-required ActionId,
        // QuestionId, ActionValue, and CorrelationId on the data dict — otherwise the
        // mapper cannot reconstruct a HumanDecisionEvent without help.
        Assert.Equal("reject-action", (string?)rejectData[CardActionDataKeys.ActionId]);

        // Simulate the inbound activity — Bot Framework merges the Input.Text values
        // (keyed by Input.Id) into the action's Data payload before posting back as
        // Activity.Value.
        rejectData[CardActionDataKeys.Comment] = "Insufficient test coverage";

        var decision = new CardActionMapper().Map(
            rejectData,
            messenger: "Teams",
            externalUserId: "aad-alice",
            externalMessageId: "act-roundtrip",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-roundtrip-1", decision.QuestionId);
        Assert.Equal("reject", decision.ActionValue);
        Assert.Equal("corr-roundtrip-1", decision.CorrelationId);
        Assert.Equal("Insufficient test coverage", decision.Comment);
        Assert.Equal("aad-alice", decision.ExternalUserId);
        Assert.Equal("act-roundtrip", decision.ExternalMessageId);
        Assert.Equal(ReceivedAt, decision.ReceivedAt);

        // ReadPayload also surfaces the ActionId — the contract Stage 3.3 consumes.
        var typedPayload = new CardActionMapper().ReadPayload(rejectData);
        Assert.Equal("reject-action", typedPayload.ActionId);
        Assert.Equal("reject", typedPayload.ActionValue);
    }

    /// <summary>
    /// Cross-component round-trip for the incident-escalation card (impl-plan §3.1 step 4
    /// — Escalate / Acknowledge buttons). Extends the Stage 3.1 brief's "Card action
    /// round-trip" test scenario from question cards to incident cards: render the card
    /// via <see cref="AdaptiveCardBuilder.RenderIncidentCard"/>, pluck the chosen action's
    /// data dict out of the rendered JSON, and feed it through
    /// <see cref="CardActionMapper.Map"/>. The resulting <c>HumanDecisionEvent</c> must
    /// carry the per-acknowledger <c>QuestionId</c>, the canonical action value, and the
    /// shared correlation id — proving the renderer and mapper agree on the payload
    /// contract for incident cards too.
    /// </summary>
    [Theory]
    [InlineData("Escalate", "escalate", "incident.escalate")]
    [InlineData("Acknowledge", "acknowledge", "incident.acknowledge")]
    public void RenderIncidentThenMap_RoundTripPayload_ProducesHumanDecisionEventForChosenButton(
        string buttonTitle,
        string expectedActionValue,
        string expectedActionId)
    {
        var renderer = new AdaptiveCardBuilder();
        var incident = new IncidentSummary(
            IncidentId: "inc-cross-roundtrip",
            QuestionId: "Q-inc-roundtrip-alice",
            TaskId: "task-77",
            AgentId: "release-agent-01",
            AffectedAgents: new[] { "release-agent-01", "deploy-agent-03" },
            Severity: AgentSwarm.Messaging.Abstractions.MessageSeverities.Critical,
            Title: "Production deploy failed",
            Description: "Rollback initiated due to schema migration error.",
            OccurredAt: DateTimeOffset.Parse("2025-02-01T03:14:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-incident-cross-roundtrip");

        var attachment = renderer.RenderIncidentCard(incident);
        var card = (JObject)attachment.Content;
        var action = ((JArray)card["actions"]!)
            .Single(a => (string?)a["title"] == buttonTitle);
        var data = (JObject)action["data"]!;

        // The renderer must have stamped the per-acknowledger QuestionId — NOT the IncidentId.
        Assert.Equal("Q-inc-roundtrip-alice", (string?)data[CardActionDataKeys.QuestionId]);
        Assert.Equal(expectedActionId, (string?)data[CardActionDataKeys.ActionId]);
        Assert.Equal(expectedActionValue, (string?)data[CardActionDataKeys.ActionValue]);
        Assert.Equal("corr-incident-cross-roundtrip", (string?)data[CardActionDataKeys.CorrelationId]);

        var decision = new CardActionMapper().Map(
            data,
            messenger: "Teams",
            externalUserId: "aad-acknowledger",
            externalMessageId: "act-incident-roundtrip",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-inc-roundtrip-alice", decision.QuestionId);
        Assert.Equal(expectedActionValue, decision.ActionValue);
        Assert.Equal("corr-incident-cross-roundtrip", decision.CorrelationId);
        Assert.Null(decision.Comment); // incident cards don't render a comment input
        Assert.Equal("aad-acknowledger", decision.ExternalUserId);
        Assert.Equal("Teams", decision.Messenger);

        // ReadPayload must surface the same ActionId the renderer stamped — Stage 3.3's
        // CardActionHandler uses this to resolve the originating button on the stored
        // per-acknowledger AgentQuestion (per architecture.md §6.3 step 4).
        var typedPayload = new CardActionMapper().ReadPayload(data);
        Assert.Equal(expectedActionId, typedPayload.ActionId);
        Assert.Equal(expectedActionValue, typedPayload.ActionValue);
    }

    /// <summary>
    /// Cross-component round-trip for the release-gate card (impl-plan §3.1 step 5 —
    /// Approve / Reject / Defer buttons; architecture.md §6.3.1 multi-approver modeling).
    /// Each of the three release-gate buttons must round-trip through the mapper to a
    /// <c>HumanDecisionEvent</c> carrying the <i>per-approver</i> <c>QuestionId</c> from
    /// the <see cref="ReleaseGateRequest"/> (NOT the shared <c>GateId</c>) so Stage 3.3's
    /// <c>CardActionHandler</c> can run the standard single-decision first-writer-wins
    /// lifecycle independently for each approver. The orchestrator's workflow layer
    /// computes the "N of M" threshold rollup, NOT this code path.
    /// </summary>
    [Theory]
    [InlineData("Approve", "approve", "gate.approve")]
    [InlineData("Reject", "reject", "gate.reject")]
    [InlineData("Defer", "defer", "gate.defer")]
    public void RenderReleaseGateThenMap_RoundTripPayload_ProducesHumanDecisionEventForChosenButton(
        string buttonTitle,
        string expectedActionValue,
        string expectedActionId)
    {
        var renderer = new AdaptiveCardBuilder();
        var gate = new ReleaseGateRequest(
            GateId: "gate-cross-roundtrip",
            // Per-approver QuestionId — each approver renders their own card with their
            // own QuestionId, sharing only the gate metadata (per architecture.md §6.3.1).
            QuestionId: "Q-gate-roundtrip-alice",
            TaskId: "task-release-99",
            GateName: "Production Deploy Gate",
            ReleaseVersion: "v1.42.0",
            Environment: "Production",
            GateConditions: new[]
            {
                new ReleaseGateCondition("All tests pass", true),
                new ReleaseGateCondition("Security scan clean", true),
                new ReleaseGateCondition("SRE sign-off", false),
            },
            GateStatus: "Pending",
            Deadline: DateTimeOffset.Parse("2025-03-01T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-gate-cross-roundtrip");

        var attachment = renderer.RenderReleaseGateCard(gate);
        var card = (JObject)attachment.Content;
        var action = ((JArray)card["actions"]!)
            .Single(a => (string?)a["title"] == buttonTitle);
        var data = (JObject)action["data"]!;

        // The renderer must stamp the per-approver QuestionId, NOT the shared GateId.
        Assert.Equal("Q-gate-roundtrip-alice", (string?)data[CardActionDataKeys.QuestionId]);
        Assert.NotEqual("gate-cross-roundtrip", (string?)data[CardActionDataKeys.QuestionId]);
        Assert.Equal(expectedActionId, (string?)data[CardActionDataKeys.ActionId]);
        Assert.Equal(expectedActionValue, (string?)data[CardActionDataKeys.ActionValue]);
        Assert.Equal("corr-gate-cross-roundtrip", (string?)data[CardActionDataKeys.CorrelationId]);

        var decision = new CardActionMapper().Map(
            data,
            messenger: "Teams",
            externalUserId: "aad-approver",
            externalMessageId: "act-gate-roundtrip",
            receivedAt: ReceivedAt);

        Assert.Equal("Q-gate-roundtrip-alice", decision.QuestionId);
        Assert.Equal(expectedActionValue, decision.ActionValue);
        Assert.Equal("corr-gate-cross-roundtrip", decision.CorrelationId);
        Assert.Null(decision.Comment); // release-gate cards don't render a comment input
        Assert.Equal("aad-approver", decision.ExternalUserId);
        Assert.Equal("Teams", decision.Messenger);

        var typedPayload = new CardActionMapper().ReadPayload(data);
        Assert.Equal(expectedActionId, typedPayload.ActionId);
        Assert.Equal(expectedActionValue, typedPayload.ActionValue);
    }
}
