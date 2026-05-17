using AgentSwarm.Messaging.Teams.Security;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class RbacOptionsTests
{
    [Fact]
    public void WithDefaultRoleMatrix_PopulatesCanonicalThreeRoles()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();

        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.OperatorRole));
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.ApproverRole));
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.ViewerRole));
    }

    [Fact]
    public void OperatorRole_IsPermittedToExecuteEveryCanonicalCommand()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();

        foreach (var command in new[] { "agent ask", "agent status", "approve", "reject", "escalate", "pause", "resume" })
        {
            Assert.True(
                options.IsCommandPermitted(RbacOptions.OperatorRole, command),
                $"Operator role should permit '{command}'.");
        }
    }

    [Theory]
    [InlineData("approve", true)]
    [InlineData("reject", true)]
    [InlineData("agent status", true)]
    [InlineData("agent ask", false)]
    [InlineData("escalate", false)]
    [InlineData("pause", false)]
    [InlineData("resume", false)]
    public void ApproverRole_PermitsOnlyDecisionAndStatusCommands(string command, bool expected)
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Equal(expected, options.IsCommandPermitted(RbacOptions.ApproverRole, command));
    }

    [Theory]
    [InlineData("agent status", true)]
    [InlineData("approve", false)]
    [InlineData("reject", false)]
    [InlineData("agent ask", false)]
    [InlineData("escalate", false)]
    [InlineData("pause", false)]
    [InlineData("resume", false)]
    public void ViewerRole_PermitsOnlyAgentStatus(string command, bool expected)
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Equal(expected, options.IsCommandPermitted(RbacOptions.ViewerRole, command));
    }

    [Fact]
    public void IsCommandPermitted_NullOrEmptyRoleOrCommand_ReturnsFalse()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.False(options.IsCommandPermitted(null, "approve"));
        Assert.False(options.IsCommandPermitted("", "approve"));
        Assert.False(options.IsCommandPermitted("Operator", ""));
    }

    [Fact]
    public void IsCommandPermitted_RoleNameIsCaseInsensitive()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.True(options.IsCommandPermitted("operator", "approve"));
        Assert.True(options.IsCommandPermitted("OPERATOR", "approve"));
    }

    [Fact]
    public void WithDefaultRoleMatrix_PreservesExistingCustomRoles()
    {
        var options = new RbacOptions();
        options.RoleCommands["Auditor"] = new[] { "agent status" };

        options.WithDefaultRoleMatrix();

        Assert.True(options.RoleCommands.ContainsKey("Auditor"));
        Assert.Contains("agent status", options.RoleCommands["Auditor"]);
        Assert.True(options.RoleCommands.ContainsKey(RbacOptions.OperatorRole));
    }

    [Fact]
    public void AssignRole_RegistersUserUnderTenant()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole("tenant-1", "aad-obj-alice", RbacOptions.OperatorRole);

        Assert.Equal(RbacOptions.OperatorRole, options.ResolveRoleOrDefault("tenant-1", "aad-obj-alice"));
    }

    [Fact]
    public void AssignRole_IsIdempotent_ReplacesPriorAssignment()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole("tenant-1", "aad-obj-alice", RbacOptions.ViewerRole);
        options.AssignRole("tenant-1", "aad-obj-alice", RbacOptions.ApproverRole);

        Assert.Equal(RbacOptions.ApproverRole, options.ResolveRoleOrDefault("tenant-1", "aad-obj-alice"));
    }

    [Fact]
    public void AssignRole_RejectsNullOrEmptyArguments()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Throws<ArgumentException>(() => options.AssignRole("", "u", "r"));
        Assert.Throws<ArgumentException>(() => options.AssignRole("t", "", "r"));
        Assert.Throws<ArgumentException>(() => options.AssignRole("t", "u", ""));
    }

    [Fact]
    public void ResolveRoleOrDefault_ReturnsDefaultRoleWhenUserNotAssigned()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.DefaultRole = RbacOptions.ViewerRole;

        Assert.Equal(RbacOptions.ViewerRole, options.ResolveRoleOrDefault("tenant-1", "unmapped-aad"));
    }

    [Fact]
    public void ResolveRoleOrDefault_ReturnsNullWhenNoAssignmentAndNoDefault()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Null(options.ResolveRoleOrDefault("tenant-1", "unmapped-aad"));
    }

    [Fact]
    public void FindRequiredRole_PrefersLeastPrivilegedCanonicalRole()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();

        Assert.Equal(RbacOptions.ViewerRole, options.FindRequiredRole("agent status"));
        Assert.Equal(RbacOptions.ApproverRole, options.FindRequiredRole("approve"));
        Assert.Equal(RbacOptions.OperatorRole, options.FindRequiredRole("agent ask"));
    }

    [Fact]
    public void FindRequiredRole_UnknownCommand_ReturnsNull()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Null(options.FindRequiredRole("teleport"));
    }

    [Fact]
    public void FindRequiredRole_EmptyCommand_ReturnsNull()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        Assert.Null(options.FindRequiredRole(""));
    }

    [Fact]
    public void TenantRoleAssignments_AreKeyedCaseInsensitive()
    {
        var options = new RbacOptions().WithDefaultRoleMatrix();
        options.AssignRole("Tenant-A", "aad-obj-bob", RbacOptions.OperatorRole);

        Assert.Equal(RbacOptions.OperatorRole, options.ResolveRoleOrDefault("tenant-a", "aad-obj-bob"));
    }
}
