using System.Diagnostics.CodeAnalysis;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Validates <see cref="HumanAction.ActionId"/> values against the same cross-connector
/// constraints applied to <see cref="AgentQuestion.QuestionId"/> by
/// <see cref="QuestionIdValidator"/>: non-empty, printable ASCII, no control
/// characters, no <c>:</c> separator (reserved by the <c>q:{QuestionId}:{ActionId}</c>
/// component identifier scheme), and no more than <see cref="MaxLength"/> characters.
/// </summary>
/// <remarks>
/// The 30-character cap mirrors <see cref="QuestionIdValidator.MaxLength"/> so that
/// the composed Discord <c>custom_id</c> (<c>q:</c> prefix + 30-char question id +
/// <c>:</c> + 30-char action id = 63 ASCII bytes) fits comfortably within both
/// Discord's 100-character <c>custom_id</c> limit and Telegram's 64-byte
/// <c>callback_data</c> limit. Connectors should call <see cref="EnsureValid"/>
/// before encoding component identifiers and before persisting questions.
/// </remarks>
public static class ActionIdValidator
{
    /// <summary>Maximum allowed length, in characters, of an action id.</summary>
    public const int MaxLength = 30;

    /// <summary>
    /// Attempts to validate <paramref name="actionId"/>. Returns <see langword="true"/>
    /// when the value satisfies every constraint; otherwise returns <see langword="false"/>
    /// and populates <paramref name="error"/> with a human-readable reason.
    /// </summary>
    public static bool TryValidate(
        string? actionId,
        [NotNullWhen(false)] out string? error)
    {
        if (actionId is null)
        {
            error = "ActionId must not be null.";
            return false;
        }

        if (actionId.Length == 0)
        {
            error = "ActionId must not be empty.";
            return false;
        }

        if (actionId.Length > MaxLength)
        {
            error =
                $"ActionId length {actionId.Length} exceeds the maximum of {MaxLength} characters.";
            return false;
        }

        for (var i = 0; i < actionId.Length; i++)
        {
            var c = actionId[i];

            if (c == ':')
            {
                error =
                    $"ActionId must not contain ':' (reserved separator) at position {i}.";
                return false;
            }

            // Printable ASCII range is 0x20..0x7E inclusive. Anything outside that
            // (control chars, DEL, or non-ASCII) is rejected.
            if (c < 0x20 || c > 0x7E)
            {
                error =
                    $"ActionId must contain only printable ASCII characters (0x20-0x7E); invalid character 0x{(int)c:X4} at position {i}.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates <paramref name="actionId"/> and throws
    /// <see cref="ArgumentException"/> with a descriptive message when invalid.
    /// </summary>
    public static void EnsureValid(string? actionId, string paramName = "actionId")
    {
        if (!TryValidate(actionId, out var error))
        {
            throw new ArgumentException(error, paramName);
        }
    }
}
