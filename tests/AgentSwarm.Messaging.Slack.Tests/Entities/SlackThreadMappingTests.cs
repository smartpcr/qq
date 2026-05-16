using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Entities;

/// <summary>
/// Stage 2.1 field-completeness tests for <see cref="SlackThreadMapping"/>
/// per architecture.md section 3.2.
/// </summary>
public sealed class SlackThreadMappingTests
{
    [Fact]
    public void All_section_3_2_fields_are_present_with_canonical_types()
    {
        AssertProperty(nameof(SlackThreadMapping.TaskId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.TeamId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.ChannelId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.ThreadTs), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.CorrelationId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.AgentId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.CreatedAt), typeof(DateTimeOffset), nullable: false);
        AssertProperty(nameof(SlackThreadMapping.LastMessageAt), typeof(DateTimeOffset), nullable: false);
    }

    [Fact]
    public void Public_property_set_matches_architecture_section_3_2_exactly()
    {
        string[] actual = typeof(SlackThreadMapping)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = new[]
        {
            nameof(SlackThreadMapping.AgentId),
            nameof(SlackThreadMapping.ChannelId),
            nameof(SlackThreadMapping.CorrelationId),
            nameof(SlackThreadMapping.CreatedAt),
            nameof(SlackThreadMapping.LastMessageAt),
            nameof(SlackThreadMapping.TaskId),
            nameof(SlackThreadMapping.TeamId),
            nameof(SlackThreadMapping.ThreadTs),
        }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Class_is_public_and_sealed()
    {
        Type t = typeof(SlackThreadMapping);
        t.IsPublic.Should().BeTrue();
        t.IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Has_unique_index_on_TeamId_ChannelId_ThreadTs_per_architecture_section_3_2()
    {
        // Implements Stage 2.1 requirement: "add unique constraint on
        // (TeamId, ChannelId, ThreadTs)". The constraint is declared at
        // the entity level via the EF Core IndexAttribute so it is part
        // of the entity contract and is picked up by both the
        // production MessagingDbContext (Stage 2.2) and the in-memory
        // schema tests (Stage 2.3) without requiring an
        // IEntityTypeConfiguration to be present.
        IndexAttribute[] indexes = typeof(SlackThreadMapping)
            .GetCustomAttributes<IndexAttribute>(inherit: false)
            .ToArray();

        indexes.Should().NotBeEmpty(
            because: "Stage 2.1 step 2 requires a unique constraint on (TeamId, ChannelId, ThreadTs)");

        IndexAttribute uniqueIndex = indexes.Should().ContainSingle(idx => idx.IsUnique).Subject;

        uniqueIndex.PropertyNames.Should().Equal(
            new[]
            {
                nameof(SlackThreadMapping.TeamId),
                nameof(SlackThreadMapping.ChannelId),
                nameof(SlackThreadMapping.ThreadTs),
            },
            because: "architecture.md section 3.2 specifies the column order (team_id, channel_id, thread_ts)");

        uniqueIndex.IsUnique.Should().BeTrue();
    }

    private static void AssertProperty(string name, Type expectedType, bool nullable)
    {
        PropertyInfo? prop = typeof(SlackThreadMapping).GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull(because: $"architecture.md section 3.2 requires {name}");

        Type actualType = prop!.PropertyType;
        if (expectedType.IsValueType)
        {
            Type expected = nullable
                ? typeof(Nullable<>).MakeGenericType(expectedType)
                : expectedType;
            actualType.Should().Be(expected);
        }
        else
        {
            actualType.Should().Be(expectedType);
            NullabilityInfo info = new NullabilityInfoContext().Create(prop);
            bool actualNullable = info.ReadState == NullabilityState.Nullable;
            actualNullable.Should().Be(nullable);
        }

        prop!.CanWrite.Should().BeTrue();
    }
}
