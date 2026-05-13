using System.Reflection;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Abstractions.Tests;

public class AgentQuestionTests
{
    private static AgentQuestion CreateValid() => new()
    {
        QuestionId = "q-1",
        AgentId = "agent-build",
        TaskId = "task-42",
        TenantId = "11111111-1111-1111-1111-111111111111",
        TargetUserId = "user-123",
        TargetChannelId = null,
        Title = "Approve deployment?",
        Body = "Build #42 is ready to ship to prod. Proceed?",
        Severity = MessageSeverities.Warning,
        AllowedActions = new[]
        {
            new HumanAction { ActionId = "a1", Label = "Approve", Value = "approve", RequiresComment = false },
            new HumanAction { ActionId = "a2", Label = "Reject",  Value = "reject",  RequiresComment = true  }
        },
        ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ConversationId = "conv-xyz",
        CorrelationId = "corr-1",
        CreatedAt = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero),
        Status = AgentQuestionStatuses.Open
    };

    [Fact]
    public void Roundtrip_Serialize_Then_Deserialize_Preserves_All_Fields()
    {
        var original = CreateValid();

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<AgentQuestion>(json);

        Assert.NotNull(roundTripped);

        // Compare every scalar field. We avoid record-equality here because the
        // IReadOnlyList<HumanAction> property uses reference equality by default,
        // and the deserialized backing collection (List<T>) is not reference-equal
        // to the original (T[]) even when contents match.
        Assert.Equal(original.QuestionId, roundTripped!.QuestionId);
        Assert.Equal(original.AgentId, roundTripped.AgentId);
        Assert.Equal(original.TaskId, roundTripped.TaskId);
        Assert.Equal(original.TenantId, roundTripped.TenantId);
        Assert.Equal(original.TargetUserId, roundTripped.TargetUserId);
        Assert.Equal(original.TargetChannelId, roundTripped.TargetChannelId);
        Assert.Equal(original.Title, roundTripped.Title);
        Assert.Equal(original.Body, roundTripped.Body);
        Assert.Equal(original.Severity, roundTripped.Severity);
        Assert.Equal(original.ExpiresAt, roundTripped.ExpiresAt);
        Assert.Equal(original.ConversationId, roundTripped.ConversationId);
        Assert.Equal(original.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(original.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(original.Status, roundTripped.Status);

        // Two HumanAction items are preserved with all sub-fields.
        Assert.Equal(original.AllowedActions.Count, roundTripped.AllowedActions.Count);
        for (var i = 0; i < original.AllowedActions.Count; i++)
        {
            Assert.Equal(original.AllowedActions[i], roundTripped.AllowedActions[i]);
        }
    }

    [Fact]
    public void Validate_Returns_Error_When_QuestionId_Is_Null()
    {
        var bad = CreateValid() with { QuestionId = null! };

        var results = bad.ValidateAll();

        Assert.NotEmpty(results);
        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.QuestionId)));
    }

    [Fact]
    public void Validate_Returns_Error_When_QuestionId_Is_Empty()
    {
        var bad = CreateValid() with { QuestionId = string.Empty };

        var results = bad.ValidateAll();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.QuestionId)));
    }

    [Fact]
    public void Validate_Passes_For_Well_Formed_Question()
    {
        var ok = CreateValid();

        var results = ok.ValidateAll();

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_Rejects_When_Both_TargetUserId_And_TargetChannelId_Set()
    {
        var bad = CreateValid() with { TargetUserId = "u1", TargetChannelId = "c1" };

        var results = bad.ValidateAll();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.TargetUserId))
            || r.MemberNames.Contains(nameof(AgentQuestion.TargetChannelId)));
    }

    [Fact]
    public void Validate_Rejects_When_Both_TargetUserId_And_TargetChannelId_Null()
    {
        var bad = CreateValid() with { TargetUserId = null, TargetChannelId = null };

        var results = bad.ValidateAll();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.TargetUserId))
            || r.MemberNames.Contains(nameof(AgentQuestion.TargetChannelId)));
    }

    [Fact]
    public void Validate_Accepts_Channel_Scope_With_Null_User()
    {
        var ok = CreateValid() with { TargetUserId = null, TargetChannelId = "channel-abc" };

        var results = ok.ValidateAll();

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_Rejects_Unknown_Severity()
    {
        var bad = CreateValid() with { Severity = "Catastrophic" };

        var results = bad.ValidateAll();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.Severity)));
    }

    [Fact]
    public void Validate_Rejects_Unknown_Status()
    {
        var bad = CreateValid() with { Status = "PendingHumanReview" };

        var results = bad.ValidateAll();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(AgentQuestion.Status)));
    }

    [Fact]
    public void Default_Status_Is_Open()
    {
        var q = new AgentQuestion();

        Assert.Equal(AgentQuestionStatuses.Open, q.Status);
    }

    [Fact]
    public void All_Properties_Are_InitOnly()
    {
        // Sanity-check that every settable property uses an init-only setter.
        // Provides the same compile-time immutability guarantee as the HumanDecisionEvent
        // immutability test, but for the full AgentQuestion surface.
        var props = typeof(AgentQuestion).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            if (prop.SetMethod is null)
            {
                continue;
            }

            var modifiers = prop.SetMethod.ReturnParameter.GetRequiredCustomModifiers();
            Assert.Contains(modifiers, m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }
    }
}
