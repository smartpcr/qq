using System.Collections.ObjectModel;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A question raised by an agent that requires a human decision. Shared across
/// every messenger connector (Discord, Telegram, Slack, Teams). See architecture.md
/// Section 3.1 for the full field semantics and the FR-001 epic brief for the
/// originating contract.
/// </summary>
/// <param name="QuestionId">
/// Unique question identifier. Constrained to printable ASCII, max
/// <see cref="QuestionIdValidator.MaxLength"/> characters, no <c>:</c> separator.
/// Validate with <see cref="QuestionIdValidator"/>.
/// </param>
/// <param name="AgentId">Originating agent identifier.</param>
/// <param name="TaskId">Associated work item / task identifier.</param>
/// <param name="Title">Short summary rendered as the message subject.</param>
/// <param name="Body">Full context displayed to the human operator.</param>
/// <param name="Severity">Priority severity (drives queue ordering).</param>
/// <param name="AllowedActions">
/// Operator-selectable actions. Rendered as buttons (Discord, Slack, Teams) or as
/// an inline keyboard (Telegram). The caller-supplied collection is defensively
/// copied at construction into a <see cref="ReadOnlyCollection{T}"/>; the value
/// exposed to consumers cannot be mutated through the public collection API
/// after construction -- it cannot be downcast to <see cref="System.Array"/>,
/// <c>HumanAction[]</c>, <see cref="List{T}"/>, or any other mutable
/// <see cref="IList{T}"/> implementation, and mutating members of the
/// <see cref="IList{T}"/> view throw <see cref="NotSupportedException"/>.
/// (Immutability is shallow: callers should treat <see cref="HumanAction"/>
/// elements themselves as the immutable records they already are.) Validate each
/// <see cref="HumanAction.ActionId"/> with <see cref="ActionIdValidator"/>.
/// </param>
/// <param name="ExpiresAt">Deadline after which the question is considered timed out.</param>
/// <param name="CorrelationId">End-to-end trace identifier.</param>
public sealed record AgentQuestion(
    string QuestionId,
    string AgentId,
    string TaskId,
    string Title,
    string Body,
    MessageSeverity Severity,
    IReadOnlyList<HumanAction> AllowedActions,
    DateTimeOffset ExpiresAt,
    string CorrelationId)
{
    private readonly IReadOnlyList<HumanAction> _allowedActions = ToImmutable(AllowedActions);

    /// <inheritdoc cref="AgentQuestion(string, string, string, string, string, MessageSeverity, IReadOnlyList{HumanAction}, DateTimeOffset, string)"/>
    public IReadOnlyList<HumanAction> AllowedActions
    {
        get => _allowedActions;
        init => _allowedActions = ToImmutable(value);
    }

    private static IReadOnlyList<HumanAction> ToImmutable(IReadOnlyList<HumanAction>? value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(AllowedActions));
        }

        // Snapshot to a private array first so caller mutations cannot affect the
        // wrapped view, then wrap in ReadOnlyCollection so callers cannot downcast
        // the IReadOnlyList<HumanAction> back to HumanAction[] / List<HumanAction>
        // and mutate the model.
        return new ReadOnlyCollection<HumanAction>(value.ToArray());
    }
}

