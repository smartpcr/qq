using System.Linq;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Entities;
using FluentAssertions;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Entities;

/// <summary>
/// Stage 2.1 field-completeness tests for
/// <see cref="SlackWorkspaceConfig"/>. Pin the public property surface to
/// the canonical contract spelled out in architecture.md section 3.1 so
/// later stages (EF configuration, persistence migrations, seed data)
/// cannot silently drop, rename, or retype a field.
/// </summary>
public sealed class SlackWorkspaceConfigTests
{
    [Fact]
    public void All_section_3_1_fields_are_present_with_canonical_types()
    {
        AssertProperty(nameof(SlackWorkspaceConfig.TeamId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.WorkspaceName), typeof(string), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.BotTokenSecretRef), typeof(string), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.SigningSecretRef), typeof(string), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.AppLevelTokenRef), typeof(string), nullable: true);
        AssertProperty(nameof(SlackWorkspaceConfig.DefaultChannelId), typeof(string), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.FallbackChannelId), typeof(string), nullable: true);
        AssertProperty(nameof(SlackWorkspaceConfig.AllowedChannelIds), typeof(string[]), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.AllowedUserGroupIds), typeof(string[]), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.Enabled), typeof(bool), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.CreatedAt), typeof(DateTimeOffset), nullable: false);
        AssertProperty(nameof(SlackWorkspaceConfig.UpdatedAt), typeof(DateTimeOffset), nullable: false);
    }

    [Fact]
    public void Public_property_set_matches_architecture_section_3_1_exactly()
    {
        // Catches accidental property additions that drift the entity
        // away from the architecture contract.
        string[] actual = typeof(SlackWorkspaceConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] expected = new[]
        {
            nameof(SlackWorkspaceConfig.AllowedChannelIds),
            nameof(SlackWorkspaceConfig.AllowedUserGroupIds),
            nameof(SlackWorkspaceConfig.AppLevelTokenRef),
            nameof(SlackWorkspaceConfig.BotTokenSecretRef),
            nameof(SlackWorkspaceConfig.CreatedAt),
            nameof(SlackWorkspaceConfig.DefaultChannelId),
            nameof(SlackWorkspaceConfig.Enabled),
            nameof(SlackWorkspaceConfig.FallbackChannelId),
            nameof(SlackWorkspaceConfig.SigningSecretRef),
            nameof(SlackWorkspaceConfig.TeamId),
            nameof(SlackWorkspaceConfig.UpdatedAt),
            nameof(SlackWorkspaceConfig.WorkspaceName),
        }.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Array_properties_default_to_empty_so_inserts_never_carry_null()
    {
        SlackWorkspaceConfig config = new();

        config.AllowedChannelIds.Should().NotBeNull().And.BeEmpty();
        config.AllowedUserGroupIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Class_is_public_and_sealed_for_persistence_cross_assembly_use()
    {
        Type t = typeof(SlackWorkspaceConfig);
        t.IsPublic.Should().BeTrue("EF Core configuration in the Persistence project must see the type");
        t.IsSealed.Should().BeTrue("inheritance is not part of the data-model contract");
    }

    private static void AssertProperty(string name, Type expectedType, bool nullable)
    {
        PropertyInfo? prop = typeof(SlackWorkspaceConfig).GetProperty(
            name, BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull(because: $"architecture.md section 3.1 requires {name}");

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
                because: $"{name} nullability must be {(nullable ? "nullable" : "non-nullable")} per architecture.md section 3.1");
        }

        prop!.CanRead.Should().BeTrue();
        prop!.CanWrite.Should().BeTrue(because: "EF Core hydrates entities through public setters");
    }
}
