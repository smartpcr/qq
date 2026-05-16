using System.Text;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Helpers for the Telegram MarkdownV2 dialect — the parse mode used by
/// <see cref="TelegramMessageSender"/> for all outbound rendered text.
/// </summary>
/// <remarks>
/// <para>
/// Telegram's MarkdownV2 reserves the following characters and rejects
/// the entire message with HTTP 400 if any are emitted unescaped in
/// plain-text positions:
/// <c>_ * [ ] ( ) ~ ` &gt; # + - = | { } . !</c>. The escape character
/// is a single backslash; the Bot API spec lists these as the full set
/// of reserved characters.
/// </para>
/// <para>
/// <see cref="Escape"/> applies the escape uniformly so the renderer
/// never has to remember which character it just emitted; deliberate
/// formatting tokens (e.g. bold-surround asterisks emitted by the
/// renderer) are produced via concatenation around already-escaped
/// fragments rather than by selective escape inside this helper.
/// </para>
/// </remarks>
internal static class MarkdownV2
{
    /// <summary>
    /// Returns the empty string when <paramref name="text"/> is null;
    /// otherwise prepends a backslash to each MarkdownV2-reserved
    /// character. Safe to apply repeatedly only on a known-unescaped
    /// fragment (do not re-escape an already-escaped span — the leading
    /// backslash would itself be escaped).
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text!.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '_':
                case '*':
                case '[':
                case ']':
                case '(':
                case ')':
                case '~':
                case '`':
                case '>':
                case '#':
                case '+':
                case '-':
                case '=':
                case '|':
                case '{':
                case '}':
                case '.':
                case '!':
                case '\\':
                    builder.Append('\\');
                    builder.Append(ch);
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
