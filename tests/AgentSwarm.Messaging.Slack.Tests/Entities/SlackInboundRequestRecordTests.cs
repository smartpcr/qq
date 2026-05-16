using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Entities;

/// <summary>
/// Stage 2.1 field-completeness tests for
/// <see cref="SlackInboundRequestRecord"/> per architecture.md section 3.3.
/// </summary>
public sealed class SlackInboundRequestRecordTests
{
    [Fact]
    public void All_section_3_3_fields_are_present_with_canonical_types()
    {
        AssertProperty(nameof(SlackInboundRequestRecord.IdempotencyKey), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.SourceType), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.TeamId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.ChannelId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackInboundRequestRecord.UserId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.RawPayloadHash), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.ProcessingStatus), typeof(string), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.FirstSeenAt), typeof(DateTimeOffset), nullable: false);
        AssertProperty(nameof(SlackInboundRequestRecord.CompletedAt), typeof(DateTimeOffset), nullable: true);
    }

    [Fact]
    public void Public_property_set_matches_architecture_section_3_3_exactly()
    {
        string[] actual = typeof(SlackInboundRequestRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = new[]
        {
            nameof(SlackInboundRequestRecord.ChannelId),
            nameof(SlackInboundRequestRecord.CompletedAt),
            nameof(SlackInboundRequestRecord.FirstSeenAt),
            nameof(SlackInboundRequestRecord.IdempotencyKey),
            nameof(SlackInboundRequestRecord.ProcessingStatus),
            nameof(SlackInboundRequestRecord.RawPayloadHash),
            nameof(SlackInboundRequestRecord.SourceType),
            nameof(SlackInboundRequestRecord.TeamId),
            nameof(SlackInboundRequestRecord.UserId),
        }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Class_is_public_and_sealed()
    {
        Type t = typeof(SlackInboundRequestRecord);
        t.IsPublic.Should().BeTrue();
        t.IsSealed.Should().BeTrue();
    }

    private static void AssertProperty(string name, Type expectedType, bool nullable)
    {
        PropertyInfo? prop = typeof(SlackInboundRequestRecord).GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull(because: $"architecture.md section 3.3 requires {name}");

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
