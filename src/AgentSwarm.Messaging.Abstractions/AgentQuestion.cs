namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents a blocking question from an agent to a human operator.
/// The shared model does not include a DefaultAction property;
/// the proposed default action is carried as sidecar metadata
/// via <see cref="AgentQuestionEnvelope.ProposedDefaultActionId"/>.
/// </summary>
/// <remarks>
/// Construction-time validation pins the Telegram callback-data constraints
/// from architecture.md §3.1:
/// <list type="bullet">
///   <item><see cref="QuestionId"/> is 1..30 printable-ASCII characters
///         with no <c>':'</c> separator and no ASCII control characters
///         (the separator would break <c>QuestionId:ActionId</c> parsing;
///         control chars would invisibly corrupt the encoded payload).</item>
///   <item><see cref="AllowedActions"/> must be non-null and contain
///         distinct <see cref="HumanAction.ActionId"/> values (callback
///         dispatch keys on that id, so duplicates would silently shadow
///         each other).</item>
///   <item><see cref="CorrelationId"/> must be non-null, non-empty, and
///         non-whitespace per the "All messages include trace/correlation
///         ID" acceptance criterion.</item>
/// </list>
/// </remarks>
public sealed record AgentQuestion
{
    /// <summary>
    /// Maximum character length of <see cref="QuestionId"/>. Combined with
    /// the ASCII-only constraint, this guarantees the UTF-8 byte count of
    /// the id is ≤ 30 — see the class-level remarks for the budget math.
    /// </summary>
    public const int MaxQuestionIdLength = 30;

    private readonly string _questionId = null!;
    private readonly IReadOnlyList<HumanAction> _allowedActions = null!;
    private readonly string _correlationId = null!;

    public required string QuestionId
    {
        get => _questionId;
        init
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "QuestionId must be non-null and non-empty.",
                    nameof(QuestionId));
            }
            if (value.Length > MaxQuestionIdLength)
            {
                throw new ArgumentException(
                    $"QuestionId must be {MaxQuestionIdLength} characters or fewer "
                    + $"(Telegram callback_data 64-byte budget; was {value.Length}).",
                    nameof(QuestionId));
            }
            var failure = CallbackDataValidation.ValidateCallbackToken(value, out var offendingIndex);
            switch (failure)
            {
                case CallbackDataValidation.FailureReason.NonAscii:
                    throw new ArgumentException(
                        $"QuestionId must contain ASCII characters only so its UTF-8 byte "
                        + $"count cannot exceed its character count and the Telegram 64-byte "
                        + $"callback_data budget remains satisfied; non-ASCII code point "
                        + $"U+{(int)value[offendingIndex]:X4} at index {offendingIndex}.",
                        nameof(QuestionId));
                case CallbackDataValidation.FailureReason.ContainsSeparator:
                    throw new ArgumentException(
                        $"QuestionId must not contain the ':' separator; callback_data is "
                        + $"serialized as 'QuestionId:ActionId' and an embedded ':' would "
                        + $"make the parse ambiguous (offending index {offendingIndex}).",
                        nameof(QuestionId));
                case CallbackDataValidation.FailureReason.ContainsControlChar:
                    throw new ArgumentException(
                        $"QuestionId must not contain ASCII control characters; "
                        + $"U+{(int)value[offendingIndex]:X4} at index {offendingIndex} would "
                        + $"silently corrupt the encoded callback_data payload.",
                        nameof(QuestionId));
            }
            _questionId = value;
        }
    }

    public required string AgentId { get; init; }

    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required MessageSeverity Severity { get; init; }

    public required IReadOnlyList<HumanAction> AllowedActions
    {
        get => _allowedActions;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(AllowedActions));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in value)
            {
                if (action is null)
                {
                    throw new ArgumentException(
                        "AllowedActions may not contain null entries.",
                        nameof(AllowedActions));
                }
                if (!seen.Add(action.ActionId))
                {
                    throw new ArgumentException(
                        $"Duplicate ActionId '{action.ActionId}' in AllowedActions. "
                        + "Callback dispatch is keyed on ActionId so duplicates would "
                        + "silently shadow each other.",
                        nameof(AllowedActions));
                }
            }
            _allowedActions = value;
        }
    }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string CorrelationId
    {
        get => _correlationId;
        init => _correlationId = CorrelationIdValidation.Require(value, nameof(CorrelationId));
    }
}
