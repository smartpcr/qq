using System.Text;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Represents a single action button an agent question can offer to a human operator.
/// </summary>
/// <remarks>
/// Construction-time validation pins the Telegram callback-data and inline
/// button-label constraints from architecture.md §3.1:
/// <list type="bullet">
///   <item><see cref="ActionId"/> is 1..30 printable-ASCII characters with
///         no <c>':'</c> separator and no ASCII control characters — half
///         of the 64-byte callback_data budget after the
///         <c>QuestionId:ActionId</c> join, with the separator and control
///         characters disallowed so the parse is unambiguous and the
///         encoded payload cannot carry invisible corruption.</item>
///   <item><see cref="Label"/> is 1..64 <b>UTF-8 bytes</b>. The Telegram
///         button label budget is byte-oriented, so multi-byte Unicode
///         labels (emoji, accents, CJK) are constrained at the wire-relevant
///         unit rather than at a character count that would silently
///         over-spend the budget.</item>
/// </list>
/// </remarks>
public sealed record HumanAction
{
    /// <summary>Maximum character length of <see cref="ActionId"/> (ASCII-only, so chars == bytes).</summary>
    public const int MaxActionIdLength = 30;

    /// <summary>
    /// Maximum character count of <see cref="Label"/>. Retained for
    /// compatibility with char-oriented callers; the authoritative wire
    /// constraint is <see cref="MaxLabelByteLength"/>.
    /// </summary>
    public const int MaxLabelLength = 64;

    /// <summary>
    /// Maximum UTF-8 byte count of <see cref="Label"/> — the unit Telegram
    /// actually budgets. A 64-character ASCII label fits exactly at the
    /// boundary; a multi-byte label is admitted only up to this byte budget.
    /// </summary>
    public const int MaxLabelByteLength = 64;

    private readonly string _actionId = null!;
    private readonly string _label = null!;

    public required string ActionId
    {
        get => _actionId;
        init
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "ActionId must be non-null and non-empty.",
                    nameof(ActionId));
            }
            if (value.Length > MaxActionIdLength)
            {
                throw new ArgumentException(
                    $"ActionId must be {MaxActionIdLength} characters or fewer "
                    + $"(Telegram callback_data 64-byte budget; was {value.Length}).",
                    nameof(ActionId));
            }
            var failure = CallbackDataValidation.ValidateCallbackToken(value, out var offendingIndex);
            switch (failure)
            {
                case CallbackDataValidation.FailureReason.NonAscii:
                    throw new ArgumentException(
                        $"ActionId must contain ASCII characters only so its UTF-8 byte "
                        + $"count cannot exceed its character count and the Telegram 64-byte "
                        + $"callback_data budget remains satisfied; non-ASCII code point "
                        + $"U+{(int)value[offendingIndex]:X4} at index {offendingIndex}.",
                        nameof(ActionId));
                case CallbackDataValidation.FailureReason.ContainsSeparator:
                    throw new ArgumentException(
                        $"ActionId must not contain the ':' separator; callback_data is "
                        + $"serialized as 'QuestionId:ActionId' and an embedded ':' would "
                        + $"make the parse ambiguous (offending index {offendingIndex}).",
                        nameof(ActionId));
                case CallbackDataValidation.FailureReason.ContainsControlChar:
                    throw new ArgumentException(
                        $"ActionId must not contain ASCII control characters; "
                        + $"U+{(int)value[offendingIndex]:X4} at index {offendingIndex} would "
                        + $"silently corrupt the encoded callback_data payload.",
                        nameof(ActionId));
            }
            _actionId = value;
        }
    }

    public required string Label
    {
        get => _label;
        init
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(
                    "Label must be non-null and non-empty.",
                    nameof(Label));
            }
            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > MaxLabelByteLength)
            {
                throw new ArgumentException(
                    $"Label must be {MaxLabelByteLength} UTF-8 bytes or fewer "
                    + $"(Telegram inline button label budget; was {byteCount} bytes "
                    + $"across {value.Length} characters).",
                    nameof(Label));
            }
            _label = value;
        }
    }

    public required string Value { get; init; }

    public bool RequiresComment { get; init; }
}
