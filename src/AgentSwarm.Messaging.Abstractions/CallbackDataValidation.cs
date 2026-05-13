namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Shared validation helpers for inputs that flow into Telegram callback_data.
/// </summary>
/// <remarks>
/// Telegram's Bot API caps callback_data at 64 <b>bytes</b> after UTF-8
/// encoding (architecture.md §11, §3.1). Identifier fields that participate
/// in the encoded payload (<c>"QuestionId:ActionId"</c>) must therefore be
/// constrained at the byte level — character-count alone is not sufficient
/// because a single non-ASCII code point can encode to up to 4 UTF-8 bytes.
/// Enforcing ASCII makes the byte count equal to the character count, so
/// a 30-character ASCII identifier is provably ≤ 30 bytes on the wire.
/// In addition to the ASCII rule, identifier fields must not contain the
/// <c>':'</c> separator (which would break the
/// <c>"QuestionId:ActionId"</c> split at parse time) or ASCII control
/// characters (which Telegram strips silently and which can also leak
/// terminal-control sequences into operator-facing logs).
/// </remarks>
internal static class CallbackDataValidation
{
    /// <summary>
    /// Reason an identifier failed callback-data safety. <see cref="None"/>
    /// is the success case; everything else carries a specific diagnostic.
    /// </summary>
    public enum FailureReason
    {
        None,
        NonAscii,
        ContainsSeparator,
        ContainsControlChar,
    }

    /// <summary>
    /// Returns <c>true</c> if every character in <paramref name="value"/> is
    /// in the ASCII range (U+0000..U+007F). When a non-ASCII character is
    /// found, <paramref name="offendingIndex"/> is set to its position so
    /// the caller can produce a precise diagnostic.
    /// </summary>
    public static bool IsAsciiOnly(string value, out int offendingIndex)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F)
            {
                offendingIndex = i;
                return false;
            }
        }
        offendingIndex = -1;
        return true;
    }

    /// <summary>
    /// Full callback-token safety check: rejects non-ASCII, the <c>':'</c>
    /// separator character, and ASCII control characters (U+0000..U+001F,
    /// U+007F). Returns <see cref="FailureReason.None"/> on success;
    /// otherwise <paramref name="offendingIndex"/> identifies the position
    /// and the returned reason names the violated rule.
    /// </summary>
    public static FailureReason ValidateCallbackToken(string value, out int offendingIndex)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c > 0x7F)
            {
                offendingIndex = i;
                return FailureReason.NonAscii;
            }
            if (c == ':')
            {
                offendingIndex = i;
                return FailureReason.ContainsSeparator;
            }
            if (c < 0x20 || c == 0x7F)
            {
                offendingIndex = i;
                return FailureReason.ContainsControlChar;
            }
        }
        offendingIndex = -1;
        return FailureReason.None;
    }
}
