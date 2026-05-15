using System.Globalization;
using AdaptiveCards;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Schema;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Cards;

/// <summary>
/// Default <see cref="IAdaptiveCardRenderer"/> implementation. Renders Adaptive Card 1.5
/// payloads using the <c>AdaptiveCards</c> NuGet package and wraps each card in a
/// Bot Framework <see cref="Attachment"/> with
/// <c>ContentType = "application/vnd.microsoft.card.adaptive"</c>. Implements steps 1–5 of
/// <c>implementation-plan.md</c> §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema version.</b> Cards are emitted at Adaptive Card schema version 1.5 (the
/// current Teams baseline) — pinned per <c>tech-spec.md</c> §5.1 risk R-3 ("Adaptive Card
/// schema drift … Pin card schema version in templates").
/// </para>
/// <para>
/// <b>Action data layout.</b> Every <c>Action.Submit</c> on a question / incident /
/// release-gate card carries a typed payload (<see cref="CardActionPayload"/>) with the
/// originating <c>questionId</c>, the chosen <c>actionValue</c>, and the
/// <c>correlationId</c>. When at least one allowed action declares
/// <see cref="HumanAction.RequiresComment"/> = <c>true</c>, the card includes one
/// <c>Input.Text</c> field (Id <c>comment</c>) so the user can type the reason in the
/// same submit. <see cref="CardActionMapper"/> reads the merged payload on the inbound
/// round-trip.
/// </para>
/// </remarks>
public sealed class AdaptiveCardBuilder : IAdaptiveCardRenderer
{
    /// <summary>The Adaptive Card schema version emitted by every <c>Render*</c> method.</summary>
    public static readonly AdaptiveSchemaVersion SchemaVersion = new(1, 5);

    /// <inheritdoc />
    public Attachment RenderQuestionCard(AgentQuestion question)
    {
        if (question is null)
        {
            throw new ArgumentNullException(nameof(question));
        }

        var card = new AdaptiveCard(SchemaVersion);

        AddSeverityHeader(card, question.Title, question.Severity);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = question.Body,
            Wrap = true,
            Spacing = AdaptiveSpacing.Medium,
        });

        card.Body.Add(BuildMetadataFactSet(new (string, string)[]
        {
            ("Question", question.QuestionId),
            ("Agent", question.AgentId),
            ("Task", question.TaskId),
            ("Expires", FormatTimestamp(question.ExpiresAt)),
        }));

        var requiresComment = question.AllowedActions.Any(a => a.RequiresComment);
        if (requiresComment)
        {
            // Single shared comment field — Bot Framework merges the typed value into
            // every action's Data dict on submit, so the mapper sees the same `comment`
            // key regardless of which button the user pressed. Empty submissions are
            // normalised to null on HumanDecisionEvent.Comment so action buttons that
            // do not require a comment are unaffected by the field's presence.
            var commentRequiredActions = question.AllowedActions
                .Where(a => a.RequiresComment)
                .Select(a => a.Label)
                .ToList();
            var label = commentRequiredActions.Count == question.AllowedActions.Count
                ? "Comment (required)"
                : $"Comment (required for: {string.Join(", ", commentRequiredActions)})";
            card.Body.Add(new AdaptiveTextInput
            {
                Id = CardActionDataKeys.Comment,
                Label = label,
                Placeholder = "Type your reason here…",
                IsMultiline = true,
                Spacing = AdaptiveSpacing.Medium,
            });
        }

        foreach (var action in question.AllowedActions)
        {
            card.Actions.Add(BuildSubmitAction(
                title: action.Label,
                actionId: action.ActionId,
                actionValue: action.Value,
                questionId: question.QuestionId,
                correlationId: question.CorrelationId,
                style: SeverityToActionStyle(question.Severity, action.Value)));
        }

        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderStatusCard(AgentStatusSummary status)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        var card = new AdaptiveCard(SchemaVersion);
        AddPlainHeader(card, $"Agent status — {status.AgentName}");

        var facts = new List<(string Key, string Value)>
        {
            ("Agent", status.AgentId),
            ("Task", status.TaskId ?? "(swarm-wide)"),
            ("State", status.CurrentState),
            ("Active tasks", status.ActiveTaskCount.ToString(CultureInfo.InvariantCulture)),
            ("Last activity", FormatTimestamp(status.LastActivityAt)),
        };

        if (status.ProgressPercent.HasValue)
        {
            var clamped = Math.Clamp(status.ProgressPercent.Value, 0, 100);
            facts.Add(("Progress", clamped.ToString(CultureInfo.InvariantCulture) + "%"));
        }

        card.Body.Add(BuildMetadataFactSet(facts));

        if (!string.IsNullOrWhiteSpace(status.Summary))
        {
            card.Body.Add(new AdaptiveTextBlock
            {
                Text = status.Summary,
                Wrap = true,
                Spacing = AdaptiveSpacing.Medium,
            });
        }

        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderIncidentCard(IncidentSummary incident)
    {
        if (incident is null)
        {
            throw new ArgumentNullException(nameof(incident));
        }

        var card = new AdaptiveCard(SchemaVersion);
        AddSeverityHeader(card, $"Incident — {incident.Title}", incident.Severity);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = incident.Description,
            Wrap = true,
            Spacing = AdaptiveSpacing.Medium,
        });

        var affected = incident.AffectedAgents.Count == 0
            ? incident.AgentId
            : string.Join(", ", incident.AffectedAgents);

        card.Body.Add(BuildMetadataFactSet(new (string, string)[]
        {
            ("Incident", incident.IncidentId),
            ("Severity", incident.Severity),
            ("Task", incident.TaskId),
            ("Reporting agent", incident.AgentId),
            ("Affected agents", affected),
            ("Occurred", FormatTimestamp(incident.OccurredAt)),
        }));

        // Incident cards expose an Escalate / Acknowledge pair. The QuestionId on the
        // payload is the per-recipient AgentQuestion ID (the orchestrator creates one
        // per acknowledger / escalator following the standard single-decision lifecycle).
        // The IncidentId is included as additional metadata in the body fact set above.
        card.Actions.Add(BuildSubmitAction(
            title: "Escalate",
            actionId: "incident.escalate",
            actionValue: "escalate",
            questionId: incident.QuestionId,
            correlationId: incident.CorrelationId,
            style: "destructive"));

        card.Actions.Add(BuildSubmitAction(
            title: "Acknowledge",
            actionId: "incident.acknowledge",
            actionValue: "acknowledge",
            questionId: incident.QuestionId,
            correlationId: incident.CorrelationId,
            style: "positive"));

        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderReleaseGateCard(ReleaseGateRequest gate)
    {
        if (gate is null)
        {
            throw new ArgumentNullException(nameof(gate));
        }

        var card = new AdaptiveCard(SchemaVersion);
        AddPlainHeader(card, $"Release gate — {gate.GateName}");

        // Per implementation-plan §3.1 step 5 and architecture §6.3.1 the release-gate
        // card template renders identically for each per-approver AgentQuestion and does
        // NOT surface threshold aggregation ("N of M approvers must approve") — that
        // rollup is computed by the orchestrator's workflow layer and reflected through
        // GateStatus. The fact set below intentionally omits any approval counters.
        card.Body.Add(BuildMetadataFactSet(new (string, string)[]
        {
            ("Gate", gate.GateId),
            ("Release", gate.ReleaseVersion),
            ("Environment", gate.Environment),
            ("Status", gate.GateStatus),
            ("Deadline", FormatTimestamp(gate.Deadline)),
        }));

        if (gate.GateConditions.Count > 0)
        {
            card.Body.Add(new AdaptiveTextBlock
            {
                Text = "Gate conditions",
                Weight = AdaptiveTextWeight.Bolder,
                Spacing = AdaptiveSpacing.Medium,
            });

            var conditions = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Small };
            foreach (var condition in gate.GateConditions)
            {
                conditions.Facts.Add(new AdaptiveFact(
                    title: condition.Name,
                    value: condition.Satisfied ? "✓ satisfied" : "✗ outstanding"));
            }

            card.Body.Add(conditions);
        }

        // Approve / Reject / Defer per implementation-plan §3.1 step 5. The QuestionId on
        // the payload is the per-approver AgentQuestion.QuestionId from the
        // ReleaseGateRequest — Stage 3.3's CardActionHandler runs the standard
        // single-decision first-writer-wins lifecycle independently for each approver
        // (per architecture.md §6.3.1). Threshold aggregation (e.g., "2 of 3 approvers
        // must approve") is the orchestrator's workflow-layer responsibility.
        card.Actions.Add(BuildSubmitAction(
            title: "Approve",
            actionId: "gate.approve",
            actionValue: "approve",
            questionId: gate.QuestionId,
            correlationId: gate.CorrelationId,
            style: "positive"));

        card.Actions.Add(BuildSubmitAction(
            title: "Reject",
            actionId: "gate.reject",
            actionValue: "reject",
            questionId: gate.QuestionId,
            correlationId: gate.CorrelationId,
            style: "destructive"));

        card.Actions.Add(BuildSubmitAction(
            title: "Defer",
            actionId: "gate.defer",
            actionValue: "defer",
            questionId: gate.QuestionId,
            correlationId: gate.CorrelationId,
            style: null));

        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision)
        => RenderDecisionConfirmationCard(decision, actorDisplayName: null);

    /// <inheritdoc />
    public Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision, string? actorDisplayName)
    {
        if (decision is null)
        {
            throw new ArgumentNullException(nameof(decision));
        }

        var card = new AdaptiveCard(SchemaVersion);
        var actorLabel = string.IsNullOrWhiteSpace(actorDisplayName)
            ? decision.ExternalUserId
            : actorDisplayName!;
        // Header carries the action-attributed acknowledgement that the implementation-plan
        // §3.3 acceptance scenario requires (e.g. "Approved by Alice"). The actor-display
        // fallback to ExternalUserId keeps the contract working even when the inbound turn
        // context did not carry a friendly name (e.g. test channels, missing From.Name).
        var headline = $"{ActionVerbForActionValue(decision.ActionValue)} by {actorLabel}";
        AddPlainHeader(card, headline);

        var facts = new List<(string Key, string Value)>
        {
            ("Question", decision.QuestionId),
            ("Action", decision.ActionValue),
            ("Decided by", actorLabel),
            ("Recorded at", FormatTimestamp(decision.ReceivedAt)),
        };

        if (!string.IsNullOrWhiteSpace(decision.Comment))
        {
            facts.Add(("Comment", decision.Comment!));
        }

        card.Body.Add(BuildMetadataFactSet(facts));

        // Confirmation cards are read-only by contract (per e2e-scenarios.md §Bot updates
        // an existing approval card after decision: "the updated card no longer contains
        // action buttons"). No card.Actions are added.
        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderExpiredNoticeCard(string questionId)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentNullException(nameof(questionId));
        }

        var card = new AdaptiveCard(SchemaVersion);
        AddPlainHeader(card, "Question expired");
        card.Body.Add(BuildMetadataFactSet(new (string, string)[]
        {
            ("Question", questionId),
            ("Status", "Expired"),
            ("Recorded at", FormatTimestamp(DateTimeOffset.UtcNow)),
        }));
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "This question expired before a human response was recorded.",
            Wrap = true,
            IsSubtle = true,
            Spacing = AdaptiveSpacing.Medium,
        });
        return ToAttachment(card);
    }

    /// <inheritdoc />
    public Attachment RenderCancelledNoticeCard(string questionId)
    {
        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentNullException(nameof(questionId));
        }

        var card = new AdaptiveCard(SchemaVersion);
        AddPlainHeader(card, "Question cancelled");
        card.Body.Add(BuildMetadataFactSet(new (string, string)[]
        {
            ("Question", questionId),
            ("Status", "Cancelled"),
            ("Recorded at", FormatTimestamp(DateTimeOffset.UtcNow)),
        }));
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "This question was cancelled before a human response was recorded.",
            Wrap = true,
            IsSubtle = true,
            Spacing = AdaptiveSpacing.Medium,
        });
        return ToAttachment(card);
    }

    private static string ActionVerbForActionValue(string actionValue)
    {
        // Translate the lowercase machine-readable action value into a past-tense verb so
        // the confirmation card header reads naturally in English. Unknown action values
        // fall back to "Responded" so the card still renders coherently for custom
        // action vocabularies.
        return actionValue?.ToLowerInvariant() switch
        {
            "approve" => "Approved",
            "reject" => "Rejected",
            "acknowledge" => "Acknowledged",
            "escalate" => "Escalated",
            "defer" => "Deferred",
            "pause" => "Paused",
            "resume" => "Resumed",
            null => "Responded",
            "" => "Responded",
            _ => char.ToUpperInvariant(actionValue[0]) + actionValue[1..],
        };
    }

    private static void AddPlainHeader(AdaptiveCard card, string title)
    {
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = title,
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });
    }

    private static void AddSeverityHeader(AdaptiveCard card, string title, string severity)
    {
        var container = new AdaptiveContainer
        {
            Style = SeverityToContainerStyle(severity),
            Bleed = true,
        };

        container.Items.Add(new AdaptiveTextBlock
        {
            Text = title,
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        container.Items.Add(new AdaptiveTextBlock
        {
            Text = $"Severity: {severity}",
            Weight = AdaptiveTextWeight.Default,
            IsSubtle = true,
            Spacing = AdaptiveSpacing.Small,
        });

        card.Body.Add(container);
    }

    private static AdaptiveFactSet BuildMetadataFactSet(IEnumerable<(string Key, string Value)> facts)
    {
        var set = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        foreach (var (key, value) in facts)
        {
            set.Facts.Add(new AdaptiveFact(title: key, value: value));
        }

        return set;
    }

    private static AdaptiveSubmitAction BuildSubmitAction(
        string title,
        string actionId,
        string actionValue,
        string questionId,
        string correlationId,
        string? style)
    {
        var submit = new AdaptiveSubmitAction
        {
            Title = title,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CardActionDataKeys.QuestionId] = questionId,
                [CardActionDataKeys.ActionId] = actionId,
                [CardActionDataKeys.ActionValue] = actionValue,
                [CardActionDataKeys.CorrelationId] = correlationId,
            },
        };

        if (!string.IsNullOrWhiteSpace(style))
        {
            submit.Style = style;
        }

        return submit;
    }

    private static AdaptiveContainerStyle SeverityToContainerStyle(string severity)
    {
        return severity switch
        {
            MessageSeverities.Critical => AdaptiveContainerStyle.Attention,
            MessageSeverities.Error => AdaptiveContainerStyle.Attention,
            MessageSeverities.Warning => AdaptiveContainerStyle.Warning,
            MessageSeverities.Info => AdaptiveContainerStyle.Emphasis,
            _ => AdaptiveContainerStyle.Default,
        };
    }

    private static string? SeverityToActionStyle(string severity, string actionValue)
    {
        // Highlight the dominant action: rejections / escalations on high-severity
        // questions render destructive, approvals render positive. Anything else
        // remains the Adaptive Card default.
        var normalized = actionValue.ToLowerInvariant();
        if (normalized is "approve" or "acknowledge" or "resume")
        {
            return "positive";
        }

        if (normalized is "reject" or "escalate" or "pause")
        {
            return severity is MessageSeverities.Critical or MessageSeverities.Error
                ? "destructive"
                : null;
        }

        return null;
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

    private static Attachment ToAttachment(AdaptiveCard card)
    {
        // Bot Framework round-trips the attachment Content through Newtonsoft.Json. The
        // safest interop is to serialise the card to its canonical JSON via
        // AdaptiveCard.ToJson() and re-parse into a JObject so the wire shape is the
        // exact Adaptive Card schema (camelCase, no .NET-specific extension data) and
        // the Teams client renders it without falling through to the fallback text.
        return new Attachment
        {
            ContentType = AdaptiveCard.ContentType,
            Content = JObject.Parse(card.ToJson()),
        };
    }
}
