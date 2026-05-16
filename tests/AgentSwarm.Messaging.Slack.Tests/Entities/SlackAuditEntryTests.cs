using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Entities;

/// <summary>
/// Stage 2.1 field-completeness tests for <see cref="SlackAuditEntry"/>
/// per architecture.md section 3.5. This class implements the explicit
/// brief scenario:
/// <c>Given SlackAuditEntry, When its public properties are reflected,
/// Then all fields from architecture.md section 3.5 are present (Id,
/// CorrelationId, AgentId, TaskId, ConversationId, Direction,
/// RequestType, TeamId, ChannelId, ThreadTs, MessageTs, UserId,
/// CommandText, ResponsePayload, Outcome, ErrorDetail, Timestamp).</c>
/// </summary>
public sealed class SlackAuditEntryTests
{
    [Fact]
    public void All_section_3_5_fields_are_present_with_canonical_types()
    {
        AssertProperty(nameof(SlackAuditEntry.Id), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.CorrelationId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.AgentId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.TaskId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.ConversationId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.Direction), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.RequestType), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.TeamId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.ChannelId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.ThreadTs), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.MessageTs), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.UserId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.CommandText), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.ResponsePayload), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.Outcome), typeof(string), nullable: false);
        AssertProperty(nameof(SlackAuditEntry.ErrorDetail), typeof(string), nullable: true);
        AssertProperty(nameof(SlackAuditEntry.Timestamp), typeof(DateTimeOffset), nullable: false);
    }

    [Fact]
    public void Public_property_set_matches_architecture_section_3_5_exactly()
    {
        // The brief test scenario: "all fields from architecture.md
        // section 3.5 are present". This fact enumerates every property
        // and verifies the set matches the contract -- catching both
        // missing fields AND accidental extras that would drift the
        // entity away from the architecture.
        string[] actual = typeof(SlackAuditEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = new[]
        {
            nameof(SlackAuditEntry.AgentId),
            nameof(SlackAuditEntry.ChannelId),
            nameof(SlackAuditEntry.CommandText),
            nameof(SlackAuditEntry.ConversationId),
            nameof(SlackAuditEntry.CorrelationId),
            nameof(SlackAuditEntry.Direction),
            nameof(SlackAuditEntry.ErrorDetail),
            nameof(SlackAuditEntry.Id),
            nameof(SlackAuditEntry.MessageTs),
            nameof(SlackAuditEntry.Outcome),
            nameof(SlackAuditEntry.RequestType),
            nameof(SlackAuditEntry.ResponsePayload),
            nameof(SlackAuditEntry.TaskId),
            nameof(SlackAuditEntry.TeamId),
            nameof(SlackAuditEntry.ThreadTs),
            nameof(SlackAuditEntry.Timestamp),
            nameof(SlackAuditEntry.UserId),
        }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        actual.Should().BeEquivalentTo(expected,
            because: "every field listed in architecture.md section 3.5 must be present and no extras are allowed");
        actual.Should().HaveCount(17,
            because: "architecture.md section 3.5 enumerates exactly 17 columns");
    }

    [Fact]
    public void Class_is_public_and_sealed()
    {
        Type t = typeof(SlackAuditEntry);
        t.IsPublic.Should().BeTrue();
        t.IsSealed.Should().BeTrue();
    }

    private static void AssertProperty(string name, Type expectedType, bool nullable)
    {
        PropertyInfo? prop = typeof(SlackAuditEntry).GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull(because: $"architecture.md section 3.5 requires {name}");

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
            actualNullable.Should().Be(nullable,
                because: $"{name} nullability must be {(nullable ? "string?" : "string")} per architecture.md section 3.5");
        }

        prop!.CanRead.Should().BeTrue();
        prop!.CanWrite.Should().BeTrue();
    }
}
