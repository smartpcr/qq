// -----------------------------------------------------------------------
// <copyright file="SlackInteractionEncoding.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Rendering;

using System;
using System.Globalization;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Wire-level encoding contract shared by the Stage 6.1
/// <see cref="ISlackMessageRenderer"/> (when it serialises an
/// <see cref="AgentQuestion"/> into Block Kit) and the Stage 5.3
/// <see cref="Pipeline.SlackInteractionHandler"/> (when it decodes a
/// Block Kit interactive payload back into the original
/// <c>QuestionId</c> + <c>RequiresComment</c> flag).
/// </summary>
/// <remarks>
/// <para>
/// Architecture.md §5.2 (lines 629-634) says <c>QuestionId</c> "is
/// extracted from the button's <c>block_id</c> (encoded during
/// rendering)". The architecture does NOT pin a specific encoding;
/// this class owns that contract for the Slack connector so the
/// renderer and the handler cannot drift.
/// </para>
/// <para>
/// Encoding rules (4 prefixes, longest-match wins):
/// </para>
/// <list type="bullet">
///   <item><description><c>q:{QuestionId}</c> -- a normal action
///   button, chunk index 0 (legacy unsuffixed form). Clicking it
///   produces a <c>HumanDecisionEvent</c> directly, no comment
///   modal.</description></item>
///   <item><description><c>qc:{QuestionId}</c> -- an action button
///   whose backing <see cref="HumanAction.RequiresComment"/> is
///   <see langword="true"/>, chunk index 0. Clicking it triggers a
///   follow-up modal (<c>views.open</c>) carrying a free-text input,
///   INSTEAD of publishing a decision directly. The modal's
///   <c>private_metadata</c> carries the original action value so the
///   subsequent <c>view_submission</c> can reconstruct the
///   <c>HumanDecisionEvent</c>.</description></item>
///   <item><description><c>qk:{N}:{QuestionId}</c> -- the SAME
///   semantics as <c>q:</c> but for chunk index N &#8805; 1. Emitted
///   when an <see cref="AgentQuestion"/> has more buttons than
///   Slack's 5-per-<c>actions</c>-block limit so the renderer can
///   emit multiple actions blocks with Slack-unique <c>block_id</c>s
///   that all decode back to the same
///   <see cref="AgentQuestion.QuestionId"/>. The chunk index lives
///   between the prefix and the question id, so the question id
///   body is opaque and may contain any character (including
///   <c>::</c> or <c>::5</c>) without ever being misinterpreted as
///   a chunk suffix.</description></item>
///   <item><description><c>qck:{N}:{QuestionId}</c> -- chunked
///   counterpart of <c>qc:</c> for N &#8805; 1.</description></item>
/// </list>
/// <para>
/// All four prefixes are short by design: Slack caps <c>block_id</c>
/// at 255 characters and the prefix + chunk-index + separator budget
/// needs to stay inside the <see cref="AgentQuestion.QuestionId"/>
/// length headroom.
/// </para>
/// </remarks>
internal static class SlackInteractionEncoding
{
    /// <summary>Prefix marking a normal action button (chunk 0, no follow-up modal).</summary>
    public const string QuestionBlockPrefix = "q:";

    /// <summary>Prefix marking an action button that requires a follow-up comment modal (chunk 0).</summary>
    public const string QuestionRequiresCommentBlockPrefix = "qc:";

    /// <summary>
    /// Prefix marking a CHUNKED non-comment actions block (chunk
    /// index &#8805; 1). Encoding: <c>qk:&lt;chunkIndex&gt;:&lt;questionId&gt;</c>.
    /// The chunk index is parsed greedily as decimal digits up to
    /// the FIRST <c>:</c> after the prefix; everything after that
    /// colon is the raw <c>questionId</c> and may legally contain
    /// any character (including <c>:</c>, <c>::</c>, or <c>::5</c>).
    /// </summary>
    public const string QuestionChunkedBlockPrefix = "qk:";

    /// <summary>
    /// Prefix marking a CHUNKED requires-comment actions block
    /// (chunk index &#8805; 1). Encoding:
    /// <c>qck:&lt;chunkIndex&gt;:&lt;questionId&gt;</c>. Same parsing
    /// rules as <see cref="QuestionChunkedBlockPrefix"/>.
    /// </summary>
    public const string QuestionChunkedRequiresCommentBlockPrefix = "qck:";

    /// <summary><c>callback_id</c> used by the comment modal opened in response to a RequiresComment button click.</summary>
    public const string CommentCallbackId = "agent_comment_modal";

    /// <summary>Encodes a button's <c>block_id</c> for a question / action pairing.</summary>
    /// <param name="questionId">Originating <see cref="AgentQuestion.QuestionId"/>.</param>
    /// <param name="requiresComment">Value of the backing
    /// <see cref="HumanAction.RequiresComment"/> flag.</param>
    public static string EncodeQuestionBlockId(string questionId, bool requiresComment)
        => EncodeQuestionBlockId(questionId, requiresComment, chunkIndex: 0);

    /// <summary>
    /// Chunk-aware overload: encodes a button's <c>block_id</c> with
    /// an optional numeric chunk index. <paramref name="chunkIndex"/> = 0
    /// emits the canonical legacy form (<c>q:&lt;qid&gt;</c> /
    /// <c>qc:&lt;qid&gt;</c>) so all existing payloads remain
    /// bit-identical; positive values emit a STRUCTURALLY DIFFERENT
    /// chunked form (<c>qk:&lt;n&gt;:&lt;qid&gt;</c> /
    /// <c>qck:&lt;n&gt;:&lt;qid&gt;</c>) whose prefix can NEVER overlap
    /// with the legacy unsuffixed forms. The chunk index sits BETWEEN
    /// the chunked prefix and the question id, so the
    /// <paramref name="questionId"/> body is opaque -- it may contain
    /// any character including <c>::</c>, <c>::1</c>, etc., without
    /// ever being misinterpreted as a chunk suffix.
    /// </summary>
    /// <param name="questionId">Originating <see cref="AgentQuestion.QuestionId"/>.</param>
    /// <param name="requiresComment">Value of the backing
    /// <see cref="HumanAction.RequiresComment"/> flag.</param>
    /// <param name="chunkIndex">Zero-based chunk index; 0 = legacy
    /// unsuffixed form, 1+ = chunked prefix + index + raw qid.</param>
    public static string EncodeQuestionBlockId(string questionId, bool requiresComment, int chunkIndex)
    {
        if (string.IsNullOrEmpty(questionId))
        {
            throw new ArgumentException("questionId must be non-empty.", nameof(questionId));
        }

        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "chunkIndex must be non-negative.");
        }

        if (chunkIndex == 0)
        {
            string legacyPrefix = requiresComment ? QuestionRequiresCommentBlockPrefix : QuestionBlockPrefix;
            return legacyPrefix + questionId;
        }

        string chunkedPrefix = requiresComment
            ? QuestionChunkedRequiresCommentBlockPrefix
            : QuestionChunkedBlockPrefix;
        return string.Concat(
            chunkedPrefix,
            chunkIndex.ToString(CultureInfo.InvariantCulture),
            ":",
            questionId);
    }

    /// <summary>
    /// Decodes a button's <c>block_id</c> produced by
    /// <see cref="EncodeQuestionBlockId(string, bool)"/>. Returns
    /// <see langword="false"/> when the supplied value does not match
    /// any of the recognised prefixes (defensive against malformed
    /// payloads -- the caller logs and short-circuits rather than
    /// throwing).
    /// </summary>
    /// <param name="blockId">Raw <c>block_id</c> from the Slack
    /// interactive payload.</param>
    /// <param name="questionId">Receives the decoded question id when
    /// the method returns <see langword="true"/>.</param>
    /// <param name="requiresComment">Receives the decoded
    /// requires-comment flag when the method returns
    /// <see langword="true"/>.</param>
    public static bool TryDecodeQuestionBlockId(
        string? blockId,
        out string questionId,
        out bool requiresComment)
        => TryDecodeQuestionBlockId(blockId, out questionId, out requiresComment, out _);

    /// <summary>
    /// Chunk-aware overload of <see cref="TryDecodeQuestionBlockId(string?, out string, out bool)"/>:
    /// additionally returns the chunk index parsed from the chunked
    /// prefix (0 when the input is in the legacy unsuffixed form).
    /// The returned <paramref name="questionId"/> is the RAW value
    /// originally passed to <see cref="EncodeQuestionBlockId(string, bool, int)"/>
    /// -- the chunk index lives in its own slot between the prefix
    /// and the question id, so chunked and non-chunked block_ids
    /// for the same <see cref="AgentQuestion.QuestionId"/> collapse
    /// to the same decoded value (and question ids containing
    /// <c>::&lt;digits&gt;</c> round-trip losslessly).
    /// </summary>
    public static bool TryDecodeQuestionBlockId(
        string? blockId,
        out string questionId,
        out bool requiresComment,
        out int chunkIndex)
    {
        questionId = string.Empty;
        requiresComment = false;
        chunkIndex = 0;
        if (string.IsNullOrEmpty(blockId))
        {
            return false;
        }

        // Prefix precedence MUST be longest-first because shorter
        // prefixes are prefixes of longer ones (qck > qc; qk > q).
        // qck: -> chunked + requires-comment
        // qk:  -> chunked, no comment
        // qc:  -> legacy + requires-comment (chunk 0)
        // q:   -> legacy, no comment (chunk 0)
        if (blockId.StartsWith(QuestionChunkedRequiresCommentBlockPrefix, StringComparison.Ordinal))
        {
            requiresComment = true;
            return TryParseChunkedRemainder(
                blockId[QuestionChunkedRequiresCommentBlockPrefix.Length..],
                out chunkIndex,
                out questionId);
        }

        if (blockId.StartsWith(QuestionChunkedBlockPrefix, StringComparison.Ordinal))
        {
            return TryParseChunkedRemainder(
                blockId[QuestionChunkedBlockPrefix.Length..],
                out chunkIndex,
                out questionId);
        }

        if (blockId.StartsWith(QuestionRequiresCommentBlockPrefix, StringComparison.Ordinal))
        {
            requiresComment = true;
            questionId = blockId[QuestionRequiresCommentBlockPrefix.Length..];
            return questionId.Length > 0;
        }

        if (blockId.StartsWith(QuestionBlockPrefix, StringComparison.Ordinal))
        {
            questionId = blockId[QuestionBlockPrefix.Length..];
            return questionId.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Parses the post-prefix tail of a chunked block_id
    /// (<c>&lt;digits&gt;:&lt;rawQuestionId&gt;</c>). The chunk index
    /// is everything up to the FIRST <c>:</c>, and must be a positive
    /// integer (chunk 0 is encoded with the legacy prefixes, never
    /// the chunked ones). The question id is the literal rest of the
    /// string and is NEVER post-processed -- this is what guarantees
    /// QIDs containing <c>:</c> or <c>::</c> round-trip cleanly.
    /// </summary>
    private static bool TryParseChunkedRemainder(string remainder, out int chunkIndex, out string questionId)
    {
        chunkIndex = 0;
        questionId = string.Empty;
        int colonIdx = remainder.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx <= 0 || colonIdx == remainder.Length - 1)
        {
            return false;
        }

        string digits = remainder[..colonIdx];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedIndex)
            || parsedIndex <= 0)
        {
            return false;
        }

        chunkIndex = parsedIndex;
        questionId = remainder[(colonIdx + 1)..];
        return questionId.Length > 0;
    }
}
