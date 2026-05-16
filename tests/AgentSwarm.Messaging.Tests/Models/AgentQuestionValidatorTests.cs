using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class AgentQuestionValidatorTests
{
    private static AgentQuestion BuildQuestion(params HumanAction[] actions) =>
        new(
            QuestionId: "Q-1",
            AgentId: "agent",
            TaskId: "task",
            Title: "title",
            Body: "body",
            Severity: MessageSeverity.Normal,
            AllowedActions: actions,
            ExpiresAt: DateTimeOffset.UnixEpoch,
            CorrelationId: "trace");

    [Fact]
    public void TryValidate_AcceptsWellFormedQuestion()
    {
        var question = BuildQuestion(
            new HumanAction("approve", "Approve", "approved", false),
            new HumanAction("reject", "Reject", "rejected", true));

        var ok = AgentQuestionValidator.TryValidate(question, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_RejectsNullQuestion()
    {
        var ok = AgentQuestionValidator.TryValidate((AgentQuestion?)null, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("null");
    }

    [Fact]
    public void TryValidate_RejectsInvalidQuestionId()
    {
        // Bypass the AgentQuestion constructor's lack of validation by using a
        // QuestionId that fails QuestionIdValidator (contains ':').
        var question = new AgentQuestion(
            QuestionId: "bad:id",
            AgentId: "agent",
            TaskId: "task",
            Title: "t",
            Body: "b",
            Severity: MessageSeverity.Normal,
            AllowedActions: new[] { new HumanAction("ok", "OK", "ok", false) },
            ExpiresAt: DateTimeOffset.UnixEpoch,
            CorrelationId: "c");

        var ok = AgentQuestionValidator.TryValidate(question, out var error);

        ok.Should().BeFalse();
        error.Should().Contain(":");
    }

    [Fact]
    public void TryValidate_RejectsInvalidActionId()
    {
        var question = BuildQuestion(
            new HumanAction("ok", "OK", "ok", false),
            new HumanAction("bad:action", "Bad", "v", false));

        var ok = AgentQuestionValidator.TryValidate(question, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("AllowedActions[1]");
        error.Should().Contain(":");
    }

    [Fact]
    public void TryValidate_RejectsDuplicateActionIds()
    {
        var question = BuildQuestion(
            new HumanAction("ok", "OK", "v1", false),
            new HumanAction("ok", "OK-2", "v2", false));

        var ok = AgentQuestionValidator.TryValidate(question, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("duplicate");
        error.Should().Contain("ok");
    }

    [Fact]
    public void TryValidate_Envelope_AcceptsValidDefaultActionId()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: BuildQuestion(
                new HumanAction("approve", "Approve", "approved", false),
                new HumanAction("reject", "Reject", "rejected", true)),
            ProposedDefaultActionId: "approve",
            RoutingMetadata: new Dictionary<string, string> { ["DiscordChannelId"] = "1" });

        var ok = AgentQuestionValidator.TryValidate(envelope, out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_Envelope_RejectsPhantomDefaultActionId()
    {
        var envelope = new AgentQuestionEnvelope(
            Question: BuildQuestion(
                new HumanAction("approve", "Approve", "approved", false)),
            ProposedDefaultActionId: "ghost",
            RoutingMetadata: new Dictionary<string, string> { ["DiscordChannelId"] = "1" });

        var ok = AgentQuestionValidator.TryValidate(envelope, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("ghost");
        error.Should().Contain("AllowedActions");
    }

    [Fact]
    public void EnsureValid_ThrowsForInvalidQuestion()
    {
        var question = BuildQuestion(
            new HumanAction("dup", "L", "v", false),
            new HumanAction("dup", "L2", "v2", false));

        var act = () => AgentQuestionValidator.EnsureValid(question);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*duplicate*");
    }
}
