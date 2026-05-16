using AdaptiveCards;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Builds the Adaptive Card payloads the Stage 3.2 command handlers send as replies — the
/// help card listed in <c>e2e-scenarios.md</c> §Unrecognized input, the acknowledgement
/// cards rendered by <see cref="AskCommandHandler"/> / <see cref="StatusCommandHandler"/> /
/// <see cref="EscalateCommandHandler"/> / <see cref="PauseCommandHandler"/> /
/// <see cref="ResumeCommandHandler"/>, the decision-confirmation card the approve/reject
/// handlers post after a successful resolution, and the disambiguation card the bare
/// <c>approve</c>/<c>reject</c> handlers render when more than one open question exists for
/// the conversation.
/// </summary>
/// <remarks>
/// <para>
/// The card layout is intentionally lightweight — the canonical question-rendering paths
/// remain owned by <see cref="Cards.AdaptiveCardBuilder"/>. The reply cards here are
/// command-acknowledgement / help / disambiguation cards which do not flow through the
/// proactive question pipeline, so they are constructed inline rather than going through
/// <see cref="Cards.IAdaptiveCardRenderer"/>.
/// </para>
/// <para>
/// Every card is emitted at <see cref="Cards.AdaptiveCardBuilder.SchemaVersion"/>
/// (Adaptive Card 1.5) so cards rendered by these helpers look consistent with the
/// renderer's output in Teams clients (per <c>tech-spec.md</c> §5.1 risk R-3, schema
/// version is pinned).
/// </para>
/// </remarks>
internal static class CommandReplyCards
{
    /// <summary>
    /// Build a help card listing every canonical command keyword in
    /// <see cref="CommandNames.All"/>. Matches the table layout shown in
    /// <c>e2e-scenarios.md</c> §Unrecognized input.
    /// </summary>
    public static IMessageActivity BuildHelpCard()
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "I didn't recognise that command. Here's what I support:",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Wrap = true,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("agent ask", "Create a new task"));
        facts.Facts.Add(new AdaptiveFact("agent status", "Query status"));
        facts.Facts.Add(new AdaptiveFact("approve", "Approve an open question"));
        facts.Facts.Add(new AdaptiveFact("reject", "Reject an open question"));
        facts.Facts.Add(new AdaptiveFact("escalate", "Escalate the current incident"));
        facts.Facts.Add(new AdaptiveFact("pause", "Pause the agent for this conversation"));
        facts.Facts.Add(new AdaptiveFact("resume", "Resume a paused agent"));
        card.Body.Add(facts);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Tip: `approve` and `reject` work without arguments when there is exactly one open question; otherwise pass the question id (for example, `approve q-123`).",
            IsSubtle = true,
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        return ToActivity(card, fallback: "Available commands: agent ask, agent status, approve, reject, escalate, pause, resume.");
    }

    /// <summary>
    /// Build the acknowledgement card sent after the user issues <c>agent ask</c>. Per
    /// <c>architecture.md</c> §6.1 step 9, the card surfaces the correlation/tracking id
    /// so the user can follow the task downstream.
    /// </summary>
    public static IMessageActivity BuildAskAcknowledgementCard(string prompt, string correlationId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Task submitted",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt)
                ? "(empty prompt)"
                : prompt,
            Wrap = true,
            Spacing = AdaptiveSpacing.Medium,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("Tracking ID", correlationId));
        card.Body.Add(facts);

        return ToActivity(card, fallback: $"Task submitted — tracking ID: {correlationId}");
    }

    /// <summary>
    /// Build a simple "command received" acknowledgement card used by the
    /// escalate / pause / resume handlers (status uses a richer status-summary card built
    /// by <see cref="BuildStatusReply"/>). The card confirms receipt only — the actual
    /// lifecycle work is performed by the agent-swarm orchestrator (§2.14, external) after
    /// it consumes the <see cref="CommandEvent"/> published by the handler itself (per
    /// <c>implementation-plan.md</c> §3.2: each canonical command handler publishes its own
    /// verb-specific <see cref="CommandEvent"/>; <see cref="TeamsSwarmActivityHandler"/> no
    /// longer publishes a post-dispatch event).
    /// </summary>
    public static IMessageActivity BuildAcknowledgementCard(string title, string detail, string correlationId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = title,
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = detail,
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("Tracking ID", correlationId));
        card.Body.Add(facts);

        return ToActivity(card, fallback: $"{title}: {detail} (tracking ID: {correlationId}).");
    }

    /// <summary>
    /// Build the disambiguation card rendered by bare <c>approve</c> / <c>reject</c>
    /// commands when <see cref="IAgentQuestionStore.GetOpenByConversationAsync"/> returns
    /// more than one open question. The card lists the <see cref="AgentQuestion.QuestionId"/>,
    /// <see cref="AgentQuestion.Title"/>, and <see cref="AgentQuestion.CreatedAt"/> for each
    /// open question so the user can re-issue the command with an explicit question id
    /// (per <c>implementation-plan.md</c> §3.2 step 4).
    /// </summary>
    public static IMessageActivity BuildDisambiguationCard(string commandName, IReadOnlyList<AgentQuestion> openQuestions)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = $"Multiple open questions — pick one to {commandName}",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = $"There are {openQuestions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} open questions in this conversation. Re-issue the command with an explicit question id, e.g. `{commandName} {openQuestions[0].QuestionId}`.",
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        foreach (var question in openQuestions)
        {
            var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
            facts.Facts.Add(new AdaptiveFact("Question", question.QuestionId));
            facts.Facts.Add(new AdaptiveFact("Title", question.Title));
            facts.Facts.Add(new AdaptiveFact("Created", question.CreatedAt.UtcDateTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture)));
            card.Body.Add(facts);
        }

        var fallback = $"Multiple open questions in this conversation — pick one and re-issue with the question id: " +
            string.Join(", ", openQuestions.Select(q => q.QuestionId));

        return ToActivity(card, fallback);
    }

    /// <summary>
    /// Build a confirmation card rendered after a successful text-based
    /// approve/reject resolution, using the canonical decision-confirmation layout from
    /// <see cref="Cards.AdaptiveCardBuilder.RenderDecisionConfirmationCard(HumanDecisionEvent)"/>.
    /// </summary>
    public static IMessageActivity BuildDecisionConfirmationCard(
        Cards.IAdaptiveCardRenderer renderer,
        HumanDecisionEvent decision)
    {
        var attachment = renderer.RenderDecisionConfirmationCard(decision);
        var activity = Activity.CreateMessageActivity();
        activity.Attachments = new List<Attachment> { attachment };
        activity.Text = $"Recorded {decision.ActionValue} for {decision.QuestionId}.";
        return activity;
    }

    /// <summary>
    /// Build a plain-text error card for command-handler error paths (e.g., explicit
    /// <c>approve q-X</c> where <c>q-X</c> does not exist, or a target question whose
    /// status is no longer <c>Open</c>).
    /// </summary>
    public static IMessageActivity BuildErrorCard(string title, string detail)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        var header = new AdaptiveContainer
        {
            Style = AdaptiveContainerStyle.Attention,
            Bleed = true,
        };
        header.Items.Add(new AdaptiveTextBlock
        {
            Text = title,
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Color = AdaptiveTextColor.Attention,
            Wrap = true,
        });
        card.Body.Add(header);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = detail,
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        return ToActivity(card, fallback: $"{title}: {detail}");
    }

    /// <summary>
    /// Build the "no active agents" Adaptive Card returned by
    /// <see cref="StatusCommandHandler"/> when the
    /// <see cref="IAgentSwarmStatusProvider"/> reports an empty set. Distinct from
    /// <see cref="BuildErrorCard"/> because this is a normal-flow success — there's just
    /// nothing to display.
    /// </summary>
    public static IMessageActivity BuildEmptyStatusCard(string correlationId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Agent status",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "No active agents are currently visible in your scope.",
            Wrap = true,
            Spacing = AdaptiveSpacing.Medium,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("Tracking ID", correlationId));
        card.Body.Add(facts);

        return ToActivity(card, fallback: $"Agent status: no active agents (tracking ID: {correlationId}).");
    }

    /// <summary>
    /// Build a status reply containing one Adaptive Card attachment per active agent. The
    /// per-agent card layout is owned by <see cref="Cards.IAdaptiveCardRenderer.RenderStatusCard"/>
    /// (Stage 3.1); this helper composes the per-agent attachments into a single
    /// <c>list</c>-layout reply for Teams.
    /// </summary>
    public static IMessageActivity BuildStatusReply(
        Cards.IAdaptiveCardRenderer renderer,
        IReadOnlyList<Cards.AgentStatusSummary> agents)
    {
        var activity = Activity.CreateMessageActivity();
        activity.AttachmentLayout = AttachmentLayoutTypes.List;
        activity.Attachments = new List<Attachment>(agents.Count);
        foreach (var summary in agents)
        {
            activity.Attachments.Add(renderer.RenderStatusCard(summary));
        }

        activity.Text = $"Agent status — {agents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} active agent(s).";
        return activity;
    }

    private static IMessageActivity ToActivity(AdaptiveCard card, string fallback)
    {
        var attachment = new Attachment
        {
            ContentType = AdaptiveCard.ContentType,
            Content = JObject.Parse(card.ToJson()),
        };

        var activity = Activity.CreateMessageActivity();
        activity.Attachments = new List<Attachment> { attachment };
        activity.Text = fallback;
        return activity;
    }
}
