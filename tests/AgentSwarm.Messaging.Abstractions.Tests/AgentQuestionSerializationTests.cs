using System.Text.Json;

namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Stage 1.1 test scenario: "Serialize round-trip — Given an AgentQuestion with two
/// HumanAction items, When serialized to JSON and deserialized, Then all field values match
/// the original."
/// </summary>
public sealed class AgentQuestionSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
    };

    private static AgentQuestion BuildSampleQuestion()
    {
        return new AgentQuestion
        {
            QuestionId = "q-1234",
            AgentId = "release-agent-01",
            TaskId = "task-987",
            TenantId = "11111111-1111-1111-1111-111111111111",
            TargetUserId = "alice-internal",
            TargetChannelId = null,
            Title = "Approve hotfix deployment",
            Body = "Agent ready to deploy hotfix v1.2.3 to production. Approve or reject?",
            Severity = MessageSeverities.Warning,
            AllowedActions = new[]
            {
                new HumanAction("approve-action", "Approve", "approve", RequiresComment: false),
                new HumanAction("reject-action", "Reject", "reject", RequiresComment: true),
            },
            ExpiresAt = new DateTimeOffset(2026, 5, 13, 18, 30, 0, TimeSpan.Zero),
            ConversationId = null,
            CorrelationId = "corr-abc-def",
            CreatedAt = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
            Status = AgentQuestionStatuses.Open,
        };
    }

    [Fact]
    public void RoundTrip_PreservesAllScalarFields()
    {
        var original = BuildSampleQuestion();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<AgentQuestion>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original.QuestionId, restored!.QuestionId);
        Assert.Equal(original.AgentId, restored.AgentId);
        Assert.Equal(original.TaskId, restored.TaskId);
        Assert.Equal(original.TenantId, restored.TenantId);
        Assert.Equal(original.TargetUserId, restored.TargetUserId);
        Assert.Equal(original.TargetChannelId, restored.TargetChannelId);
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Body, restored.Body);
        Assert.Equal(original.Severity, restored.Severity);
        Assert.Equal(original.ExpiresAt, restored.ExpiresAt);
        Assert.Equal(original.ConversationId, restored.ConversationId);
        Assert.Equal(original.CorrelationId, restored.CorrelationId);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
        Assert.Equal(original.Status, restored.Status);
    }

    [Fact]
    public void RoundTrip_PreservesAllowedActionsCollection()
    {
        var original = BuildSampleQuestion();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<AgentQuestion>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.AllowedActions.Count);

        for (var i = 0; i < original.AllowedActions.Count; i++)
        {
            var originalAction = original.AllowedActions[i];
            var restoredAction = restored.AllowedActions[i];

            Assert.Equal(originalAction.ActionId, restoredAction.ActionId);
            Assert.Equal(originalAction.Label, restoredAction.Label);
            Assert.Equal(originalAction.Value, restoredAction.Value);
            Assert.Equal(originalAction.RequiresComment, restoredAction.RequiresComment);

            // Record structural equality also matches.
            Assert.Equal(originalAction, restoredAction);
        }
    }

    [Fact]
    public void RoundTrip_AllowedActionsAreDefensivelyCopiedAtInit()
    {
        var actions = new List<HumanAction>
        {
            new("a1", "Approve", "approve", RequiresComment: false),
            new("a2", "Reject", "reject", RequiresComment: true),
        };

        var question = new AgentQuestion
        {
            QuestionId = "q",
            AgentId = "a",
            TaskId = "t",
            TenantId = "tenant",
            TargetUserId = "u",
            Title = "title",
            Body = "body",
            Severity = MessageSeverities.Info,
            AllowedActions = actions,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = "corr",
        };

        // Mutating the source collection after init must not change the snapshot.
        actions.Add(new HumanAction("a3", "Mutated", "mutated", false));
        actions.Clear();

        Assert.Equal(2, question.AllowedActions.Count);
        Assert.Equal("a1", question.AllowedActions[0].ActionId);
        Assert.Equal("a2", question.AllowedActions[1].ActionId);
    }

    [Fact]
    public void RoundTrip_NullAllowedActionsBecomesEmptySnapshot()
    {
        var question = new AgentQuestion
        {
            QuestionId = "q",
            AgentId = "a",
            TaskId = "t",
            TenantId = "tenant",
            TargetUserId = "u",
            Title = "title",
            Body = "body",
            Severity = MessageSeverities.Info,
            AllowedActions = null!,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = "corr",
        };

        Assert.NotNull(question.AllowedActions);
        Assert.Empty(question.AllowedActions);
    }
}
