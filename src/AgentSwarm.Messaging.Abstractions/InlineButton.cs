namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Transport-agnostic inline-keyboard button surfaced by
/// <see cref="ITelegramUpdatePipeline"/> on
/// <see cref="PipelineResult.ResponseButtons"/>. The Stage 2.3 sender layer
/// is responsible for rendering this into a Telegram-specific
/// <c>InlineKeyboardMarkup</c> on the outbound message; modeling it here
/// keeps the pipeline contract decoupled from the Telegram.Bot SDK types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-format constraints (architecture.md §3.1).</b> Telegram caps
/// <c>callback_data</c> at <b>64 UTF-8 bytes</b> and the user-visible
/// <c>Label</c> at <b>64 UTF-8 bytes</b>. Both fields are validated in
/// the record's <c>init</c> accessors so a malformed button cannot reach
/// the sender (and therefore cannot reach Telegram, which would respond
/// with HTTP 400). The byte limit is enforced via
/// <see cref="System.Text.Encoding.UTF8"/>'s byte count, not character
/// count, because one non-ASCII code point can encode to up to four UTF-8
/// bytes.
/// </para>
/// <para>
/// <b>Use case.</b> The Stage 2.2 multi-workspace disambiguation prompt
/// emits one <see cref="InlineButton"/> per <c>OperatorBinding</c>
/// returned by <see cref="IUserAuthorizationService"/>; the sender
/// renders these as inline-keyboard rows so the operator can pick a
/// workspace from the chat (per architecture.md §4.3 and
/// e2e-scenarios.md "workspace disambiguation via inline keyboard").
/// </para>
/// </remarks>
public sealed record InlineButton
{
    /// <summary>
    /// Maximum length in UTF-8 bytes of <see cref="Label"/> per the
    /// Telegram Bot API <c>InlineKeyboardButton.text</c> contract.
    /// </summary>
    public const int MaxLabelBytes = 64;

    /// <summary>
    /// Maximum length in UTF-8 bytes of <see cref="CallbackData"/> per the
    /// Telegram Bot API <c>InlineKeyboardButton.callback_data</c> contract.
    /// </summary>
    public const int MaxCallbackDataBytes = 64;

    private readonly string _label = null!;
    private readonly string _callbackData = null!;

    /// <summary>
    /// User-visible button text. Must be non-empty and fit in
    /// <see cref="MaxLabelBytes"/> UTF-8 bytes.
    /// </summary>
    public required string Label
    {
        get => _label;
        init => _label = ValidateUtf8(value, nameof(Label), MaxLabelBytes);
    }

    /// <summary>
    /// Opaque callback payload echoed back to the bot as a
    /// <see cref="EventType.CallbackResponse"/> event when the operator
    /// taps the button. Must be non-empty and fit in
    /// <see cref="MaxCallbackDataBytes"/> UTF-8 bytes.
    /// </summary>
    public required string CallbackData
    {
        get => _callbackData;
        init => _callbackData = ValidateUtf8(value, nameof(CallbackData), MaxCallbackDataBytes);
    }

    private static string ValidateUtf8(string value, string paramName, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"{paramName} must be non-null and non-empty.", paramName);
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(value);
        if (bytes > maxBytes)
        {
            throw new ArgumentException(
                $"{paramName} must be at most {maxBytes} UTF-8 bytes (was {bytes}).",
                paramName);
        }

        return value;
    }
}
