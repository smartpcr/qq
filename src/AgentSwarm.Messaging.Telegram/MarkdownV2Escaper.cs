using System.Text;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Escapes user-supplied text for Telegram Bot API MarkdownV2 parse mode.
/// Per the Bot API spec, the following characters must be escaped with a
/// preceding backslash to appear as literal text inside a MarkdownV2
/// message body:
/// <c>_ * [ ] ( ) ~ ` &gt; # + - = | { } . !</c>
/// </summary>
/// <remarks>
/// <para>
/// Without escaping, an operator-facing question body containing
/// punctuation (e.g. an environment name like <c>env=prod-1</c> or a
/// shell snippet) would either render with unintended formatting or be
/// rejected by Telegram with a <c>can't parse entities</c> error. The
/// sender escapes every dynamic field (titles, bodies, button labels,
/// default-action labels, correlation footers) before splicing into the
/// MarkdownV2 template.
/// </para>
/// <para>
/// We deliberately do <b>not</b> escape inside fenced code blocks because
/// MarkdownV2 treats <c>```...```</c> as a verbatim section; the sender
/// does not emit code-fences, so escaping plain text is the only path
/// exercised.
/// </para>
/// </remarks>
internal static class MarkdownV2Escaper
{
    private static readonly char[] EscapableChars =
    {
        '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-',
        '=', '|', '{', '}', '.', '!', '\\',
    };

    private static readonly HashSet<char> EscapableSet = new(EscapableChars);

    /// <summary>
    /// Returns <paramref name="value"/> with every MarkdownV2-significant
    /// character prefixed by a backslash. A <c>null</c> input returns the
    /// empty string. The escaping is idempotent in the sense that
    /// re-escaping output adds another backslash to every prior backslash,
    /// so callers should escape once at the boundary.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (EscapableSet.Contains(c))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
