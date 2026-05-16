using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
        // The unique constraint on (TeamId, ChannelId, ThreadTs) is
        // declared by SlackThreadMappingConfiguration (Stage 2.2). Build
        // the test DbContext model and verify the configuration produces
        // the required unique index. This test stays in the Stage 2.1
        // entity test file because it pins the same SEMANTIC contract
        // the entity carries -- only the declaration mechanism shifted
        // from an [Index] attribute to the fluent configuration.
        DbContextOptions<SlackTestDbContext> options =
            new DbContextOptionsBuilder<SlackTestDbContext>()
                .UseSqlite("Filename=:memory:")
                .Options;
        using SlackTestDbContext context = new(options);

        IEntityType entity = context.Model.FindEntityType(typeof(SlackThreadMapping))
            ?? throw new InvalidOperationException("SlackThreadMapping not registered.");

        IIndex[] uniqueIndexes = entity.GetIndexes().Where(i => i.IsUnique).ToArray();

        uniqueIndexes.Should().NotBeEmpty(
            because: "Stage 2.1 step 2 requires a unique constraint on (TeamId, ChannelId, ThreadTs)");

        IIndex uniqueIndex = uniqueIndexes.Should().ContainSingle().Subject;

        uniqueIndex.Properties.Select(p => p.Name).Should().Equal(
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
