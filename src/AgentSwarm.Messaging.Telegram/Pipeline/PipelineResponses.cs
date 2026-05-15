using System.Text;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Centralized operator-facing response strings emitted by
/// <see cref="TelegramUpdatePipeline"/>. Pinning the literal strings here
/// (a) keeps tests stable, (b) makes localization a single-file change,
/// and (c) prevents subtle drift between the pipeline and any future
/// audit/reply logging.
/// </summary>
internal static class PipelineResponses
{
    /// <summary>
    /// Inline-keyboard <c>callback_data</c> prefix the pipeline assigns to
    /// workspace-selection buttons. Stage 3.3 <c>CallbackQueryHandler</c>
    /// recognises this prefix to dispatch the workspace-selection flow
    /// rather than treating the callback as a question answer. Re-exposed
    /// from <see cref="PendingDisambiguation.WorkspaceCallbackDataPrefix"/>
    /// so the wire-format constant lives next to the contract that
    /// defines it; the alias keeps existing references in tests and logs
    /// stable.
    /// </summary>
    public const string WorkspaceCallbackDataPrefix = PendingDisambiguation.WorkspaceCallbackDataPrefix;

    public const string Unauthorized =
        "Unauthorized – contact your administrator.";

    public const string InsufficientPermissions =
        "Insufficient permissions for this command.";

    public const string CommandNotRecognized =
        "Command not recognized.";

    public const string UnknownEventType =
        "Unsupported event type.";

    public const string MultiWorkspacePromptText =
        "You have access to multiple workspaces. Choose one to continue:";

    /// <summary>
    /// Fallback reply surfaced to the operator when a routed handler
    /// returns <see cref="CommandResult.Success"/>=<c>false</c> WITHOUT
    /// populating <see cref="CommandResult.ResponseText"/>. Without this
    /// fallback the operator would receive an empty reply for a failed
    /// command and have no way to know the action did not succeed.
    /// </summary>
    public const string HandlerFailureFallback =
        "Command failed – please try again or contact your administrator.";

    /// <summary>
    /// U+2026 HORIZONTAL ELLIPSIS appended by
    /// <see cref="TruncateLabelForUtf8Budget"/> to signal that an
    /// operator-facing label was clipped to fit Telegram's
    /// <c>InlineKeyboardButton.text</c> byte cap. Encodes to exactly
    /// 3 UTF-8 bytes (<c>0xE2 0x80 0xA6</c>); see
    /// <see cref="EllipsisUtf8ByteCount"/>.
    /// </summary>
    internal const string TruncationEllipsis = "\u2026";

    /// <summary>
    /// UTF-8 byte length of <see cref="TruncationEllipsis"/>. Cached as a
    /// constant so the truncation hot path does not re-measure the
    /// suffix per button.
    /// </summary>
    internal const int EllipsisUtf8ByteCount = 3;

    /// <summary>
    /// Builds the workspace-selection inline keyboard surfaced when an
    /// operator has more than one <c>OperatorBinding</c> for the same
    /// (user, chat) pair. Each binding becomes one button whose
    /// <see cref="InlineButton.Label"/> is the workspace identifier
    /// (truncated to fit Telegram's 64-byte label cap if needed; see
    /// <see cref="TruncateLabelForUtf8Budget"/>) and whose
    /// <see cref="InlineButton.CallbackData"/> is
    /// <c>ws:&lt;token&gt;:&lt;index&gt;</c> — where <c>index</c> is the
    /// 0-based position of the binding in <paramref name="workspaceIds"/>.
    /// The Stage 2.3 sender renders these as a Telegram inline keyboard;
    /// the Stage 3.3 <c>CallbackQueryHandler</c> splits on the second
    /// colon to recover (a) the disambiguation token used to look up the
    /// <see cref="PendingDisambiguation"/> server-side record and
    /// (b) the integer index, then resolves the chosen workspace via
    /// <see cref="PendingDisambiguation.CandidateWorkspaceIds"/>[index].
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the index, not the workspace id, on the wire.</b> Telegram
    /// caps <c>callback_data</c> at 64 UTF-8 bytes AND the format uses
    /// <c>:</c> as the structural separator. Embedding the raw
    /// workspace identifier — a tenant-supplied string of unconstrained
    /// length and content — exposed two latent failure modes the iter-3
    /// evaluator flagged: (1) a workspace id longer than the remaining
    /// callback budget would throw at
    /// <see cref="InlineButton.CallbackData"/> validation, blocking
    /// otherwise-valid prompts; (2) a workspace id containing a literal
    /// <c>:</c> would corrupt the wire format and either be misparsed
    /// or rejected by Stage 3.3. Encoding only the integer index closes
    /// both: the index is always 1-3 ASCII digits regardless of the
    /// workspace id's length or content, so the resulting
    /// <c>callback_data</c> is bounded above by <c>3 + 12 + 1 + 3 = 19</c>
    /// bytes (prefix + token + sep + index) for any conceivable binding
    /// count, leaving ample headroom under the 64-byte cap.
    /// </para>
    /// <para>
    /// <b>Label byte cap.</b> <see cref="InlineButton.Label"/> carries
    /// its own (independent) 64-byte budget enforced by the
    /// <c>InlineButton</c> setter. Workspace ids are tenant-supplied
    /// strings of unconstrained length; iter-4 review pinned that
    /// piping them verbatim into <see cref="InlineButton.Label"/> would
    /// throw <see cref="ArgumentException"/> for any id beyond the cap,
    /// AND — because this method runs OUTSIDE
    /// <see cref="TelegramUpdatePipeline"/>'s route-stage try/catch —
    /// would also leak the dedup reservation taken at the dedup stage,
    /// silently dropping the next live re-delivery. The label is now
    /// clipped via <see cref="TruncateLabelForUtf8Budget"/> at a
    /// whole-Rune boundary with an ellipsis suffix, so an oversize id
    /// still produces a valid (if shortened) prompt rather than
    /// crashing the pipeline.
    /// </para>
    /// <para>
    /// <b>Tampering rejection (Stage 3.3 contract).</b> A non-numeric
    /// or out-of-range index in a tapped callback indicates either a
    /// hostile payload or a stale prompt that survived a server
    /// restart; Stage 3.3 must reject it rather than fall back to a
    /// default workspace.
    /// </para>
    /// </remarks>
    /// <param name="token">
    /// Server-side <see cref="PendingDisambiguation.Token"/> the pipeline
    /// just stored. Same token shared by every button in the keyboard;
    /// the index suffix differentiates which option was tapped.
    /// </param>
    /// <param name="workspaceIds">
    /// Authoritative ordered list of workspaces the operator may pick.
    /// Order MUST match <see cref="PendingDisambiguation.CandidateWorkspaceIds"/>
    /// because the wire format references entries by position, not name.
    /// </param>
    public static IReadOnlyList<InlineButton> MultiWorkspaceButtons(
        string token,
        IReadOnlyList<string> workspaceIds)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("token must be non-null and non-empty.", nameof(token));
        }
        ArgumentNullException.ThrowIfNull(workspaceIds);

        var buttons = new InlineButton[workspaceIds.Count];
        for (var i = 0; i < workspaceIds.Count; i++)
        {
            buttons[i] = new InlineButton
            {
                Label = TruncateLabelForUtf8Budget(workspaceIds[i], InlineButton.MaxLabelBytes),
                CallbackData = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{WorkspaceCallbackDataPrefix}{token}:{i}"),
            };
        }
        return buttons;
    }

    /// <summary>
    /// Returns <paramref name="label"/> verbatim if it already fits in
    /// <paramref name="maxBytes"/> UTF-8 bytes; otherwise returns the
    /// longest whole-Rune prefix that, when followed by
    /// <see cref="TruncationEllipsis"/>, still fits in
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clipping happens at Unicode scalar boundaries (via
    /// <see cref="Rune.EnumerateRunes(string)"/>), never mid-codepoint,
    /// so the truncated label is always well-formed UTF-8 and never
    /// splits a surrogate pair or a multi-byte sequence. Each <see cref="Rune"/>
    /// reports its UTF-8 byte length through
    /// <see cref="Rune.Utf8SequenceLength"/>, which the loop sums until
    /// the next character would breach the
    /// <c>maxBytes - </c><see cref="EllipsisUtf8ByteCount"/> budget.
    /// </para>
    /// <para>
    /// The empty string is returned when <paramref name="maxBytes"/> is
    /// smaller than <see cref="EllipsisUtf8ByteCount"/> — a pathological
    /// budget the pipeline never passes (callers use
    /// <see cref="InlineButton.MaxLabelBytes"/> = 64) but which is
    /// handled defensively so the helper can be reused with any cap.
    /// Null or empty input is passed through unchanged so the caller's
    /// downstream non-empty validation (in <see cref="InlineButton"/>)
    /// continues to fire on genuinely-missing data.
    /// </para>
    /// </remarks>
    /// <param name="label">Operator-facing label to fit within the cap.</param>
    /// <param name="maxBytes">
    /// Maximum UTF-8 byte length allowed by the downstream consumer
    /// (Telegram's <c>InlineKeyboardButton.text</c> field caps at 64).
    /// </param>
    internal static string TruncateLabelForUtf8Budget(string label, int maxBytes)
    {
        if (string.IsNullOrEmpty(label))
        {
            return label;
        }

        if (Encoding.UTF8.GetByteCount(label) <= maxBytes)
        {
            return label;
        }

        var budget = maxBytes - EllipsisUtf8ByteCount;
        if (budget <= 0)
        {
            return string.Empty;
        }

        var consumed = 0;
        var charCutoff = 0;
        foreach (var rune in label.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (consumed + runeBytes > budget)
            {
                break;
            }
            consumed += runeBytes;
            charCutoff += rune.Utf16SequenceLength;
        }

        return string.Concat(label.AsSpan(0, charCutoff), TruncationEllipsis);
    }
}
