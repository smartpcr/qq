using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Models;

public class GuildBindingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void Roundtrip_PreservesAllFields_IncludingCommandRestrictions()
    {
        var binding = new GuildBinding(
            Id: Guid.Parse("0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d"),
            GuildId: 111222333444555666UL,
            ChannelId: 999888777666555444UL,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "tenant-1",
            WorkspaceId: "workspace-A",
            AllowedRoleIds: new ulong[] { 100UL, 200UL, 300UL },
            CommandRestrictions: new Dictionary<string, IReadOnlyList<ulong>>
            {
                ["approve"] = new ulong[] { 200UL },
                ["reject"] = new ulong[] { 200UL, 300UL },
            },
            RegisteredAt: new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
            IsActive: true);

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<GuildBinding>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.Id.Should().Be(binding.Id);
        roundTripped.GuildId.Should().Be(binding.GuildId);
        roundTripped.ChannelId.Should().Be(binding.ChannelId);
        roundTripped.ChannelPurpose.Should().Be(ChannelPurpose.Control);
        roundTripped.TenantId.Should().Be(binding.TenantId);
        roundTripped.WorkspaceId.Should().Be(binding.WorkspaceId);
        roundTripped.AllowedRoleIds.Should().Equal(100UL, 200UL, 300UL);
        roundTripped.CommandRestrictions.Should().NotBeNull();
        roundTripped.CommandRestrictions!["approve"].Should().Equal(200UL);
        roundTripped.CommandRestrictions["reject"].Should().Equal(200UL, 300UL);
        roundTripped.RegisteredAt.Should().Be(binding.RegisteredAt);
        roundTripped.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Roundtrip_NullCommandRestrictions_IsPreserved()
    {
        var binding = new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 1UL,
            ChannelId: 2UL,
            ChannelPurpose: ChannelPurpose.Alert,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: new ulong[] { 10UL },
            CommandRestrictions: null,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: false);

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<GuildBinding>(json, JsonOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.CommandRestrictions.Should().BeNull();
        roundTripped.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ChannelPurpose_IsSerializedAsStringName()
    {
        var binding = new GuildBinding(
            Id: Guid.Empty,
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Workstream,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: Array.Empty<ulong>(),
            CommandRestrictions: null,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        var json = JsonSerializer.Serialize(binding, JsonOptions);

        json.Should().Contain("\"ChannelPurpose\":\"Workstream\"");
        json.Should().NotContain("\"ChannelPurpose\":2");
    }

    [Fact]
    public void AllowedRoleIds_IsDefensivelyCopied_FromMutableSource()
    {
        var roles = new List<ulong> { 1UL, 2UL };

        var binding = new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: roles,
            CommandRestrictions: null,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        roles.Add(999UL);
        roles[0] = 0UL;

        binding.AllowedRoleIds.Should().Equal(1UL, 2UL);
    }

    [Fact]
    public void CommandRestrictions_IsDefensivelyCopied_BothDictAndValueLists()
    {
        var inner = new List<ulong> { 10UL, 20UL };
        var outer = new Dictionary<string, IReadOnlyList<ulong>>
        {
            ["approve"] = inner,
        };

        var binding = new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: Array.Empty<ulong>(),
            CommandRestrictions: outer,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        outer["reject"] = new ulong[] { 999UL };
        inner.Add(30UL);
        inner[0] = 0UL;

        binding.CommandRestrictions.Should().NotBeNull();
        binding.CommandRestrictions!.Should().HaveCount(1);
        binding.CommandRestrictions!["approve"].Should().Equal(10UL, 20UL);
        binding.CommandRestrictions!.Should().NotContainKey("reject");
    }

    [Fact]
    public void Constructor_ThrowsOnNullAllowedRoleIds()
    {
        var act = () => new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: null!,
            CommandRestrictions: null,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullCommandRestrictionsValue()
    {
        // The previous implementation silently coerced a null value to
        // Array.Empty<ulong>(), which hid malformed config / JSON. The new
        // contract requires producers to use an explicit empty collection
        // when they mean "no roles".
        var bad = new Dictionary<string, IReadOnlyList<ulong>>
        {
            ["approve"] = null!,
        };

        var act = () => new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: Array.Empty<ulong>(),
            CommandRestrictions: bad,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*CommandRestrictions*approve*null*");
    }

    [Fact]
    public void Deserialize_NullCommandRestrictionsValue_FailsLoudly()
    {
        // Companion to Constructor_ThrowsOnNullCommandRestrictionsValue: the
        // evaluator's concern was specifically that the previous silent
        // coercion hid malformed JSON. This test exercises the JSON boundary
        // by feeding payloads that contain "approve": null and confirming the
        // deserializer surfaces a failure (System.Text.Json may wrap the
        // constructor's ArgumentException in a JsonException, so we accept
        // either).
        const string json =
            "{\"Id\":\"00000000-0000-0000-0000-000000000001\"," +
            "\"GuildId\":1,\"ChannelId\":2," +
            "\"ChannelPurpose\":\"Control\"," +
            "\"TenantId\":\"t\",\"WorkspaceId\":\"w\"," +
            "\"AllowedRoleIds\":[]," +
            "\"CommandRestrictions\":{\"approve\":null}," +
            "\"RegisteredAt\":\"1970-01-01T00:00:00+00:00\"," +
            "\"IsActive\":true}";

        var act = () => JsonSerializer.Deserialize<GuildBinding>(json, JsonOptions);

        var thrown = act.Should().Throw<Exception>().Which;
        (thrown is ArgumentException || thrown is JsonException)
            .Should().BeTrue(
                "the model must surface malformed null restriction values rather than silently accept them; got {0}: {1}",
                thrown.GetType().FullName,
                thrown.Message);
    }

    [Fact]
    public void Constructor_EmptyCommandRestrictionsValue_IsAllowed()
    {
        // The null check above must NOT reject the explicit empty collection
        // that callers should use to mean "this subcommand has no allowed roles".
        var explicitEmpty = new Dictionary<string, IReadOnlyList<ulong>>
        {
            ["approve"] = Array.Empty<ulong>(),
        };

        var binding = new GuildBinding(
            Id: Guid.NewGuid(),
            GuildId: 0,
            ChannelId: 0,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "t",
            WorkspaceId: "w",
            AllowedRoleIds: Array.Empty<ulong>(),
            CommandRestrictions: explicitEmpty,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

        binding.CommandRestrictions.Should().NotBeNull();
        binding.CommandRestrictions!["approve"].Should().BeEmpty();
    }
}
