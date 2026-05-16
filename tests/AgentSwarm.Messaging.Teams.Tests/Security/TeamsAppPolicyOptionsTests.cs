using System.Collections.ObjectModel;
using AgentSwarm.Messaging.Teams.Security;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class TeamsAppPolicyOptionsTests
{
    [Fact]
    public void Defaults_AreProductionConservative()
    {
        var options = new TeamsAppPolicyOptions();

        Assert.True(options.RequireAdminConsent);
        Assert.True(options.BlockSideloading);
        Assert.Single(options.AllowedAppCatalogScopes);
        Assert.Contains(TeamsAppPolicyOptions.OrganizationScope, options.AllowedAppCatalogScopes);
    }

    [Theory]
    [InlineData(TeamsAppPolicyOptions.OrganizationScope, true)]
    [InlineData(TeamsAppPolicyOptions.PersonalScope, false)]
    [InlineData("ORGANIZATION", true)]
    [InlineData("Personal", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void IsScopeAllowed_HonoursAllowedScopesCaseInsensitive(string scope, bool expected)
    {
        var options = new TeamsAppPolicyOptions();
        Assert.Equal(expected, options.IsScopeAllowed(scope));
    }

    [Fact]
    public void Validate_DefaultOptions_AreValid()
    {
        var options = new TeamsAppPolicyOptions();
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_EmptyAllowedScopes_ReturnsError()
    {
        var options = new TeamsAppPolicyOptions
        {
            AllowedAppCatalogScopes = new List<string>(),
        };

        var errors = options.Validate();

        Assert.Single(errors);
        Assert.Contains("AllowedAppCatalogScopes", errors[0]);
    }

    [Fact]
    public void Validate_UnknownScope_ReturnsError()
    {
        var options = new TeamsAppPolicyOptions
        {
            AllowedAppCatalogScopes = new List<string> { "organization", "shadow-catalog" },
        };

        var errors = options.Validate();

        Assert.Single(errors);
        Assert.Contains("shadow-catalog", errors[0]);
    }

    [Fact]
    public void Validate_NullAllowedScopes_ReturnsError()
    {
        var options = new TeamsAppPolicyOptions
        {
            AllowedAppCatalogScopes = null!,
        };

        var errors = options.Validate();

        Assert.Single(errors);
    }

    [Fact]
    public void SupportedScopes_ContainsOrganizationAndPersonal()
    {
        Assert.Contains(TeamsAppPolicyOptions.OrganizationScope, TeamsAppPolicyOptions.SupportedScopes);
        Assert.Contains(TeamsAppPolicyOptions.PersonalScope, TeamsAppPolicyOptions.SupportedScopes);
        Assert.Equal(2, TeamsAppPolicyOptions.SupportedScopes.Count);
    }
}
