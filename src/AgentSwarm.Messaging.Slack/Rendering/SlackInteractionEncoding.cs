// -----------------------------------------------------------------------
// <copyright file="SlackInteractionEncoding.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Rendering;

using System;
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
/// Encoding rules:
/// </para>
/// <list type="bullet">
///   <item><description><c>q:{QuestionId}</c> -- a normal action
///   button. Clicking it produces a <c>HumanDecisionEvent</c> directly,
///   no comment modal.</description></item>
///   <item><description><c>qc:{QuestionId}</c> -- an action button
///   whose backing <see cref="HumanAction.RequiresComment"/> is
///   <see langword="true"/>. Clicking it triggers a follow-up modal
///   (<c>views.open</c>) carrying a free-text input, INSTEAD of
///   publishing a decision directly. The modal's
///   <c>private_metadata</c> carries the original action value so the
///   subsequent <c>view_submission</c> can reconstruct the
///   <c>HumanDecisionEvent</c>.</description></item>
/// </list>
/// <para>
/// Both prefixes are short by design: Slack caps <c>block_id</c> at
/// 255 characters and the prefix budget needs to stay inside the
/// <see cref="AgentQuestion.QuestionId"/> length headroom.
/// </para>
/// </remarks>
internal static class SlackInteractionEncoding
{
    /// <summary>Prefix marking a normal action button.</summary>
    public const string QuestionBlockPrefix = "q:";

    /// <summary>Prefix marking an action button that requires a follow-up comment modal.</summary>
    public const string QuestionRequiresCommentBlockPrefix = "qc:";

    /// <summary><c>callback_id</c> used by the comment modal opened in response to a RequiresComment button click.</summary>
    public const string CommentCallbackId = "agent_comment_modal";

    /// <summary>Encodes a button's <c>block_id</c> for a question / action pairing.</summary>
    /// <param name="questionId">Originating <see cref="AgentQuestion.QuestionId"/>.</param>
    /// <param name="requiresComment">Value of the backing
    /// <see cref="HumanAction.RequiresComment"/> flag.</param>
    public static string EncodeQuestionBlockId(string questionId, bool requiresComment)
    {
        if (string.IsNullOrEmpty(questionId))
        {
            throw new ArgumentException("questionId must be non-empty.", nameof(questionId));
        }

        return (requiresComment ? QuestionRequiresCommentBlockPrefix : QuestionBlockPrefix) + questionId;
    }

    /// <summary>
    /// Decodes a button's <c>block_id</c> produced by
    /// <see cref="EncodeQuestionBlockId"/>. Returns
    /// <see langword="false"/> when the supplied value does not match
    /// either of the recognised prefixes (defensive against malformed
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
    {
        questionId = string.Empty;
        requiresComment = false;
        if (string.IsNullOrEmpty(blockId))
        {
            return false;
        }

        // qc: MUST be checked before q: because the longer prefix
        // takes precedence (both prefixes share the same leading 'q').
        if (blockId.StartsWith(QuestionRequiresCommentBlockPrefix, StringComparison.Ordinal))
        {
            questionId = blockId[QuestionRequiresCommentBlockPrefix.Length..];
            requiresComment = true;
            return questionId.Length > 0;
        }

        if (blockId.StartsWith(QuestionBlockPrefix, StringComparison.Ordinal))
        {
            questionId = blockId[QuestionBlockPrefix.Length..];
            return questionId.Length > 0;
        }

        return false;
    }
}
