using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class AgentQuestionTests
{
    [Fact]
    public void Question_ContainsAllRequiredFields()
    {
        var buttons = new[]
        {
            new QuestionButton("btn-1", "Approve", "approved"),
            new QuestionButton("btn-2", "Reject", "rejected")
        };

        var question = new AgentQuestion(
            QuestionId: "q-1",
            AgentId: "agent-build-42",
            Context: "Build #99 completed with 2 warnings. Deploy to staging?",
            Severity: MessageSeverity.High,
            Timeout: TimeSpan.FromMinutes(15),
            ProposedDefaultAction: "approved",
            Buttons: buttons,
            Correlation: new CorrelationContext { TraceId = "trace-abc" });

        Assert.Equal("q-1", question.QuestionId);
        Assert.Equal("agent-build-42", question.AgentId);
        Assert.Equal(MessageSeverity.High, question.Severity);
        Assert.Equal(TimeSpan.FromMinutes(15), question.Timeout);
        Assert.Equal("approved", question.ProposedDefaultAction);
        Assert.Equal(2, question.Buttons.Count);
        Assert.Equal("trace-abc", question.Correlation.TraceId);
    }

    [Fact]
    public void HumanResponse_LinksBackToQuestion()
    {
        var op = new OperatorIdentity("op-1", "t-1", "ws-1", "Bob");
        var corr = new CorrelationContext();
        var response = new HumanResponse(
            ResponseId: "r-1",
            QuestionId: "q-1",
            Operator: op,
            SelectedValue: "approved",
            RawText: null,
            RespondedAtUtc: DateTimeOffset.UtcNow,
            Correlation: corr);

        Assert.Equal("q-1", response.QuestionId);
        Assert.Equal("approved", response.SelectedValue);
        Assert.Equal("op-1", response.Operator.OperatorId);
    }
}
