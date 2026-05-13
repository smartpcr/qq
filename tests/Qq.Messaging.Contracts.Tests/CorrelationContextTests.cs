using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class CorrelationContextTests
{
    [Fact]
    public void Default_GeneratesNonEmptyCorrelationId()
    {
        var ctx = new CorrelationContext();
        Assert.False(string.IsNullOrWhiteSpace(ctx.CorrelationId));
    }

    [Fact]
    public void CreateChild_ChainsParentCorrelationAsCausation()
    {
        var parent = new CorrelationContext { TraceId = "trace-1" };
        var child = parent.CreateChild();

        Assert.Equal(parent.CorrelationId, child.CausationId);
        Assert.NotEqual(parent.CorrelationId, child.CorrelationId);
        Assert.Equal("trace-1", child.TraceId);
    }

    [Fact]
    public void TwoDefaultInstances_HaveDifferentCorrelationIds()
    {
        var a = new CorrelationContext();
        var b = new CorrelationContext();
        Assert.NotEqual(a.CorrelationId, b.CorrelationId);
    }
}
