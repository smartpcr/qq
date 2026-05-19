using AgentSwarm.Messaging.Teams.Diagnostics;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="TeamsLogContext"/> — the AsyncLocal-backed ambient
/// store that feeds the §6.3 Serilog <see cref="TeamsLogEnricher"/>. Verifies the
/// push/pop nesting contract, inheritance from parent on null inputs, and async-
/// flow propagation across <c>await</c> boundaries.
/// </summary>
public sealed class TeamsLogContextTests
{
    [Fact]
    public void Snapshot_OutsideScope_ReturnsNulls()
    {
        var (corr, tenant, user) = TeamsLogContext.Snapshot();

        Assert.Null(corr);
        Assert.Null(tenant);
        Assert.Null(user);
    }

    [Fact]
    public void Push_StoresAllThreeKeys_AndPopRestoresPrevious()
    {
        using (TeamsLogContext.Push("corr-1", "tenant-1", "user-1"))
        {
            var (corr, tenant, user) = TeamsLogContext.Snapshot();
            Assert.Equal("corr-1", corr);
            Assert.Equal("tenant-1", tenant);
            Assert.Equal("user-1", user);
        }

        var (corrAfter, tenantAfter, userAfter) = TeamsLogContext.Snapshot();
        Assert.Null(corrAfter);
        Assert.Null(tenantAfter);
        Assert.Null(userAfter);
    }

    [Fact]
    public void Push_NestedScope_InheritsUnspecifiedKeysFromParent()
    {
        using (TeamsLogContext.Push("corr-outer", "tenant-outer", "user-outer"))
        {
            using (TeamsLogContext.Push(correlationId: "corr-inner", tenantId: null, userId: null))
            {
                var (corr, tenant, user) = TeamsLogContext.Snapshot();
                Assert.Equal("corr-inner", corr);
                Assert.Equal("tenant-outer", tenant);
                Assert.Equal("user-outer", user);
            }

            var (outerCorr, outerTenant, outerUser) = TeamsLogContext.Snapshot();
            Assert.Equal("corr-outer", outerCorr);
            Assert.Equal("tenant-outer", outerTenant);
            Assert.Equal("user-outer", outerUser);
        }
    }

    [Fact]
    public void Push_NestedScope_OverridesParentWhenInnerSupplied()
    {
        using (TeamsLogContext.Push("corr-outer", "tenant-outer", "user-outer"))
        {
            using (TeamsLogContext.Push("corr-inner", "tenant-inner", "user-inner"))
            {
                var (corr, tenant, user) = TeamsLogContext.Snapshot();
                Assert.Equal("corr-inner", corr);
                Assert.Equal("tenant-inner", tenant);
                Assert.Equal("user-inner", user);
            }

            var (outerCorr, _, _) = TeamsLogContext.Snapshot();
            Assert.Equal("corr-outer", outerCorr);
        }
    }

    [Fact]
    public void Push_AllNullArgs_ReturnsNoopToken_AndLeavesContextEmpty()
    {
        using (TeamsLogContext.Push(null, null, null))
        {
            var (corr, tenant, user) = TeamsLogContext.Snapshot();
            Assert.Null(corr);
            Assert.Null(tenant);
            Assert.Null(user);
        }
    }

    [Fact]
    public async Task Push_FlowsAcrossAwait()
    {
        using (TeamsLogContext.Push("corr-async", "tenant-async", "user-async"))
        {
            await Task.Yield();

            var (corr, tenant, user) = TeamsLogContext.Snapshot();
            Assert.Equal("corr-async", corr);
            Assert.Equal("tenant-async", tenant);
            Assert.Equal("user-async", user);
        }
    }

    [Fact]
    public void Push_DoubleDispose_IsSafe()
    {
        var token = TeamsLogContext.Push("c", "t", "u");
        token.Dispose();
        token.Dispose();

        var (corr, _, _) = TeamsLogContext.Snapshot();
        Assert.Null(corr);
    }
}
