using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Contracts;

public class SendResultTests
{
    [Fact]
    public void Succeeded_PopulatesPlatformIdAndClearsError()
    {
        var result = SendResult.Succeeded(987654321L);

        result.Success.Should().BeTrue();
        result.PlatformMessageId.Should().Be(987654321L);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failed_PopulatesError_AndClearsPlatformId()
    {
        var result = SendResult.Failed("rate limited");

        result.Success.Should().BeFalse();
        result.PlatformMessageId.Should().BeNull();
        result.ErrorMessage.Should().Be("rate limited");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Failed_NullOrWhitespaceError_Throws(string? error)
    {
        var act = () => SendResult.Failed(error!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Construction_AllowsArbitraryShapes()
    {
        // Direct constructor available so callers can model platform-side
        // partial outcomes (e.g. accepted-but-unverified) without being forced
        // through the convenience factories.
        var result = new SendResult(Success: true, PlatformMessageId: null, ErrorMessage: null);

        result.Success.Should().BeTrue();
        result.PlatformMessageId.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }
}

public class AuthorizationResultTests
{
    private static GuildBinding BuildBinding() =>
        new(
            Id: Guid.NewGuid(),
            GuildId: 1UL,
            ChannelId: 2UL,
            ChannelPurpose: ChannelPurpose.Control,
            TenantId: "tenant",
            WorkspaceId: "workspace",
            AllowedRoleIds: new ulong[] { 100UL, 200UL },
            CommandRestrictions: null,
            RegisteredAt: DateTimeOffset.UnixEpoch,
            IsActive: true);

    [Fact]
    public void Allow_PopulatesBinding_AndClearsDenialReason()
    {
        var binding = BuildBinding();

        var result = AuthorizationResult.Allow(binding);

        result.IsAllowed.Should().BeTrue();
        result.DenialReason.Should().BeNull();
        result.ResolvedBinding.Should().BeSameAs(binding);
    }

    [Fact]
    public void Allow_NullBinding_Throws()
    {
        var act = () => AuthorizationResult.Allow(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Deny_PopulatesReason_AndDefaultsBindingToNull()
    {
        var result = AuthorizationResult.Deny("user not in guild");

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Be("user not in guild");
        result.ResolvedBinding.Should().BeNull();
    }

    [Fact]
    public void Deny_WithBinding_RetainsBindingForLogging()
    {
        var binding = BuildBinding();

        var result = AuthorizationResult.Deny("missing role", binding);

        result.IsAllowed.Should().BeFalse();
        result.ResolvedBinding.Should().BeSameAs(binding);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deny_NullOrWhitespaceReason_Throws(string? reason)
    {
        var act = () => AuthorizationResult.Deny(reason!);

        act.Should().Throw<ArgumentException>();
    }
}
