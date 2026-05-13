namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Stage 1.1 test scenario: "Required field validation — Given an AgentQuestion with null
/// QuestionId, When validated, Then a validation error is returned." Also exercises the
/// XOR target invariant, the allowed severity/status vocabulary, and the
/// <c>AllowedActions</c> non-empty rule.
/// </summary>
public sealed class AgentQuestionValidationTests
{
    private static AgentQuestion ValidQuestion(Action<AgentQuestion>? _ = null)
    {
        // The optional Action parameter exists purely to document intent at call sites where
        // tests mutate the returned record via `with`.
        return new AgentQuestion
        {
            QuestionId = "q-valid",
            AgentId = "agent-valid",
            TaskId = "task-valid",
            TenantId = "tenant-valid",
            TargetUserId = "user-valid",
            TargetChannelId = null,
            Title = "Valid title",
            Body = "Valid body",
            Severity = MessageSeverities.Info,
            AllowedActions = new[]
            {
                new HumanAction("a1", "Approve", "approve", false),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = "corr-valid",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = AgentQuestionStatuses.Open,
        };
    }

    [Fact]
    public void Validate_NullQuestionId_ProducesError()
    {
        var question = ValidQuestion() with { QuestionId = null! };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.QuestionId)));
    }

    [Fact]
    public void Validate_EmptyQuestionId_ProducesError()
    {
        var question = ValidQuestion() with { QuestionId = string.Empty };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.QuestionId)));
    }

    [Fact]
    public void Validate_WhitespaceQuestionId_ProducesError()
    {
        var question = ValidQuestion() with { QuestionId = "   " };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.QuestionId)));
    }

    [Fact]
    public void Validate_ValidQuestion_ReturnsNoErrors()
    {
        var question = ValidQuestion();

        var errors = question.Validate();

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(nameof(AgentQuestion.AgentId))]
    [InlineData(nameof(AgentQuestion.TaskId))]
    [InlineData(nameof(AgentQuestion.TenantId))]
    [InlineData(nameof(AgentQuestion.Title))]
    [InlineData(nameof(AgentQuestion.Body))]
    [InlineData(nameof(AgentQuestion.CorrelationId))]
    public void Validate_NullRequiredString_ProducesErrorForThatField(string fieldName)
    {
        var question = fieldName switch
        {
            nameof(AgentQuestion.AgentId) => ValidQuestion() with { AgentId = null! },
            nameof(AgentQuestion.TaskId) => ValidQuestion() with { TaskId = null! },
            nameof(AgentQuestion.TenantId) => ValidQuestion() with { TenantId = null! },
            nameof(AgentQuestion.Title) => ValidQuestion() with { Title = null! },
            nameof(AgentQuestion.Body) => ValidQuestion() with { Body = null! },
            nameof(AgentQuestion.CorrelationId) => ValidQuestion() with { CorrelationId = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName)),
        };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(fieldName));
    }

    [Fact]
    public void Validate_BothTargetsSet_ProducesXorError()
    {
        var question = ValidQuestion() with
        {
            TargetUserId = "u",
            TargetChannelId = "c",
        };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains("TargetUserId") && e.Contains("TargetChannelId"));
    }

    [Fact]
    public void Validate_NeitherTargetSet_ProducesXorError()
    {
        var question = ValidQuestion() with
        {
            TargetUserId = null,
            TargetChannelId = null,
        };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains("TargetUserId") && e.Contains("TargetChannelId"));
    }

    [Fact]
    public void Validate_ChannelOnlyTarget_IsValid()
    {
        var question = ValidQuestion() with
        {
            TargetUserId = null,
            TargetChannelId = "channel-xyz",
        };

        Assert.Empty(question.Validate());
    }

    [Fact]
    public void Validate_InvalidSeverity_ProducesError()
    {
        var question = ValidQuestion() with { Severity = "Bogus" };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.Severity)));
    }

    [Fact]
    public void Validate_InvalidStatus_ProducesError()
    {
        var question = ValidQuestion() with { Status = "Bogus" };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.Status)));
    }

    [Fact]
    public void Validate_EmptyAllowedActions_ProducesError()
    {
        var question = ValidQuestion() with { AllowedActions = Array.Empty<HumanAction>() };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.AllowedActions)));
    }

    [Fact]
    public void Validate_DefaultExpiresAt_ProducesError()
    {
        var question = ValidQuestion() with { ExpiresAt = default };

        var errors = question.Validate();

        Assert.Contains(errors, e => e.Contains(nameof(AgentQuestion.ExpiresAt)));
    }

    [Fact]
    public void Validate_AllCanonicalSeverityValues_AreAccepted()
    {
        foreach (var severity in MessageSeverities.All)
        {
            var question = ValidQuestion() with { Severity = severity };
            Assert.Empty(question.Validate());
        }
    }

    [Fact]
    public void Validate_AllCanonicalStatusValues_AreAccepted()
    {
        foreach (var status in AgentQuestionStatuses.All)
        {
            var question = ValidQuestion() with { Status = status };
            Assert.Empty(question.Validate());
        }
    }
}
