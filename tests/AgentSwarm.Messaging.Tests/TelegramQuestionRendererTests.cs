using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

public sealed class TelegramQuestionRendererTests
{
    private static AgentQuestion BuildQuestion(
        string title = "Deploy Solution12?",
        string body = "Pre-flight clean. Stage now?",
        MessageSeverity severity = MessageSeverity.High,
        int actionCount = 2,
        bool actionRequiresComment = false,
        TimeSpan? expiresIn = null,
        string correlationId = "trace-7f3a") =>
        new()
        {
            QuestionId = "q-001",
            AgentId = "agent-deployer",
            TaskId = "task-12",
            Title = title,
            Body = body,
            Severity = severity,
            AllowedActions = Enumerable.Range(0, actionCount)
                .Select(i => new HumanAction
                {
                    ActionId = "a" + i,
                    Label = "Action " + i,
                    Value = "value-" + i,
                    RequiresComment = actionRequiresComment && i == 0,
                })
                .ToList(),
            ExpiresAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z") + (expiresIn ?? TimeSpan.FromMinutes(15)),
            CorrelationId = correlationId,
        };

    private static FakeTimeProvider FixedTime() =>
        new(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));

    [Fact]
    public void BuildInlineKeyboard_ProducesOneRowPerAction()
    {
        var q = BuildQuestion(actionCount: 3);
        var keyboard = TelegramQuestionRenderer.BuildInlineKeyboard(q);

        keyboard.InlineKeyboard.Should().HaveCount(3, "each AllowedAction renders on its own row");
        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        buttons.Should().HaveCount(3);
        buttons[0].CallbackData.Should().Be("q-001:a0");
        buttons[1].CallbackData.Should().Be("q-001:a1");
        buttons[2].CallbackData.Should().Be("q-001:a2");
    }

    [Fact]
    public void BuildInlineKeyboard_AppendsRequiresCommentSuffix()
    {
        var q = BuildQuestion(actionCount: 2, actionRequiresComment: true);
        var keyboard = TelegramQuestionRenderer.BuildInlineKeyboard(q);

        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        buttons[0].Text.Should().EndWith(TelegramQuestionRenderer.RequiresCommentSuffix,
            "RequiresComment=true must surface in the button label so the operator knows to type a reply");
        buttons[1].Text.Should().NotEndWith(TelegramQuestionRenderer.RequiresCommentSuffix);
    }

    [Fact]
    public void BuildBody_IncludesSeverityTimeoutBodyDefaultActionAndTraceFooter()
    {
        var time = FixedTime();
        var q = BuildQuestion(severity: MessageSeverity.Critical, expiresIn: TimeSpan.FromMinutes(15));
        var envelope = new AgentQuestionEnvelope
        {
            Question = q,
            ProposedDefaultActionId = "a1",
        };

        var body = TelegramQuestionRenderer.BuildBody(envelope, time);

        body.Should().Contain("Deploy Solution12", "title must appear");
        body.Should().Contain("Pre\\-flight clean", "body must appear (MarkdownV2 escapes hyphens)");
        body.Should().Contain("Severity: Critical", "severity badge + label must appear");
        body.Should().Contain("Times out in 15 min", "timeout countdown must appear");
        body.Should().Contain("Default action if no response: Action 1",
            "proposed default action label must be displayed");
        body.Should().Contain("trace: trace\\-7f3a", "correlation id footer per architecture §10.1");
    }

    [Fact]
    public void BuildBody_OmitsDefaultActionLine_WhenProposedDefaultActionIdIsNull()
    {
        var time = FixedTime();
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        var body = TelegramQuestionRenderer.BuildBody(envelope, time);

        body.Should().NotContain("Default action if no response",
            "no default-action line when ProposedDefaultActionId is null");
    }

    [Fact]
    public async Task CacheActionsAsync_WritesOnePayloadPerActionWithGracePeriodExpiry()
    {
        var time = FixedTime();
        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var q = BuildQuestion(actionCount: 2, expiresIn: TimeSpan.FromMinutes(10));

        await TelegramQuestionRenderer.CacheActionsAsync(q, cache, time, CancellationToken.None);

        var bytes0 = await cache.GetAsync("q-001:a0", CancellationToken.None);
        var bytes1 = await cache.GetAsync("q-001:a1", CancellationToken.None);
        bytes0.Should().NotBeNull("a0 must have been cached");
        bytes1.Should().NotBeNull("a1 must have been cached");

        var roundTripped = JsonSerializer.Deserialize<HumanAction>(bytes0!);
        roundTripped!.ActionId.Should().Be("a0");
        roundTripped.Value.Should().Be("value-0");
    }
}
