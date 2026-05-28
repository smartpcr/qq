using AdaptiveCards;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the implementation-plan §3.1 step 1–5 templates and the three explicit test
/// scenarios — approval card rendering, comment-input conditional, and the typed action
/// payload that <see cref="CardActionMapper"/> consumes on the inbound round-trip.
/// </summary>
public sealed class AdaptiveCardBuilderTests
{
    private const string Tenant = "contoso-tenant-id";
    private const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";

    /// <summary>
    /// Test scenario 1: "Given an AgentQuestion with actions Approve and Reject, When
    /// rendered by AdaptiveCardBuilder, Then the resulting JSON contains two
    /// <c>Action.Submit</c> elements with matching labels."
    /// </summary>
    [Fact]
    public void RenderQuestionCard_ApproveAndRejectActions_EmitsTwoActionSubmitElementsWithMatchingLabels()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-rendering-001",
            new HumanAction("approve", "Approve", "approve", false),
            new HumanAction("reject", "Reject", "reject", false));

        var attachment = renderer.RenderQuestionCard(question);

        Assert.Equal(AdaptiveCardContentType, attachment.ContentType);
        var card = Assert.IsType<JObject>(attachment.Content);

        var actions = card["actions"] as JArray;
        Assert.NotNull(actions);
        Assert.Equal(2, actions!.Count);

        Assert.All(actions, action =>
        {
            Assert.Equal("Action.Submit", (string?)action["type"]);
        });

        var titles = actions.Select(a => (string?)a["title"]).ToList();
        Assert.Contains("Approve", titles);
        Assert.Contains("Reject", titles);
    }

    /// <summary>
    /// Test scenario 2: "Given a <c>HumanAction</c> with <c>RequiresComment = true</c>,
    /// When the card is rendered, Then an <c>Input.Text</c> field is included adjacent
    /// to the action button." Adaptive Cards 1.5 separates inputs (placed in
    /// <c>body</c>) from actions (placed in <c>actions</c>), so "adjacent" is interpreted
    /// as "the comment input is the LAST element of the card body, immediately
    /// preceding the actions section" — the strongest visual / interaction adjacency
    /// the schema supports. The comment input must additionally name every action
    /// that requires it (impl-plan step 2: "comment input for actions where
    /// <c>RequiresComment = true</c>"), so the user knows which buttons demand a
    /// reason before submitting.
    /// </summary>
    [Fact]
    public void RenderQuestionCard_ActionRequiresComment_PlacesInputTextAdjacentToActions()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-rendering-002",
            new HumanAction("approve-action", "Approve", "approve", false),
            new HumanAction("reject-action", "Reject", "reject", true));

        var attachment = renderer.RenderQuestionCard(question);

        var card = Assert.IsType<JObject>(attachment.Content);
        var body = card["body"] as JArray;
        Assert.NotNull(body);

        // The comment Input.Text must exist with the canonical id.
        var commentInput = body!
            .FirstOrDefault(b =>
                (string?)b["type"] == "Input.Text"
                && (string?)b["id"] == CardActionDataKeys.Comment);
        Assert.NotNull(commentInput);

        // Adjacency: the comment input must be the LAST element of the body so it
        // visually borders the actions row that immediately follows. Anything trailing
        // it (another text block, another fact set) would push it away from the
        // RequiresComment action button and break the "adjacent" contract.
        Assert.Same(commentInput, body[body.Count - 1]);

        // Multi-line free text — the contract for a "reason" / "comment" field.
        Assert.True((bool?)commentInput!["isMultiline"] ?? false);

        // The label must call out the specific action that requires a comment so the
        // user knows BEFORE submitting that pressing "Reject" will demand a reason
        // (otherwise pressing the button just silently fails server-side validation).
        var label = (string?)commentInput["label"];
        Assert.NotNull(label);
        Assert.Contains("Reject", label!, StringComparison.Ordinal);

        // Round-trip sanity: pressing the Reject action with a typed comment must
        // produce a payload whose `comment` key resolves back to the same Input.Text
        // — proving the input is wired into the same data dict the action carries.
        var rejectAction = ((JArray)card["actions"]!)
            .Single(a => (string?)a["title"] == "Reject");
        var rejectData = (JObject)rejectAction["data"]!;
        rejectData[CardActionDataKeys.Comment] = "needs more tests";
        var decision = new CardActionMapper().Map(
            rejectData,
            messenger: "Teams",
            externalUserId: "aad-user",
            externalMessageId: "act-id",
            receivedAt: DateTimeOffset.UtcNow);
        Assert.Equal("reject", decision.ActionValue);
        Assert.Equal("needs more tests", decision.Comment);
    }

    /// <summary>
    /// When EVERY action requires a comment, the label must indicate the comment is
    /// universally required (no per-action enumeration), and adjacency to the actions
    /// row must still hold.
    /// </summary>
    [Fact]
    public void RenderQuestionCard_AllActionsRequireComment_UsesUniversalRequiredLabel()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-rendering-002b",
            new HumanAction("approve-action", "Approve", "approve", true),
            new HumanAction("reject-action", "Reject", "reject", true));

        var attachment = renderer.RenderQuestionCard(question);
        var card = Assert.IsType<JObject>(attachment.Content);
        var body = (JArray)card["body"]!;

        var commentInput = body
            .Single(b => (string?)b["type"] == "Input.Text"
                && (string?)b["id"] == CardActionDataKeys.Comment);
        Assert.Same(commentInput, body[body.Count - 1]);

        var label = (string?)commentInput["label"];
        Assert.NotNull(label);
        Assert.Contains("required", label!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When NO action declares <c>RequiresComment = true</c> the renderer must not emit
    /// any <c>Input.Text</c> field (the comment block is conditional, per impl-plan §3.1
    /// step 2).
    /// </summary>
    [Fact]
    public void RenderQuestionCard_NoActionRequiresComment_OmitsInputTextField()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-rendering-003",
            new HumanAction("approve", "Approve", "approve", false),
            new HumanAction("reject", "Reject", "reject", false));

        var attachment = renderer.RenderQuestionCard(question);

        var card = Assert.IsType<JObject>(attachment.Content);
        var body = card["body"] as JArray ?? new JArray();
        Assert.DoesNotContain(body, b => (string?)b["type"] == "Input.Text");
    }

    /// <summary>
    /// Every <c>Action.Submit</c> button must carry a typed data payload with the
    /// originating question ID, the <see cref="HumanAction.ActionId"/>, the
    /// <see cref="HumanAction.Value"/>, and the correlation ID — this is the contract
    /// <see cref="CardActionMapper"/> consumes on the inbound round-trip (test scenario 3,
    /// extended with the architecture §2.10 <c>ActionId</c> requirement).
    /// </summary>
    [Fact]
    public void RenderQuestionCard_SubmitDataPayload_CarriesQuestionIdActionIdActionValueAndCorrelationId()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-rendering-004",
            new HumanAction("approve-action", "Approve", "approve", false));
        question = question with { CorrelationId = "corr-rendering-004" };

        var attachment = renderer.RenderQuestionCard(question);

        var card = Assert.IsType<JObject>(attachment.Content);
        var actions = card["actions"] as JArray;
        Assert.NotNull(actions);

        var submit = Assert.Single(actions!);
        var data = submit["data"] as JObject;
        Assert.NotNull(data);
        Assert.Equal(question.QuestionId, (string?)data![CardActionDataKeys.QuestionId]);
        Assert.Equal("approve-action", (string?)data[CardActionDataKeys.ActionId]);
        Assert.Equal("approve", (string?)data[CardActionDataKeys.ActionValue]);
        Assert.Equal("corr-rendering-004", (string?)data[CardActionDataKeys.CorrelationId]);
    }

    /// <summary>
    /// Pin the Adaptive Card schema version to 1.5 per <c>tech-spec.md</c> §5.1
    /// risk R-3 ("Adaptive Card schema drift … Pin card schema version in templates").
    /// Every Render* path must emit the exact same schema version string in the JSON.
    /// </summary>
    [Fact]
    public void RenderQuestionCard_PinsAdaptiveCardSchemaVersion_To_1_5()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-schema",
            new HumanAction("approve", "Approve", "approve", false));

        var attachment = renderer.RenderQuestionCard(question);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal("1.5", (string?)card["version"]);
    }

    /// <summary>
    /// The renderer must emit exactly one shared <c>Input.Text</c> field with
    /// <c>Id == comment</c> when at least one allowed action declares
    /// <c>RequiresComment = true</c> — Bot Framework merges the typed value into every
    /// action's <c>Data</c> dict on submit, so a single shared input is the right
    /// design (per-action inputs would duplicate the field on every action button).
    /// </summary>
    [Fact]
    public void RenderQuestionCard_MultipleActionsRequireComment_EmitsExactlyOneSharedInputText()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-shared-input",
            new HumanAction("approve", "Approve", "approve", true),
            new HumanAction("reject", "Reject", "reject", true),
            new HumanAction("defer", "Defer", "defer", false));

        var attachment = renderer.RenderQuestionCard(question);

        var card = Assert.IsType<JObject>(attachment.Content);
        var body = card["body"] as JArray ?? new JArray();
        var commentInputs = body
            .Where(b => (string?)b["type"] == "Input.Text"
                && (string?)b["id"] == CardActionDataKeys.Comment)
            .ToList();

        Assert.Single(commentInputs);
    }

    [Fact]
    public void RenderQuestionCard_SeverityCritical_RendersAttentionStyledHeader()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-severity",
            new HumanAction("approve", "Approve", "approve", false));
        question = question with { Severity = MessageSeverities.Critical };

        var attachment = renderer.RenderQuestionCard(question);

        var card = Assert.IsType<JObject>(attachment.Content);
        var firstBlock = (card["body"] as JArray)?.FirstOrDefault();
        Assert.NotNull(firstBlock);
        Assert.Equal("Container", (string?)firstBlock!["type"]);
        Assert.Equal("attention", (string?)firstBlock["style"]);
    }

    [Fact]
    public void RenderQuestionCard_NullQuestion_ThrowsArgumentNullException()
    {
        var renderer = new AdaptiveCardBuilder();
        Assert.Throws<ArgumentNullException>(() => renderer.RenderQuestionCard(null!));
    }

    /// <summary>
    /// Full coverage of the <c>AdaptiveCardBuilder.SeverityToContainerStyle</c> switch
    /// expression (AdaptiveCardBuilder.cs:387–397) — the severity-to-container-style
    /// table that drives the visual treatment of every severity header on question,
    /// incident, and release-gate cards. Prior to this iteration only the
    /// <see cref="MessageSeverities.Critical"/> branch was exercised (via
    /// <c>RenderQuestionCard_SeverityCritical_RendersAttentionStyledHeader</c>), leaving
    /// the <see cref="MessageSeverities.Error"/>, <see cref="MessageSeverities.Warning"/>,
    /// <see cref="MessageSeverities.Info"/>, and <c>_ =&gt; Default</c> fallback branches
    /// uncovered. The fallback branch matters for forward-compat: if a future
    /// orchestrator emits a severity outside <see cref="MessageSeverities.All"/> (or
    /// if an older record is replayed during a migration), the card must still
    /// render with a non-explicit container style rather than throwing. We exercise
    /// this through <see cref="IAdaptiveCardRenderer.RenderIncidentCard"/> because
    /// <see cref="IncidentSummary"/> does not pre-validate <c>Severity</c>, unlike
    /// <see cref="AgentQuestion"/> which enforces <see cref="MessageSeverities.IsValid"/>
    /// in its constructor. This is the only realistic path to drive the
    /// <c>_ =&gt; Default</c> arm without violating the construction contract of
    /// <see cref="AgentQuestion"/>.
    /// </summary>
    [Theory]
    [InlineData(MessageSeverities.Critical, "attention")]
    [InlineData(MessageSeverities.Error,    "attention")]
    [InlineData(MessageSeverities.Warning,  "warning")]
    [InlineData(MessageSeverities.Info,     "emphasis")]
    [InlineData("UnknownSeverity",          null)]
    public void RenderIncidentCard_SeverityToContainerStyle_CoversFullSwitchTable(
        string severity,
        string? expectedStyle)
    {
        var renderer = new AdaptiveCardBuilder();
        var incident = new IncidentSummary(
            IncidentId: $"inc-style-{severity}",
            QuestionId: $"Q-style-{severity}",
            TaskId: "task-style-matrix",
            AgentId: "agent-style",
            AffectedAgents: new[] { "agent-style" },
            Severity: severity,
            Title: "Severity matrix probe",
            Description: "Drives SeverityToContainerStyle through its full switch table.",
            OccurredAt: DateTimeOffset.Parse(
                "2025-04-10T08:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: $"corr-style-{severity}");

        var attachment = renderer.RenderIncidentCard(incident);
        var card = Assert.IsType<JObject>(attachment.Content);

        // Severity container is always the first body element — AddSeverityHeader is
        // the first call in RenderIncidentCard.
        var firstBlock = (card["body"] as JArray)?.FirstOrDefault();
        Assert.NotNull(firstBlock);
        Assert.Equal("Container", (string?)firstBlock!["type"]);

        var actualStyle = (string?)firstBlock["style"];
        if (expectedStyle is null)
        {
            // _ => AdaptiveContainerStyle.Default: the AdaptiveCards serializer
            // omits the "style" key entirely (Default is the implicit container
            // style), so the property must be missing OR the literal "default" —
            // both shapes are valid Adaptive Card 1.5 JSON for "no override".
            Assert.True(
                actualStyle is null
                || string.Equals(actualStyle, "default", StringComparison.Ordinal),
                $"Expected unknown severity '{severity}' to map to the AdaptiveContainerStyle.Default "
                + $"fallback (missing or 'default' style), but got '{actualStyle}'.");
        }
        else
        {
            Assert.Equal(expectedStyle, actualStyle);
        }

        // Severity is also surfaced in the secondary TextBlock under the header,
        // so the unknown-severity case still renders human-readable text
        // ("Severity: UnknownSeverity") rather than swallowing the input.
        var headerContainer = (JObject)firstBlock!;
        var headerItems = (JArray)headerContainer["items"]!;
        var severityLine = headerItems
            .OfType<JObject>()
            .Where(b => (string?)b["type"] == "TextBlock")
            .Select(b => (string?)b["text"])
            .FirstOrDefault(t => t is not null && t.StartsWith("Severity:", StringComparison.Ordinal));
        Assert.Equal($"Severity: {severity}", severityLine);
    }

    /// <summary>
    /// Action-button style mapping per <see cref="AdaptiveCardBuilder"/>'s severity
    /// table: approve/acknowledge/resume render as <c>positive</c> regardless of
    /// severity, while reject/escalate/pause only escalate to <c>destructive</c> on
    /// <see cref="MessageSeverities.Critical"/> or <see cref="MessageSeverities.Error"/>;
    /// other severities leave the dominant-action button at the Adaptive Card default
    /// style. This is the UX safety affordance for impl-plan §3.1 step 2 ("severity
    /// indicator … action buttons generated from <c>HumanAction</c> list") — without it
    /// a critical-severity reject button is visually indistinguishable from an info
    /// approve. The existing severity header test only covers the container style,
    /// not the per-action button styling.
    /// </summary>
    [Theory]
    [InlineData(MessageSeverities.Critical, "approve", "positive")]
    [InlineData(MessageSeverities.Critical, "reject",  "destructive")]
    [InlineData(MessageSeverities.Error,    "reject",  "destructive")]
    [InlineData(MessageSeverities.Info,     "approve", "positive")]
    [InlineData(MessageSeverities.Info,     "reject",  null)]
    [InlineData(MessageSeverities.Warning,  "reject",  null)]
    public void RenderQuestionCard_ActionButtonStyle_FollowsSeverityActionMatrix(
        string severity,
        string actionValue,
        string? expectedStyle)
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            $"Q-style-{severity}-{actionValue}",
            new HumanAction(actionValue, char.ToUpperInvariant(actionValue[0]) + actionValue[1..], actionValue, false));
        question = question with { Severity = severity };

        var attachment = renderer.RenderQuestionCard(question);
        var card = Assert.IsType<JObject>(attachment.Content);

        var action = Assert.Single((JArray)card["actions"]!);
        var actualStyle = (string?)action["style"];
        Assert.Equal(expectedStyle, actualStyle);
    }

    [Fact]
    public void RenderStatusCard_EmitsAgentMetadataAndOmitsActionButtons()
    {
        var renderer = new AdaptiveCardBuilder();
        var status = new AgentStatusSummary(
            AgentId: "agent-build-007",
            TaskId: "task-42",
            AgentName: "Build Agent",
            CurrentState: "Working",
            ActiveTaskCount: 3,
            LastActivityAt: DateTimeOffset.Parse("2025-01-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ProgressPercent: 72,
            Summary: "Compiling integration tests.",
            CorrelationId: "corr-status-1");

        var attachment = renderer.RenderStatusCard(status);

        Assert.Equal(AdaptiveCardContentType, attachment.ContentType);
        var card = Assert.IsType<JObject>(attachment.Content);
        var json = card.ToString();
        Assert.Contains("agent-build-007", json, StringComparison.Ordinal);
        Assert.Contains("task-42", json, StringComparison.Ordinal);
        Assert.Contains("Working", json, StringComparison.Ordinal);
        Assert.Contains("72%", json, StringComparison.Ordinal);
        Assert.Contains("Compiling integration tests.", json, StringComparison.Ordinal);

        var actions = card["actions"] as JArray ?? new JArray();
        Assert.Empty(actions);
    }

    [Fact]
    public void RenderStatusCard_NullStatus_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AdaptiveCardBuilder().RenderStatusCard(null!));
    }

    /// <summary>
    /// Two untested production branches in <c>AdaptiveCardBuilder.RenderStatusCard</c>
    /// (impl-plan §3.1 step 3): when <see cref="AgentStatusSummary.TaskId"/> is
    /// <c>null</c> the renderer substitutes the literal <c>"(swarm-wide)"</c> on the
    /// Task fact (the affordance for swarm-wide status reports per
    /// <see cref="AgentStatusSummary.TaskId"/>'s XDoc "null for swarm-wide status"),
    /// and when <see cref="AgentStatusSummary.ProgressPercent"/> is <c>null</c> the
    /// Progress fact is omitted entirely (rather than rendered as 0% or "n/a"). The
    /// existing status-card test only covers the populated-value path for both fields;
    /// this test asserts the explicit omission and substitution semantics.
    /// </summary>
    [Fact]
    public void RenderStatusCard_NullTaskIdAndNullProgressPercent_SubstitutesSwarmWideAndOmitsProgressFact()
    {
        var renderer = new AdaptiveCardBuilder();
        var status = new AgentStatusSummary(
            AgentId: "agent-swarm-monitor",
            TaskId: null,
            AgentName: "Swarm Monitor",
            CurrentState: "Idle",
            ActiveTaskCount: 0,
            LastActivityAt: DateTimeOffset.Parse("2025-06-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ProgressPercent: null,
            Summary: "All agents idle.",
            CorrelationId: "corr-status-swarm-wide");

        var attachment = renderer.RenderStatusCard(status);
        var card = Assert.IsType<JObject>(attachment.Content);

        var factSet = ((JArray)card["body"]!)
            .Single(b => (string?)b["type"] == "FactSet");
        var facts = ((JArray)factSet["facts"]!)
            .Select(f => new
            {
                Title = (string?)f["title"],
                Value = (string?)f["value"],
            })
            .ToList();

        // Branch 1: null TaskId substitutes the swarm-wide literal.
        var taskFact = Assert.Single(facts, f => f.Title == "Task");
        Assert.Equal("(swarm-wide)", taskFact.Value);

        // Branch 2: null ProgressPercent omits the Progress fact (not "0%", not "n/a").
        Assert.DoesNotContain(facts, f => f.Title == "Progress");
    }

    [Fact]
    public void RenderIncidentCard_EmitsEscalateAndAcknowledgeActions()
    {
        var renderer = new AdaptiveCardBuilder();
        var incident = new IncidentSummary(
            IncidentId: "inc-9001",
            QuestionId: "Q-inc-9001-alice",
            TaskId: "task-77",
            AgentId: "release-agent-01",
            AffectedAgents: new[] { "release-agent-01", "deploy-agent-03" },
            Severity: MessageSeverities.Critical,
            Title: "Production deploy failed",
            Description: "Rollback initiated due to schema migration error.",
            OccurredAt: DateTimeOffset.Parse("2025-02-01T03:14:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-incident-1");

        var attachment = renderer.RenderIncidentCard(incident);

        Assert.Equal(AdaptiveCardContentType, attachment.ContentType);
        var card = Assert.IsType<JObject>(attachment.Content);
        var actions = card["actions"] as JArray;
        Assert.NotNull(actions);
        Assert.Equal(2, actions!.Count);

        var titles = actions.Select(a => (string?)a["title"]).ToList();
        Assert.Contains("Escalate", titles);
        Assert.Contains("Acknowledge", titles);

        var values = actions.Select(a => (string?)a["data"]?[CardActionDataKeys.ActionValue]).ToList();
        Assert.Contains("escalate", values);
        Assert.Contains("acknowledge", values);

        // Per architecture §6.3 step 4 the data payload must carry the per-recipient
        // QuestionId (not the IncidentId) so CardActionHandler can resolve the
        // originating AgentQuestion in the standard single-decision flow.
        Assert.All(actions, action =>
        {
            var data = action["data"] as JObject;
            Assert.NotNull(data);
            Assert.Equal("Q-inc-9001-alice", (string?)data![CardActionDataKeys.QuestionId]);
            Assert.NotNull((string?)data[CardActionDataKeys.ActionId]);
            Assert.Equal("corr-incident-1", (string?)data[CardActionDataKeys.CorrelationId]);
        });

        var json = card.ToString();
        Assert.Contains("inc-9001", json, StringComparison.Ordinal);
        Assert.Contains("release-agent-01", json, StringComparison.Ordinal);
        Assert.Contains("deploy-agent-03", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderIncidentCard_NullIncident_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AdaptiveCardBuilder().RenderIncidentCard(null!));
    }

    /// <summary>
    /// Edge case for the incident card's "Affected agents" fact: when
    /// <see cref="IncidentSummary.AffectedAgents"/> is empty, the renderer falls back
    /// to the single reporting <see cref="IncidentSummary.AgentId"/> (per
    /// <c>AdaptiveCardBuilder.RenderIncidentCard</c> lines 163–165) so the card still
    /// shows a meaningful "Affected agents" value rather than an empty string. This
    /// closes the only branch in <see cref="AdaptiveCardBuilder"/> with no direct
    /// coverage; the existing incident card test exercises the multi-agent path only.
    /// </summary>
    [Fact]
    public void RenderIncidentCard_EmptyAffectedAgents_FallsBackToReportingAgentId()
    {
        var renderer = new AdaptiveCardBuilder();
        var incident = new IncidentSummary(
            IncidentId: "inc-empty-affected",
            QuestionId: "Q-inc-empty",
            TaskId: "task-77",
            AgentId: "release-agent-01",
            AffectedAgents: Array.Empty<string>(),
            Severity: MessageSeverities.Warning,
            Title: "Single-agent advisory",
            Description: "Heartbeat skew detected.",
            OccurredAt: DateTimeOffset.Parse("2025-02-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-inc-empty");

        var attachment = renderer.RenderIncidentCard(incident);
        var card = Assert.IsType<JObject>(attachment.Content);

        // Walk the body for the FactSet that includes "Affected agents"; the renderer
        // emits one FactSet containing the metadata facts (incident id, severity, task,
        // reporting agent, affected agents, occurred).
        var factSets = ((JArray)card["body"]!)
            .Where(b => (string?)b["type"] == "FactSet")
            .ToList();
        var affectedFact = factSets
            .SelectMany(fs => (JArray)fs["facts"]!)
            .Single(f => (string?)f["title"] == "Affected agents");

        Assert.Equal("release-agent-01", (string?)affectedFact["value"]);
    }

    [Fact]
    public void RenderReleaseGateCard_EmitsApproveRejectDeferActionsAndConditionsChecklist()
    {
        var renderer = new AdaptiveCardBuilder();
        var gate = new ReleaseGateRequest(
            GateId: "gate-prod-deploy",
            QuestionId: "Q-gate-prod-deploy-alice",
            TaskId: "task-release-12",
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
            CorrelationId: "corr-gate-1");

        var attachment = renderer.RenderReleaseGateCard(gate);

        Assert.Equal(AdaptiveCardContentType, attachment.ContentType);
        var card = Assert.IsType<JObject>(attachment.Content);
        var actions = card["actions"] as JArray;
        Assert.NotNull(actions);
        Assert.Equal(3, actions!.Count);

        var titles = actions.Select(a => (string?)a["title"]).ToList();
        Assert.Contains("Approve", titles);
        Assert.Contains("Reject", titles);
        Assert.Contains("Defer", titles);

        var values = actions.Select(a => (string?)a["data"]?[CardActionDataKeys.ActionValue]).ToList();
        Assert.Contains("approve", values);
        Assert.Contains("reject", values);
        Assert.Contains("defer", values);

        // Per architecture §6.3.1 (multi-approver release gates), the data payload must
        // carry the per-approver AgentQuestion.QuestionId — NOT the GateId — so that
        // Stage 3.3's CardActionHandler runs the standard single-decision lifecycle
        // independently for each approver.
        Assert.All(actions, action =>
        {
            var data = action["data"] as JObject;
            Assert.NotNull(data);
            Assert.Equal("Q-gate-prod-deploy-alice", (string?)data![CardActionDataKeys.QuestionId]);
            Assert.NotNull((string?)data[CardActionDataKeys.ActionId]);
            Assert.Equal("corr-gate-1", (string?)data[CardActionDataKeys.CorrelationId]);
        });

        var json = card.ToString();
        Assert.Contains("Production Deploy Gate", json, StringComparison.Ordinal);
        Assert.Contains("v1.42.0", json, StringComparison.Ordinal);
        Assert.Contains("Production", json, StringComparison.Ordinal);
        Assert.Contains("All tests pass", json, StringComparison.Ordinal);
        Assert.Contains("Security scan clean", json, StringComparison.Ordinal);
        Assert.Contains("SRE sign-off", json, StringComparison.Ordinal);
        Assert.Contains("Pending", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Threshold aggregation lives in the orchestrator, NOT in the card template
    /// (per impl-plan §3.1 step 5 + architecture §6.3.1). The card must NOT surface
    /// any "N of M" approval rollup — the per-approver card renders identically and
    /// the orchestrator-computed rollup is reflected through <c>GateStatus</c>.
    /// </summary>
    [Fact]
    public void RenderReleaseGateCard_DoesNotSurfaceThresholdAggregationFacts()
    {
        var renderer = new AdaptiveCardBuilder();
        var gate = new ReleaseGateRequest(
            GateId: "gate-no-rollup",
            QuestionId: "Q-gate-no-rollup-bob",
            TaskId: "task-release-13",
            GateName: "Stage Deploy Gate",
            ReleaseVersion: "v2.0.0",
            Environment: "Staging",
            GateConditions: new[] { new ReleaseGateCondition("Smoke tests pass", true) },
            GateStatus: "Pending",
            Deadline: DateTimeOffset.Parse("2025-03-01T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-gate-no-rollup");

        var attachment = renderer.RenderReleaseGateCard(gate);
        var card = Assert.IsType<JObject>(attachment.Content);
        var json = card.ToString();

        // No threshold counters, no "N of M" string anywhere on the card.
        Assert.DoesNotContain("Approvals", json, StringComparison.Ordinal);
        Assert.DoesNotContain(" of ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Required", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Approver", json, StringComparison.Ordinal);

        // No fact set entry at all named like an aggregation rollup.
        var body = (JArray)card["body"]!;
        var allFactTitles = body
            .OfType<JObject>()
            .Where(b => (string?)b["type"] == "FactSet")
            .SelectMany(fs => ((JArray)fs["facts"]!).OfType<JObject>())
            .Select(f => (string?)f["title"])
            .ToList();
        Assert.DoesNotContain(allFactTitles, t =>
            t is not null
            && (t.Contains("Approval", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Approver", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Required", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RenderReleaseGateCard_NullGate_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AdaptiveCardBuilder().RenderReleaseGateCard(null!));
    }

    /// <summary>
    /// Untested production branch in <c>AdaptiveCardBuilder.RenderReleaseGateCard</c>
    /// at line 225 (<c>if (gate.GateConditions.Count &gt; 0)</c>): when the
    /// orchestrator constructs a <see cref="ReleaseGateRequest"/> with an empty
    /// <see cref="ReleaseGateRequest.GateConditions"/>, the renderer omits BOTH the
    /// "Gate conditions" <c>TextBlock</c> header AND the conditions <c>FactSet</c> —
    /// the card must not render an empty header with no rows beneath it. This is
    /// structurally different from the single-fact omission tested on the status
    /// card (a TextBlock header + FactSet pair, not a single fact), and exercises a
    /// real impl-plan §3.1 step 5 affordance: gates with no preconditions still
    /// render their Approve/Reject/Defer action buttons but skip the empty checklist.
    /// </summary>
    [Fact]
    public void RenderReleaseGateCard_EmptyGateConditions_OmitsBothConditionsHeaderAndFactSet()
    {
        var renderer = new AdaptiveCardBuilder();
        var gate = new ReleaseGateRequest(
            GateId: "gate-no-conditions",
            QuestionId: "Q-gate-no-conditions-carol",
            TaskId: "task-release-bare",
            GateName: "Bare Approval Gate",
            ReleaseVersion: "v0.1.0",
            Environment: "Sandbox",
            GateConditions: Array.Empty<ReleaseGateCondition>(),
            GateStatus: "Pending",
            Deadline: DateTimeOffset.Parse("2025-04-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-gate-no-conditions");

        var attachment = renderer.RenderReleaseGateCard(gate);
        var card = Assert.IsType<JObject>(attachment.Content);
        var body = (JArray)card["body"]!;

        // Branch 1: no TextBlock with text "Gate conditions" — the header is fully
        // omitted, not rendered as an empty bolded label.
        Assert.DoesNotContain(body.OfType<JObject>(), b =>
            (string?)b["type"] == "TextBlock"
            && (string?)b["text"] == "Gate conditions");

        // Branch 2: the only FactSet on the body is the metadata fact set (Gate /
        // Release / Environment / Status / Deadline). The conditions FactSet — which
        // would have rendered one Fact per gate condition with the "✓ satisfied" /
        // "✗ outstanding" markers — must not appear.
        var factSets = body.OfType<JObject>()
            .Where(b => (string?)b["type"] == "FactSet")
            .ToList();
        Assert.Single(factSets);
        var metadataFacts = ((JArray)factSets[0]["facts"]!)
            .OfType<JObject>()
            .Select(f => (string?)f["title"])
            .ToList();
        Assert.DoesNotContain("✓ satisfied", metadataFacts);
        Assert.DoesNotContain("✗ outstanding", metadataFacts);
        Assert.Contains("Gate", metadataFacts);
        Assert.Contains("Status", metadataFacts);

        // The three action buttons still render — impl-plan §3.1 step 5 contract
        // unaffected by an empty condition list.
        var actions = card["actions"] as JArray;
        Assert.NotNull(actions);
        Assert.Equal(3, actions!.Count);
    }

    [Fact]
    public void RenderDecisionConfirmationCard_OmitsActionButtonsAndIncludesDecisionMetadata()
    {
        var renderer = new AdaptiveCardBuilder();
        var decision = new HumanDecisionEvent(
            QuestionId: "Q-confirm-1",
            ActionValue: "approve",
            Comment: "Looks good to me.",
            Messenger: "Teams",
            ExternalUserId: "aad-alice",
            ExternalMessageId: "act-123",
            ReceivedAt: DateTimeOffset.Parse("2025-04-01T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-confirm-1");

        var attachment = renderer.RenderDecisionConfirmationCard(decision);

        var card = Assert.IsType<JObject>(attachment.Content);
        var actions = card["actions"] as JArray ?? new JArray();
        Assert.Empty(actions);

        var json = card.ToString();
        Assert.Contains("Q-confirm-1", json, StringComparison.Ordinal);
        Assert.Contains("approve", json, StringComparison.Ordinal);
        Assert.Contains("Looks good to me.", json, StringComparison.Ordinal);
        Assert.Contains("aad-alice", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDecisionConfirmationCard_NullDecision_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AdaptiveCardBuilder().RenderDecisionConfirmationCard(null!));
    }

    /// <summary>
    /// Schema-validity gate for Stage 3.1: the JObject content emitted by
    /// <see cref="AdaptiveCardBuilder.RenderQuestionCard"/> must round-trip through the
    /// official <c>AdaptiveCards</c> parser (<see cref="AdaptiveCard.FromJson"/>) and
    /// reconstruct an <see cref="AdaptiveCard"/> at the pinned schema version. This is
    /// stronger than the field-shape assertions in the other tests: it confirms Teams
    /// will actually render the payload (mismatched schema features, unknown element
    /// types, or malformed action data would surface as a parser exception or a missing
    /// <c>parseResult.Card</c>). Closes the tech-spec §5.1 risk R-3 mitigation
    /// ("Pin card schema version in templates") with executable verification.
    /// </summary>
    [Fact]
    public void RenderQuestionCard_RoundTripsThroughAdaptiveCardParser_AtPinnedSchemaVersion()
    {
        var renderer = new AdaptiveCardBuilder();
        var question = NewQuestion(
            "Q-roundtrip-1",
            new HumanAction("approve", "Approve", "approve", false),
            new HumanAction("reject", "Reject", "reject", true));

        var attachment = renderer.RenderQuestionCard(question);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal(AdaptiveCardBuilder.SchemaVersion.ToString(), (string?)card["version"]);

        var parseResult = AdaptiveCard.FromJson(card.ToString());
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult.Card);
        Assert.Equal(
            AdaptiveCardBuilder.SchemaVersion.ToString(),
            parseResult.Card.Version.ToString());
    }

    /// <summary>
    /// Parser-validity gate for the status card (impl-plan §3.1 step 3). Status cards
    /// are structurally distinct from question cards — they carry a progress-percent
    /// fact, a last-activity timestamp, and emit zero <c>Action.Submit</c> elements —
    /// so re-parsing through <see cref="AdaptiveCard.FromJson"/> validates the
    /// no-action shape independently of the question/release-gate paths.
    /// </summary>
    [Fact]
    public void RenderStatusCard_RoundTripsThroughAdaptiveCardParser_AtPinnedSchemaVersion()
    {
        var renderer = new AdaptiveCardBuilder();
        var status = new AgentStatusSummary(
            AgentId: "agent-build-007",
            TaskId: "task-42",
            AgentName: "Build Agent",
            CurrentState: "Working",
            ActiveTaskCount: 3,
            LastActivityAt: DateTimeOffset.Parse("2025-01-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ProgressPercent: 72,
            Summary: "Compiling integration tests.",
            CorrelationId: "corr-status-roundtrip");

        var attachment = renderer.RenderStatusCard(status);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal(AdaptiveCardBuilder.SchemaVersion.ToString(), (string?)card["version"]);

        var parseResult = AdaptiveCard.FromJson(card.ToString());
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult.Card);
        Assert.Equal(
            AdaptiveCardBuilder.SchemaVersion.ToString(),
            parseResult.Card.Version.ToString());
    }

    /// <summary>
    /// Parser-validity gate for the incident escalation card (impl-plan §3.1 step 4).
    /// Incident cards exercise the attention-styled severity header path, the
    /// affected-agents list rendering, and a two-button escalate/acknowledge action
    /// set — none of which the question/status round-trip tests cover.
    /// </summary>
    [Fact]
    public void RenderIncidentCard_RoundTripsThroughAdaptiveCardParser_AtPinnedSchemaVersion()
    {
        var renderer = new AdaptiveCardBuilder();
        var incident = new IncidentSummary(
            IncidentId: "inc-roundtrip",
            QuestionId: "Q-inc-roundtrip-alice",
            TaskId: "task-77",
            AgentId: "release-agent-01",
            AffectedAgents: new[] { "release-agent-01", "deploy-agent-03" },
            Severity: MessageSeverities.Critical,
            Title: "Production deploy failed",
            Description: "Rollback initiated due to schema migration error.",
            OccurredAt: DateTimeOffset.Parse("2025-02-01T03:14:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-incident-roundtrip");

        var attachment = renderer.RenderIncidentCard(incident);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal(AdaptiveCardBuilder.SchemaVersion.ToString(), (string?)card["version"]);

        var parseResult = AdaptiveCard.FromJson(card.ToString());
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult.Card);
        Assert.Equal(
            AdaptiveCardBuilder.SchemaVersion.ToString(),
            parseResult.Card.Version.ToString());
    }

    /// <summary>
    /// Parser-validity gate for the release-gate card (impl-plan §3.1 step 5). Release
    /// gate cards are the most structurally complex of the five templates: they emit a
    /// gate-conditions checklist (each row a <c>TextBlock</c> with a status emoji), a
    /// gate-status fact set, and a three-button approve/reject/defer action set whose
    /// per-action <c>Data</c> payload carries the per-approver
    /// <see cref="AgentQuestion.QuestionId"/> per architecture §6.3.1 multi-approver
    /// modeling. Re-parsing through <see cref="AdaptiveCard.FromJson"/> validates that
    /// all of this is schema-compliant Adaptive Card 1.5 JSON, not just well-formed
    /// JObject content.
    /// </summary>
    [Fact]
    public void RenderReleaseGateCard_RoundTripsThroughAdaptiveCardParser_AtPinnedSchemaVersion()
    {
        var renderer = new AdaptiveCardBuilder();
        var gate = new ReleaseGateRequest(
            GateId: "gate-roundtrip",
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
            CorrelationId: "corr-gate-roundtrip");

        var attachment = renderer.RenderReleaseGateCard(gate);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal(AdaptiveCardBuilder.SchemaVersion.ToString(), (string?)card["version"]);

        var parseResult = AdaptiveCard.FromJson(card.ToString());
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult.Card);
        Assert.Equal(
            AdaptiveCardBuilder.SchemaVersion.ToString(),
            parseResult.Card.Version.ToString());
    }

    /// <summary>
    /// Parser-validity gate for the decision-confirmation card. Confirmation cards
    /// emit zero action buttons and embed the answered <see cref="HumanDecisionEvent"/>
    /// metadata as facts — a third structurally distinct shape that needs its own
    /// schema-validity coverage so Teams' message-update path renders it cleanly.
    /// </summary>
    [Fact]
    public void RenderDecisionConfirmationCard_RoundTripsThroughAdaptiveCardParser_AtPinnedSchemaVersion()
    {
        var renderer = new AdaptiveCardBuilder();
        var decision = new HumanDecisionEvent(
            QuestionId: "Q-confirm-roundtrip",
            ActionValue: "approve",
            Comment: "Looks good to me.",
            Messenger: "Teams",
            ExternalUserId: "aad-alice",
            ExternalMessageId: "act-roundtrip-1",
            ReceivedAt: DateTimeOffset.Parse("2025-04-01T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId: "corr-confirm-roundtrip");

        var attachment = renderer.RenderDecisionConfirmationCard(decision);
        var card = Assert.IsType<JObject>(attachment.Content);

        Assert.Equal(AdaptiveCardBuilder.SchemaVersion.ToString(), (string?)card["version"]);

        var parseResult = AdaptiveCard.FromJson(card.ToString());
        Assert.NotNull(parseResult);
        Assert.NotNull(parseResult.Card);
        Assert.Equal(
            AdaptiveCardBuilder.SchemaVersion.ToString(),
            parseResult.Card.Version.ToString());
    }

    private static AgentQuestion NewQuestion(string questionId, params HumanAction[] actions)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-build",
            TaskId = "task-42",
            TenantId = Tenant,
            TargetUserId = "internal-alice",
            Title = "Promote build to staging?",
            Body = "Build #42 finished. Promote to staging environment?",
            Severity = MessageSeverities.Info,
            AllowedActions = actions,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = $"corr-{questionId}",
        };
    }
}
