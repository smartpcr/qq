// -----------------------------------------------------------------------
// <copyright file="DefaultSlackMessageRendererTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Rendering;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Rendering;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 6.1 unit tests for <see cref="DefaultSlackMessageRenderer"/>.
/// Cover the three scenarios called out in implementation-plan.md
/// Stage 6.1 (question buttons, text truncation, review modal
/// structure) plus the severity / message-type styling rules,
/// <see cref="SlackInteractionEncoding"/> wire-format hand-off, and
/// the 50-block hard cap from tech-spec.md §5.2.
/// </summary>
public sealed class DefaultSlackMessageRendererTests
{
    // -----------------------------------------------------------------
    // RenderQuestion
    // -----------------------------------------------------------------

    [Fact]
    public void RenderQuestion_with_three_actions_emits_actions_block_with_three_buttons_whose_value_matches_HumanAction_Value()
    {
        AgentQuestion question = BuildQuestion(
            actions: new[]
            {
                new HumanAction("a-approve", "Approve", "approve", RequiresComment: false),
                new HumanAction("a-changes", "Request changes", "request-changes", RequiresComment: false),
                new HumanAction("a-reject", "Reject", "reject", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement blocks = SingleAttachmentBlocks(root);

        IReadOnlyList<JsonElement> actionsBlocks = BlocksOfType(blocks, "actions");
        actionsBlocks.Should().HaveCount(1, "all three buttons share RequiresComment=false");

        JsonElement elements = actionsBlocks[0].GetProperty("elements");
        elements.GetArrayLength().Should().Be(3);

        string[] buttonValues = elements.EnumerateArray()
            .Select(e => e.GetProperty("value").GetString()!)
            .ToArray();
        buttonValues.Should().Equal("approve", "request-changes", "reject");

        // Every button must be type=button with the HumanAction.ActionId
        // as action_id so the Stage 5.3 handler can resolve the originating action.
        string[] actionIds = elements.EnumerateArray()
            .Select(e => e.GetProperty("action_id").GetString()!)
            .ToArray();
        actionIds.Should().Equal("a-approve", "a-changes", "a-reject");

        elements.EnumerateArray()
            .All(e => e.GetProperty("type").GetString() == "button")
            .Should().BeTrue();
    }

    [Fact]
    public void RenderQuestion_encodes_QuestionId_in_actions_block_block_id_per_SlackInteractionEncoding()
    {
        AgentQuestion question = BuildQuestion(
            questionId: "Q-42",
            actions: new[]
            {
                new HumanAction("a1", "Approve", "approve", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement actionsBlock = BlocksOfType(SingleAttachmentBlocks(root), "actions").Single();

        string? blockId = actionsBlock.GetProperty("block_id").GetString();
        blockId.Should().NotBeNull();

        // Round-trip through the production decoder to lock the contract.
        SlackInteractionEncoding.TryDecodeQuestionBlockId(blockId, out string decodedQuestionId, out bool decodedRequiresComment)
            .Should().BeTrue();
        decodedQuestionId.Should().Be("Q-42");
        decodedRequiresComment.Should().BeFalse();
        blockId.Should().Be(SlackInteractionEncoding.QuestionBlockPrefix + "Q-42");
    }

    [Fact]
    public void RenderQuestion_splits_actions_into_two_blocks_when_RequiresComment_flag_differs_so_each_block_id_decodes_to_correct_flag()
    {
        AgentQuestion question = BuildQuestion(
            questionId: "Q-mixed",
            actions: new[]
            {
                new HumanAction("a1", "Approve", "approve", RequiresComment: false),
                new HumanAction("a2", "Reject with comment", "reject", RequiresComment: true),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        IReadOnlyList<JsonElement> actionsBlocks = BlocksOfType(SingleAttachmentBlocks(root), "actions");

        actionsBlocks.Should().HaveCount(2,
            "RequiresComment=true buttons MUST be in a separate block whose block_id starts with 'qc:'");

        HashSet<string> blockIds = actionsBlocks
            .Select(b => b.GetProperty("block_id").GetString()!)
            .ToHashSet();
        blockIds.Should().Contain(SlackInteractionEncoding.QuestionBlockPrefix + "Q-mixed");
        blockIds.Should().Contain(SlackInteractionEncoding.QuestionRequiresCommentBlockPrefix + "Q-mixed");
    }

    [Fact]
    public void RenderQuestion_emits_header_section_actions_and_context_blocks()
    {
        AgentQuestion question = BuildQuestion(
            title: "Deploy to prod?",
            body: "Approve the staging rollout candidate sha=abc.",
            actions: new[]
            {
                new HumanAction("a1", "Approve", "approve", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement blocks = SingleAttachmentBlocks(root);

        BlocksOfType(blocks, "header").Should().ContainSingle();
        IReadOnlyList<JsonElement> sectionBlocks = BlocksOfType(blocks, "section");
        sectionBlocks.Should().ContainSingle();
        BlocksOfType(blocks, "actions").Should().ContainSingle();
        BlocksOfType(blocks, "context").Should().ContainSingle();

        // Body section uses mrkdwn (per architecture.md §2.10 line 184).
        JsonElement bodySectionText = sectionBlocks[0].GetProperty("text");
        bodySectionText.GetProperty("type").GetString().Should().Be("mrkdwn");
        bodySectionText.GetProperty("text").GetString().Should().Contain("Approve the staging rollout candidate");

        // Header uses plain_text per Slack Block Kit spec.
        JsonElement headerText = BlocksOfType(blocks, "header")[0].GetProperty("text");
        headerText.GetProperty("type").GetString().Should().Be("plain_text");
        headerText.GetProperty("text").GetString().Should().EndWith("Deploy to prod?");
    }

    [Fact]
    public void RenderQuestion_context_block_includes_expiry_deadline()
    {
        DateTimeOffset deadline = new(2030, 6, 1, 12, 30, 45, TimeSpan.Zero);
        AgentQuestion question = BuildQuestion(expiresAt: deadline);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement contextBlock = BlocksOfType(SingleAttachmentBlocks(root), "context").Single();
        string? contextText = contextBlock
            .GetProperty("elements")[0]
            .GetProperty("text")
            .GetString();

        contextText.Should().Contain("2030-06-01 12:30:45 UTC");
    }

    [Theory]
    [InlineData("critical", "🔴", "#dc3545")]
    [InlineData("CRITICAL", "🔴", "#dc3545")]
    [InlineData("warning", "🟡", "#ffc107")]
    [InlineData("info", "🔵", "#0d6efd")]
    [InlineData("unknown-severity", "🔵", "#0d6efd")] // unknown -> default = info
    [InlineData("", "🔵", "#0d6efd")]                  // empty -> default = info
    public void RenderQuestion_maps_Severity_to_emoji_prefix_and_color_attachment_bar(string severity, string expectedEmoji, string expectedColor)
    {
        AgentQuestion question = BuildQuestion(severity: severity, title: "Deploy?");

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement attachment = root.GetProperty("attachments")[0];

        attachment.GetProperty("color").GetString().Should().Be(expectedColor);

        string? headerText = BlocksOfType(attachment.GetProperty("blocks"), "header")[0]
            .GetProperty("text").GetProperty("text").GetString();
        headerText.Should().StartWith(expectedEmoji);
        headerText.Should().Contain("Deploy?");
    }

    [Fact]
    public void RenderQuestion_truncates_body_at_3000_characters_with_ellipsis_indicator()
    {
        string longBody = new string('b', 5000);
        AgentQuestion question = BuildQuestion(body: longBody);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        string body = BlocksOfType(SingleAttachmentBlocks(root), "section")[0]
            .GetProperty("text").GetProperty("text").GetString()!;

        body.Length.Should().Be(DefaultSlackMessageRenderer.MaxTextFieldLength);
        body.Should().EndWith(DefaultSlackMessageRenderer.TruncationIndicator);
        body.Should().StartWith("bbb");
    }

    [Fact]
    public void RenderQuestion_chunks_overflow_actions_into_multiple_actions_blocks_so_no_HumanAction_is_dropped()
    {
        // 8 same-RequiresComment actions => chunked into 5 + 3 across
        // two actions blocks. The "one button per HumanAction" rule
        // (implementation-plan.md Stage 6.1 step 2 / scenario 1) MUST
        // be honored even past Slack's 5-buttons-per-actions-block cap.
        HumanAction[] manyActions = Enumerable.Range(1, 8)
            .Select(i => new HumanAction($"a{i}", $"Label {i}", $"v{i}", RequiresComment: false))
            .ToArray();
        AgentQuestion question = BuildQuestion(questionId: "Q-chunk", actions: manyActions);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        IReadOnlyList<JsonElement> actionsBlocks = BlocksOfType(SingleAttachmentBlocks(root), "actions");

        actionsBlocks.Should().HaveCount(2,
            "8 same-kind actions chunk into 5 + 3 buttons across 2 actions blocks");

        int[] elementCounts = actionsBlocks
            .Select(b => b.GetProperty("elements").GetArrayLength())
            .ToArray();
        elementCounts.Should().Equal(DefaultSlackMessageRenderer.MaxButtonsPerActionsBlock, 3);

        // Every original HumanAction.Value MUST be rendered exactly
        // once -- no silent dropping.
        string[] allValues = actionsBlocks
            .SelectMany(b => b.GetProperty("elements").EnumerateArray())
            .Select(e => e.GetProperty("value").GetString()!)
            .ToArray();
        allValues.Should().BeEquivalentTo(manyActions.Select(a => a.Value));

        // Slack requires unique block_ids within a message; the chunk
        // index suffix on the second block guarantees that.
        string[] blockIds = actionsBlocks
            .Select(b => b.GetProperty("block_id").GetString()!)
            .ToArray();
        blockIds.Should().OnlyHaveUniqueItems();

        // Both block_ids MUST decode back to the same QuestionId so
        // the Stage 5.3 SlackInteractionHandler publishes one
        // HumanDecisionEvent regardless of which chunk the human
        // clicked.
        foreach (string blockId in blockIds)
        {
            SlackInteractionEncoding.TryDecodeQuestionBlockId(
                blockId, out string decodedQuestionId, out bool decodedRequiresComment)
                .Should().BeTrue();
            decodedQuestionId.Should().Be("Q-chunk");
            decodedRequiresComment.Should().BeFalse();
        }
    }

    [Fact]
    public void RenderQuestion_first_chunk_uses_unsuffixed_block_id_for_backward_compatibility()
    {
        // Chunk 0 emits the canonical "q:{QID}" form (no chunk
        // prefix) so payloads with <=5 buttons remain bit-identical
        // to the pre-chunked encoding the Stage 5.3 handler already
        // reads.
        AgentQuestion question = BuildQuestion(
            questionId: "Q-bw",
            actions: new[]
            {
                new HumanAction("a1", "Approve", "approve", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        string blockId = BlocksOfType(SingleAttachmentBlocks(root), "actions").Single()
            .GetProperty("block_id").GetString()!;
        blockId.Should().Be("q:Q-bw", "chunk 0 MUST use the legacy unsuffixed prefix");
        blockId.Should().NotStartWith(SlackInteractionEncoding.QuestionChunkedBlockPrefix);
        blockId.Should().NotStartWith(SlackInteractionEncoding.QuestionChunkedRequiresCommentBlockPrefix);
    }

    // -----------------------------------------------------------------
    // Stage 6.1 iter 5 evaluator item 1: RenderQuestion MUST preserve
    // caller-supplied AllowedActions order. The previous iter grouped
    // by RequiresComment, which reordered approve / reject-with-comment
    // / defer into approve / defer / reject-with-comment. Adjacent-
    // grouping (only split when the flag flips OR the chunk hits its
    // 5-button cap) is the structural fix.
    // -----------------------------------------------------------------
    [Fact]
    public void RenderQuestion_preserves_caller_order_when_RequiresComment_flag_alternates_across_actions()
    {
        // Caller order: approve(no) -> reject-with-comment(yes) ->
        // defer(no). Expected output: three actions blocks, in
        // CALLER ORDER, with the flag-change driving the split.
        // The grouping-by-flag implementation regressed this by
        // emitting approve+defer first, then reject-with-comment.
        AgentQuestion question = BuildQuestion(
            questionId: "Q-order",
            actions: new[]
            {
                new HumanAction("a-approve", "Approve", "approve", RequiresComment: false),
                new HumanAction("a-reject", "Reject (with comment)", "reject", RequiresComment: true),
                new HumanAction("a-defer", "Defer", "defer", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        IReadOnlyList<JsonElement> actionsBlocks = BlocksOfType(SingleAttachmentBlocks(root), "actions");

        actionsBlocks.Should().HaveCount(3,
            "an alternating no/yes/no flag pattern MUST emit THREE adjacent-grouped actions blocks, "
            + "not two groups that reorder the buttons");

        // Block 1: approve only (no), legacy prefix.
        actionsBlocks[0].GetProperty("block_id").GetString()
            .Should().Be("q:Q-order", "first chunk of RequiresComment=false uses the legacy 'q:' prefix");
        actionsBlocks[0].GetProperty("elements").GetArrayLength().Should().Be(1);
        actionsBlocks[0].GetProperty("elements")[0].GetProperty("value").GetString().Should().Be("approve");

        // Block 2: reject-with-comment only (yes), legacy prefix.
        actionsBlocks[1].GetProperty("block_id").GetString()
            .Should().Be("qc:Q-order", "first chunk of RequiresComment=true uses the legacy 'qc:' prefix");
        actionsBlocks[1].GetProperty("elements").GetArrayLength().Should().Be(1);
        actionsBlocks[1].GetProperty("elements")[0].GetProperty("value").GetString().Should().Be("reject");

        // Block 3: defer only (no, SECOND group of false), chunked prefix
        // with index 1 so the block_id is Slack-unique while still
        // decoding back to Q-order.
        actionsBlocks[2].GetProperty("block_id").GetString()
            .Should().Be("qk:1:Q-order",
                "the SECOND group of RequiresComment=false MUST use the chunked prefix "
                + "with chunkIndex=1 so block_ids stay Slack-unique");
        actionsBlocks[2].GetProperty("elements").GetArrayLength().Should().Be(1);
        actionsBlocks[2].GetProperty("elements")[0].GetProperty("value").GetString().Should().Be("defer");

        // Flatten to confirm caller order is the LITERAL render order.
        string[] valuesInRenderOrder = actionsBlocks
            .SelectMany(b => b.GetProperty("elements").EnumerateArray())
            .Select(e => e.GetProperty("value").GetString()!)
            .ToArray();
        valuesInRenderOrder.Should().Equal(new[] { "approve", "reject", "defer" },
            "the rendered button order MUST match the caller's AllowedActions order EXACTLY -- "
            + "the previous GroupBy(RequiresComment) implementation rearranged this to approve/defer/reject");

        // Every chunked block_id MUST round-trip back to Q-order so the
        // Stage 5.3 handler publishes one HumanDecisionEvent regardless
        // of which group the human clicked.
        foreach (JsonElement block in actionsBlocks)
        {
            string bid = block.GetProperty("block_id").GetString()!;
            SlackInteractionEncoding.TryDecodeQuestionBlockId(
                bid, out string decodedQid, out bool decodedFlag).Should().BeTrue();
            decodedQid.Should().Be("Q-order");

            // Flag MUST match the kind of button in the block (the
            // adjacent-group invariant means every button in a block
            // shares the same RequiresComment value).
            string firstValue = block.GetProperty("elements")[0].GetProperty("value").GetString()!;
            decodedFlag.Should().Be(firstValue == "reject",
                "the block_id's decoded RequiresComment flag MUST match the buttons it carries");
        }
    }

    [Fact]
    public void RenderQuestion_preserves_caller_order_within_a_chunk_when_under_button_cap()
    {
        // Belt-and-braces: within a single chunk (all same flag, <=5
        // buttons) the order MUST be the caller's order, no
        // alphabetical or stable-sort reshuffling.
        AgentQuestion question = BuildQuestion(
            questionId: "Q-within",
            actions: new[]
            {
                new HumanAction("a-z", "Z", "z", RequiresComment: false),
                new HumanAction("a-a", "A", "a", RequiresComment: false),
                new HumanAction("a-m", "M", "m", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement block = BlocksOfType(SingleAttachmentBlocks(root), "actions").Single();
        string[] valuesInOrder = block.GetProperty("elements").EnumerateArray()
            .Select(e => e.GetProperty("value").GetString()!)
            .ToArray();
        valuesInOrder.Should().Equal(new[] { "z", "a", "m" });
    }

    // -----------------------------------------------------------------
    // RenderMessage
    // -----------------------------------------------------------------

    [Fact]
    public void RenderMessage_with_content_exceeding_3000_characters_truncates_text_with_ellipsis_indicator()
    {
        string oversized = new string('x', 5000);
        MessengerMessage message = BuildMessage(content: oversized, type: MessageType.StatusUpdate);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderMessage(message));
        string text = BlocksOfType(SingleAttachmentBlocks(root), "section")[0]
            .GetProperty("text").GetProperty("text").GetString()!;

        text.Length.Should().BeLessOrEqualTo(DefaultSlackMessageRenderer.MaxTextFieldLength,
            "Slack hard-caps a single text field at 3000 characters");
        text.Should().EndWith(DefaultSlackMessageRenderer.TruncationIndicator);
    }

    [Theory]
    [InlineData(MessageType.StatusUpdate, "ℹ️", "#0d6efd")]
    [InlineData(MessageType.Completion, "✅", "#28a745")]
    [InlineData(MessageType.Error, "❌", "#dc3545")]
    public void RenderMessage_styles_section_by_MessageType(MessageType type, string expectedEmoji, string expectedColor)
    {
        MessengerMessage message = BuildMessage(content: "deployment ok", type: type);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderMessage(message));
        JsonElement attachment = root.GetProperty("attachments")[0];
        attachment.GetProperty("color").GetString().Should().Be(expectedColor);

        string text = BlocksOfType(attachment.GetProperty("blocks"), "section")[0]
            .GetProperty("text").GetProperty("text").GetString()!;
        text.Should().StartWith(expectedEmoji);
        text.Should().Contain("deployment ok");
    }

    [Fact]
    public void RenderMessage_emits_a_single_section_block_with_mrkdwn_text()
    {
        MessengerMessage message = BuildMessage(content: "step 2 complete", type: MessageType.StatusUpdate);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderMessage(message));
        JsonElement blocks = SingleAttachmentBlocks(root);

        blocks.GetArrayLength().Should().Be(1);
        JsonElement section = blocks[0];
        section.GetProperty("type").GetString().Should().Be("section");
        section.GetProperty("text").GetProperty("type").GetString().Should().Be("mrkdwn");
    }

    // -----------------------------------------------------------------
    // RenderReviewModal (Stage 6.1 brief: read-only summary, comment
    // text input, verdict select with 3 options, submit/cancel).
    // -----------------------------------------------------------------

    [Fact]
    public void RenderReviewModal_payload_contains_text_input_select_with_three_options_and_submit_cancel()
    {
        SlackReviewModalContext context = new(
            TaskId: "TASK-77",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-77");

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderReviewModal(context));
        JsonElement blocks = root.GetProperty("blocks");

        // 1) text input block (plain_text_input, multiline).
        JsonElement textInputBlock = FindInputBlock(blocks, "plain_text_input");
        textInputBlock.GetProperty("element").GetProperty("multiline").GetBoolean().Should().BeTrue();

        // 2) select menu with exactly 3 options (approve / request-changes / reject).
        JsonElement selectBlock = FindInputBlock(blocks, "static_select");
        JsonElement options = selectBlock.GetProperty("element").GetProperty("options");
        options.GetArrayLength().Should().Be(3);
        HashSet<string> optionValues = options.EnumerateArray()
            .Select(o => o.GetProperty("value").GetString()!)
            .ToHashSet();
        optionValues.Should().BeEquivalentTo(new[] { "approve", "request-changes", "reject" });

        // 3) submit + cancel actions on the modal view itself.
        root.GetProperty("submit").GetProperty("type").GetString().Should().Be("plain_text");
        root.GetProperty("submit").GetProperty("text").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("close").GetProperty("type").GetString().Should().Be("plain_text");
        root.GetProperty("close").GetProperty("text").GetString().Should().NotBeNullOrEmpty();

        // 4) read-only summary section that references the supplied task id.
        bool hasTaskIdSummary = blocks.EnumerateArray().Any(b =>
            b.GetProperty("type").GetString() == "section"
            && b.GetProperty("text").GetProperty("text").GetString() is string s
            && s.Contains("TASK-77"));
        hasTaskIdSummary.Should().BeTrue("the brief requires a read-only task summary section");
    }

    // -----------------------------------------------------------------
    // RenderEscalateModal (Stage 6.1 brief: task context, severity
    // select, escalation reason text input, submit/cancel).
    // -----------------------------------------------------------------

    [Fact]
    public void RenderEscalateModal_payload_contains_task_context_severity_select_with_three_options_reason_input_and_submit_cancel()
    {
        SlackEscalateModalContext context = new(
            TaskId: "TASK-88",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-88");

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderEscalateModal(context));
        JsonElement blocks = root.GetProperty("blocks");

        // 1) task context summary section referencing the task id.
        bool hasTaskIdContext = blocks.EnumerateArray().Any(b =>
            b.GetProperty("type").GetString() == "section"
            && b.GetProperty("text").GetProperty("text").GetString() is string s
            && s.Contains("TASK-88"));
        hasTaskIdContext.Should().BeTrue("the brief requires a task context section");

        // 2) severity static_select with 3 options (critical / warning / info).
        JsonElement severityBlock = FindInputBlock(blocks, "static_select");
        severityBlock.GetProperty("block_id").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateSeverityBlockId);
        JsonElement severityElement = severityBlock.GetProperty("element");
        severityElement.GetProperty("action_id").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateSeveritySelectActionId);
        JsonElement options = severityElement.GetProperty("options");
        options.GetArrayLength().Should().Be(3);
        HashSet<string> optionValues = options.EnumerateArray()
            .Select(o => o.GetProperty("value").GetString()!)
            .ToHashSet();
        optionValues.Should().BeEquivalentTo(new[]
        {
            DefaultSlackMessageRenderer.SeverityCritical,
            DefaultSlackMessageRenderer.SeverityWarning,
            DefaultSlackMessageRenderer.SeverityInfo,
        });

        // 3) escalation reason plain-text input (multiline).
        JsonElement reasonBlock = blocks.EnumerateArray().Single(b =>
            b.TryGetProperty("block_id", out JsonElement bid)
            && bid.GetString() == DefaultSlackMessageRenderer.EscalateReasonBlockId);
        reasonBlock.GetProperty("type").GetString().Should().Be("input");
        JsonElement reasonElement = reasonBlock.GetProperty("element");
        reasonElement.GetProperty("type").GetString().Should().Be("plain_text_input");
        reasonElement.GetProperty("multiline").GetBoolean().Should().BeTrue();
        reasonElement.GetProperty("action_id").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateReasonInputActionId);

        // 4) submit + cancel actions on the modal view itself.
        root.GetProperty("submit").GetProperty("text").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("close").GetProperty("text").GetString().Should().NotBeNullOrEmpty();

        // 5) private_metadata still pins actionValue = "escalate" so
        // the verdict is deterministic regardless of which severity
        // the user picks (severity is escalation routing context,
        // not the verdict itself).
        string privateMetadata = root.GetProperty("private_metadata").GetString()!;
        using JsonDocument metadataDoc = JsonDocument.Parse(privateMetadata);
        metadataDoc.RootElement.GetProperty("actionValue").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateActionValue);
    }

    [Fact]
    public void RenderEscalateModal_contains_exactly_one_plain_text_input_so_handler_FirstPlainTextInputValue_unambiguously_captures_reason()
    {
        // Item 1 regression: SlackInteractionHandler.ParseView walks
        // view.state.values in block order and assigns the FIRST
        // plain_text_input value to HumanDecisionEvent.Comment. If
        // the escalate modal carried multiple plain_text_input blocks
        // (e.g. an optional "target" field rendered before "reason"),
        // a production submission filling both would publish the
        // target as Comment and silently drop the escalation
        // justification. The escalate modal MUST therefore carry
        // EXACTLY ONE plain_text_input -- the reason -- so the
        // handler's first-plain-text-input read is unambiguous.
        SlackEscalateModalContext context = new(
            TaskId: "TASK-9",
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            CorrelationId: "corr-9");

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderEscalateModal(context));
        JsonElement blocks = root.GetProperty("blocks");

        JsonElement[] plainTextInputBlocks = blocks.EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "input"
                && b.TryGetProperty("element", out JsonElement el)
                && el.TryGetProperty("type", out JsonElement t)
                && t.GetString() == "plain_text_input")
            .ToArray();

        plainTextInputBlocks.Should().HaveCount(1,
            "the escalate modal MUST carry exactly one plain_text_input so handler's first-plain-text-input read is unambiguous");

        plainTextInputBlocks[0].GetProperty("block_id").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateReasonBlockId,
                "the sole plain_text_input MUST be the escalation reason");
        plainTextInputBlocks[0].GetProperty("element").GetProperty("action_id").GetString()
            .Should().Be(DefaultSlackMessageRenderer.EscalateReasonInputActionId);

        // The optional "escalate_target" field from earlier iters
        // MUST be gone -- it was the source of the comment-capture
        // race that loses the reason when target is filled.
        bool hasOldTargetBlock = blocks.EnumerateArray().Any(b =>
            b.TryGetProperty("block_id", out JsonElement bid)
            && bid.GetString() == "escalate_target");
        hasOldTargetBlock.Should().BeFalse(
            "the optional escalate_target input was removed to prevent handler's "
            + "FirstPlainTextInputValue from capturing the target instead of the reason");
    }

    // -----------------------------------------------------------------
    // SlackInteractionEncoding chunk-suffix round-trip
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0, false, "q:Q-1")]
    [InlineData(0, true, "qc:Q-1")]
    [InlineData(1, false, "qk:1:Q-1")]
    [InlineData(2, true, "qck:2:Q-1")]
    [InlineData(17, false, "qk:17:Q-1")]
    public void SlackInteractionEncoding_EncodeQuestionBlockId_with_chunk_index_round_trips_through_decoder(
        int chunkIndex, bool requiresComment, string expectedEncoded)
    {
        string encoded = SlackInteractionEncoding.EncodeQuestionBlockId("Q-1", requiresComment, chunkIndex);
        encoded.Should().Be(expectedEncoded);

        SlackInteractionEncoding.TryDecodeQuestionBlockId(
            encoded,
            out string decodedQuestionId,
            out bool decodedRequiresComment,
            out int decodedChunk).Should().BeTrue();
        decodedQuestionId.Should().Be("Q-1");
        decodedRequiresComment.Should().Be(requiresComment);
        decodedChunk.Should().Be(chunkIndex);
    }

    [Fact]
    public void SlackInteractionEncoding_TryDecodeQuestionBlockId_legacy_three_param_overload_still_returns_questionId_for_chunked_form()
    {
        string encoded = SlackInteractionEncoding.EncodeQuestionBlockId("Q-1", requiresComment: false, chunkIndex: 3);
        SlackInteractionEncoding.TryDecodeQuestionBlockId(
            encoded, out string decodedQuestionId, out bool decodedRequiresComment)
            .Should().BeTrue();
        decodedQuestionId.Should().Be("Q-1",
            "the chunked-prefix form MUST decode to the same QuestionId so the handler resolves to a single HumanDecisionEvent");
        decodedRequiresComment.Should().BeFalse();
    }

    [Theory]
    // Item 3 regression: a question id whose body itself contains
    // "::<digits>" MUST round-trip losslessly across every chunk
    // index. With the structural prefix-based encoding the qid body
    // is opaque -- the decoder never strips anything from inside it.
    [InlineData("Q::1", 0, false, "q:Q::1")]
    [InlineData("Q::1", 0, true, "qc:Q::1")]
    [InlineData("Q::1", 1, false, "qk:1:Q::1")]
    [InlineData("Q::1", 4, true, "qck:4:Q::1")]
    [InlineData("Q:::99", 2, false, "qk:2:Q:::99")]
    [InlineData("ulid:01HX:42", 1, false, "qk:1:ulid:01HX:42")]
    [InlineData("Q::1", 7, false, "qk:7:Q::1")]
    public void SlackInteractionEncoding_round_trips_questionId_containing_double_colon_and_digits(
        string questionId, int chunkIndex, bool requiresComment, string expectedEncoded)
    {
        string encoded = SlackInteractionEncoding.EncodeQuestionBlockId(questionId, requiresComment, chunkIndex);
        encoded.Should().Be(expectedEncoded,
            "the chunk index MUST sit between the prefix and the qid so the qid body stays opaque");

        SlackInteractionEncoding.TryDecodeQuestionBlockId(
            encoded,
            out string decodedQuestionId,
            out bool decodedRequiresComment,
            out int decodedChunk).Should().BeTrue();
        decodedQuestionId.Should().Be(questionId,
            "the qid body MUST decode back to the exact original value, "
            + "including any ':' or '::' or '::<digits>' it happens to contain");
        decodedRequiresComment.Should().Be(requiresComment);
        decodedChunk.Should().Be(chunkIndex);
    }

    [Fact]
    public void SlackInteractionEncoding_chunked_prefix_does_not_collide_with_legacy_unsuffixed_prefix()
    {
        // The chunked prefixes MUST be structurally distinct from the
        // legacy unsuffixed prefixes so a legacy decoder reading a
        // chunked block_id never mistakes the chunk-index slot for a
        // QuestionId character. The longest-match rule in the decoder
        // (qck > qc, qk > q) depends on this invariant.
        SlackInteractionEncoding.QuestionChunkedBlockPrefix
            .Should().StartWith(SlackInteractionEncoding.QuestionBlockPrefix[..1])
            .And.NotBe(SlackInteractionEncoding.QuestionBlockPrefix);
        SlackInteractionEncoding.QuestionChunkedRequiresCommentBlockPrefix
            .Should().StartWith(SlackInteractionEncoding.QuestionRequiresCommentBlockPrefix[..2])
            .And.NotBe(SlackInteractionEncoding.QuestionRequiresCommentBlockPrefix);
    }

    // -----------------------------------------------------------------
    // Stage 6.1 iter 5 evaluator item 3: EncodeQuestionBlockId MUST
    // enforce Slack's 255-character block_id cap so the renderer never
    // builds a Block Kit payload that Slack would reject at
    // chat.postMessage time.
    // -----------------------------------------------------------------

    [Fact]
    public void SlackInteractionEncoding_EncodeQuestionBlockId_at_exactly_the_255_char_cap_succeeds()
    {
        // Prefix "q:" is 2 chars, so a 253-char QuestionId produces an
        // encoded block_id of exactly 255 chars (the cap). At the
        // boundary the encoder MUST succeed.
        int prefixLen = SlackInteractionEncoding.QuestionBlockPrefix.Length;
        string qid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen);
        string encoded = SlackInteractionEncoding.EncodeQuestionBlockId(qid, requiresComment: false);
        encoded.Length.Should().Be(SlackInteractionEncoding.MaxBlockIdLength,
            "a 253-char QuestionId + 'q:' prefix MUST produce an encoded block_id of exactly 255 chars");
    }

    [Fact]
    public void SlackInteractionEncoding_EncodeQuestionBlockId_one_char_above_the_255_char_cap_throws_ArgumentException()
    {
        // One character past the cap MUST throw -- Slack rejects any
        // block_id above MaxBlockIdLength. Failing fast at the encoder
        // surfaces the problem at rendering time rather than as a
        // surprise 400 from chat.postMessage.
        int prefixLen = SlackInteractionEncoding.QuestionBlockPrefix.Length;
        string qid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen + 1);

        Action act = () => SlackInteractionEncoding.EncodeQuestionBlockId(qid, requiresComment: false);
        act.Should().Throw<ArgumentException>()
            .Where(e => e.ParamName == "questionId",
                "the failing parameter is the QuestionId whose length pushes the encoded block_id over the cap");
    }

    [Fact]
    public void SlackInteractionEncoding_EncodeQuestionBlockId_enforces_cap_for_requires_comment_prefix()
    {
        // qc: prefix is 3 chars; a 253-char QuestionId would produce
        // a 256-char encoded block_id (one past the cap) so it MUST
        // throw, while 252 chars (255 total) MUST succeed.
        int prefixLen = SlackInteractionEncoding.QuestionRequiresCommentBlockPrefix.Length;
        string okQid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen);
        string overQid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen + 1);

        SlackInteractionEncoding.EncodeQuestionBlockId(okQid, requiresComment: true)
            .Length.Should().Be(SlackInteractionEncoding.MaxBlockIdLength);

        Action act = () => SlackInteractionEncoding.EncodeQuestionBlockId(overQid, requiresComment: true);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(99999)]
    public void SlackInteractionEncoding_EncodeQuestionBlockId_enforces_cap_for_chunked_form_including_chunk_index_digits(int chunkIndex)
    {
        // Chunked prefix is "qk:" (3) + digits + ":" + qid, so the
        // headroom for qid shrinks as chunkIndex grows. The encoder
        // MUST account for the actual digit width when checking the
        // cap, not just the prefix length.
        string digits = chunkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int prefixLen = SlackInteractionEncoding.QuestionChunkedBlockPrefix.Length
            + digits.Length + 1; // +1 for the ':' between digits and qid
        string okQid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen);
        string overQid = new('q', SlackInteractionEncoding.MaxBlockIdLength - prefixLen + 1);

        SlackInteractionEncoding.EncodeQuestionBlockId(okQid, requiresComment: false, chunkIndex)
            .Length.Should().Be(SlackInteractionEncoding.MaxBlockIdLength);

        Action act = () => SlackInteractionEncoding.EncodeQuestionBlockId(overQid, requiresComment: false, chunkIndex);
        act.Should().Throw<ArgumentException>(
            "the cap MUST be checked on the FINAL encoded string so the digit-width of the chunk index counts against the budget");
    }

    // -----------------------------------------------------------------
    // 50-block hard cap (tech-spec.md §5.2) -- DefaultSlackMessageRenderer
    // MUST guarantee no Block Kit payload exceeds MaxBlocksPerMessage,
    // and when truncation kicks in MUST emit a visible marker so the
    // recipient knows content is missing.
    // -----------------------------------------------------------------

    [Fact]
    public void RenderQuestion_overflowing_50_block_cap_truncates_to_exactly_MaxBlocksPerMessage_and_appends_blocks_truncated_marker_as_last_block()
    {
        // RenderQuestion emits: 1 header + 1 body + N actions blocks
        // (where N = ceil(actions / MaxButtonsPerActionsBlock)) + 1
        // context block. To force the EnforceBlockLimit overflow path
        // we need N >= MaxBlocksPerMessage - 2 (so total >= 51).
        // With MaxButtonsPerActionsBlock = 5 and MaxBlocksPerMessage = 50,
        // 250 same-RequiresComment actions => 50 actions blocks =>
        // 53 total blocks pre-cap => the cap MUST drop the tail and
        // append the blocks_truncated marker so the final count is
        // exactly 50.
        int actionCount = DefaultSlackMessageRenderer.MaxButtonsPerActionsBlock
            * DefaultSlackMessageRenderer.MaxBlocksPerMessage;
        HumanAction[] tooManyActions = Enumerable.Range(1, actionCount)
            .Select(i => new HumanAction($"a{i}", $"Label {i}", $"v{i}", RequiresComment: false))
            .ToArray();
        AgentQuestion question = BuildQuestion(questionId: "Q-50cap", actions: tooManyActions);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement blocks = SingleAttachmentBlocks(root);

        blocks.GetArrayLength().Should().Be(
            DefaultSlackMessageRenderer.MaxBlocksPerMessage,
            "Slack hard-caps a Block Kit message at MaxBlocksPerMessage (tech-spec.md §5.2) "
            + "so EnforceBlockLimit MUST drop the overflow and keep the post-cap count at the limit");

        // Stage 6.1 evaluator item 2 (iter 5): the expiry context block
        // MUST be preserved on overflow because the brief requires a
        // context block showing ExpiresAt. The renderer now passes the
        // expiry context as a required tail to EnforceBlockLimit, so on
        // overflow the LAST block is the expiry context and the
        // truncation marker sits IMMEDIATELY BEFORE it.
        JsonElement lastBlock = blocks[blocks.GetArrayLength() - 1];
        lastBlock.GetProperty("type").GetString().Should().Be("context");
        lastBlock.GetProperty("block_id").GetString()
            .Should().Be("question_expiry",
                "the expiry context MUST always be the final block so the recipient "
                + "always sees the ExpiresAt deadline (Stage 6.1 evaluator item 2)");

        JsonElement markerBlock = blocks[blocks.GetArrayLength() - 2];
        markerBlock.GetProperty("type").GetString().Should().Be("context");
        markerBlock.GetProperty("block_id").GetString()
            .Should().Be("blocks_truncated",
                "the truncation marker MUST use a stable block_id so downstream "
                + "consumers / tests can recognise that content was dropped");

        // The marker text MUST surface BOTH the omitted-block count
        // and the limit, so an operator reading the message can see
        // exactly how much was lost.
        string markerText = markerBlock.GetProperty("elements")[0].GetProperty("text").GetString()!;
        markerText.Should().Contain(
            DefaultSlackMessageRenderer.MaxBlocksPerMessage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "the marker text MUST reference the 50-block limit");
        markerText.Should().Contain("truncated");
        markerText.Should().Contain("omitted");

        // Sanity check: no other block in the kept output uses the
        // blocks_truncated block_id (would indicate the marker
        // collided with a real block). Exactly one truncation marker.
        int markerCount = blocks.EnumerateArray()
            .Count(b => b.TryGetProperty("block_id", out JsonElement bid)
                && bid.GetString() == "blocks_truncated");
        markerCount.Should().Be(1, "exactly one blocks_truncated marker MUST be emitted");

        // Sanity check: exactly one expiry context (never dropped).
        int expiryCount = blocks.EnumerateArray()
            .Count(b => b.TryGetProperty("block_id", out JsonElement bid)
                && bid.GetString() == "question_expiry");
        expiryCount.Should().Be(1, "the expiry context MUST be preserved exactly once on overflow");
    }

    [Fact]
    public void RenderQuestion_overflowing_action_payload_preserves_expiry_context_block()
    {
        // Stage 6.1 iter 5 evaluator item 2 (standalone pin): a payload
        // whose action blocks alone exceed the 50-block cap MUST still
        // carry the question_expiry context block because the brief
        // requires "a context block showing ExpiresAt deadline" and
        // that promise cannot be silently broken just because there
        // are a lot of buttons. The previous iter appended the context
        // AFTER the action blocks, then truncated the tail; that
        // dropped the expiry.
        int actionCount = DefaultSlackMessageRenderer.MaxButtonsPerActionsBlock
            * DefaultSlackMessageRenderer.MaxBlocksPerMessage;
        HumanAction[] tooManyActions = Enumerable.Range(1, actionCount)
            .Select(i => new HumanAction($"a{i}", $"Label {i}", $"v{i}", RequiresComment: false))
            .ToArray();
        DateTimeOffset deadline = new(2030, 6, 1, 12, 30, 45, TimeSpan.Zero);
        AgentQuestion question = BuildQuestion(
            questionId: "Q-expiry-survives",
            actions: tooManyActions,
            expiresAt: deadline);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement blocks = SingleAttachmentBlocks(root);

        blocks.GetArrayLength().Should().Be(DefaultSlackMessageRenderer.MaxBlocksPerMessage);

        // Find the expiry context (block_id="question_expiry") and
        // confirm it carries the deadline text.
        JsonElement[] expiryBlocks = blocks.EnumerateArray()
            .Where(b => b.TryGetProperty("block_id", out JsonElement bid)
                && bid.GetString() == "question_expiry")
            .ToArray();
        expiryBlocks.Should().HaveCount(1,
            "the expiry context MUST be preserved on overflow -- it is the brief-required ExpiresAt deadline display");
        expiryBlocks[0].GetProperty("type").GetString().Should().Be("context");
        string expiryText = expiryBlocks[0].GetProperty("elements")[0]
            .GetProperty("text").GetString()!;
        expiryText.Should().Contain("2030-06-01 12:30:45 UTC",
            "the surviving expiry context MUST carry the formatted deadline");
    }

    [Fact]
    public void RenderQuestion_within_50_block_cap_does_not_emit_blocks_truncated_marker()
    {
        // Boundary pin: a payload that stays at or below
        // MaxBlocksPerMessage MUST NOT carry the truncation marker --
        // the marker is exclusively an overflow signal and emitting
        // it for a non-overflowing payload would mislead the
        // recipient.
        AgentQuestion question = BuildQuestion(
            questionId: "Q-tiny",
            actions: new[]
            {
                new HumanAction("a1", "Approve", "approve", RequiresComment: false),
                new HumanAction("a2", "Reject", "reject", RequiresComment: false),
            });

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderQuestion(question));
        JsonElement blocks = SingleAttachmentBlocks(root);

        blocks.GetArrayLength().Should().BeLessOrEqualTo(DefaultSlackMessageRenderer.MaxBlocksPerMessage);

        bool hasTruncationMarker = blocks.EnumerateArray()
            .Any(b => b.TryGetProperty("block_id", out JsonElement bid)
                && bid.GetString() == "blocks_truncated");
        hasTruncationMarker.Should().BeFalse(
            "the blocks_truncated marker MUST only appear when EnforceBlockLimit "
            + "actually drops blocks; a non-overflowing payload MUST NOT carry it");
    }

    [Fact]
    public void RenderMessage_with_oversized_content_stays_within_50_block_cap_and_emits_no_truncation_marker()
    {
        // RenderMessage emits a single section block by design, so it
        // never overflows the 50-block cap regardless of content
        // length (text overflow is handled separately by the 3000-char
        // truncation path tested above). This pins that invariant so
        // a future refactor that starts splitting content into
        // multiple blocks must also honor the cap.
        MessengerMessage message = BuildMessage(
            content: new string('y', 50_000),
            type: MessageType.StatusUpdate);

        JsonElement root = Serialize(new DefaultSlackMessageRenderer().RenderMessage(message));
        JsonElement blocks = SingleAttachmentBlocks(root);

        blocks.GetArrayLength().Should().BeLessOrEqualTo(DefaultSlackMessageRenderer.MaxBlocksPerMessage);
        bool hasTruncationMarker = blocks.EnumerateArray()
            .Any(b => b.TryGetProperty("block_id", out JsonElement bid)
                && bid.GetString() == "blocks_truncated");
        hasTruncationMarker.Should().BeFalse(
            "RenderMessage emits a single section so it cannot overflow the 50-block cap");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static AgentQuestion BuildQuestion(
        string questionId = "Q-1",
        string title = "Title",
        string body = "Body",
        string severity = "info",
        HumanAction[]? actions = null,
        DateTimeOffset? expiresAt = null)
    {
        return new AgentQuestion(
            QuestionId: questionId,
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: title,
            Body: body,
            Severity: severity,
            AllowedActions: actions ?? Array.Empty<HumanAction>(),
            ExpiresAt: expiresAt ?? new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CorrelationId: "corr-1");
    }

    private static MessengerMessage BuildMessage(
        string content,
        MessageType type,
        string messageId = "msg-1")
    {
        return new MessengerMessage(
            MessageId: messageId,
            AgentId: "agent-1",
            TaskId: "task-1",
            Content: content,
            MessageType: type,
            CorrelationId: "corr-1",
            Timestamp: new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    private static JsonElement Serialize(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement SingleAttachmentBlocks(JsonElement root)
    {
        root.TryGetProperty("attachments", out JsonElement attachments)
            .Should().BeTrue("Stage 6.1 wraps blocks in a legacy attachment so Slack paints the color sidebar");
        attachments.GetArrayLength().Should().Be(1);
        return attachments[0].GetProperty("blocks");
    }

    private static IReadOnlyList<JsonElement> BlocksOfType(JsonElement blocks, string type)
        => blocks.EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == type)
            .ToList();

    private static JsonElement FindInputBlock(JsonElement blocks, string elementType)
    {
        foreach (JsonElement block in blocks.EnumerateArray())
        {
            if (block.GetProperty("type").GetString() != "input")
            {
                continue;
            }

            if (block.TryGetProperty("element", out JsonElement element)
                && element.TryGetProperty("type", out JsonElement t)
                && t.GetString() == elementType)
            {
                return block;
            }
        }

        throw new InvalidOperationException($"No input block with element.type='{elementType}' found.");
    }
}
