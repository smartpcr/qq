using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Stage 2.2 scenario: "Correlation ID propagation — Given an incoming activity without a
/// <c>CorrelationId</c> header, When <c>OnTurnAsync</c> runs, Then a new GUID-based
/// <c>CorrelationId</c> is attached to the turn context." Plus the positive case where an
/// upstream-supplied correlation ID flows through unchanged.
/// </summary>
public sealed class CorrelationIdPropagationTests
{
    [Fact]
    public async Task OnTurnAsync_NoUpstreamCorrelationId_GeneratesNewGuidOnTurnState()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status", correlationId: null);

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.NotNull(context.CorrelationId);
        Assert.True(Guid.TryParse(context.CorrelationId, out _));
    }

    [Fact]
    public async Task OnTurnAsync_UpstreamCorrelationId_FlowsToDispatcherUnchanged()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var upstream = "corr-xyz-123";
        var activity = NewPersonalMessage("agent status", correlationId: upstream);

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.Equal(upstream, context.CorrelationId);
    }

    [Fact]
    public async Task OnTurnAsync_UpstreamCorrelationId_FlowsToAuditEntriesOnRejection()
    {
        var harness = Build();
        var upstream = "corr-rejection-trace";
        var activity = NewPersonalMessage("approve", aadObjectId: "aad-unmapped", correlationId: upstream);

        await ProcessAsync(harness, activity);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(upstream, audit.CorrelationId);
    }

    /// <summary>
    /// Iter-4 hardening regression: incidental leading/trailing whitespace on the
    /// upstream <c>correlationId</c> property must be trimmed before propagation so
    /// downstream byte-exact joins (Application Insights / Kusto trace correlation,
    /// SQL audit-query equality predicates) are not silently broken by a padded value.
    /// </summary>
    [Theory]
    [InlineData(" corr-abc-123")]
    [InlineData("corr-abc-123 ")]
    [InlineData("  corr-abc-123  ")]
    [InlineData("\tcorr-abc-123\t")]
    public async Task OnTurnAsync_UpstreamCorrelationIdWithWhitespace_TrimmedBeforePropagation(string padded)
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status", correlationId: padded);

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.Equal("corr-abc-123", context.CorrelationId);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("corr-abc-123", audit.CorrelationId);
    }

    /// <summary>
    /// Iter-4 hardening regression: a whitespace-only upstream value falls back to a
    /// fresh GUID (the same path taken when no upstream value is supplied). This
    /// prevents a bogus pure-whitespace ID from polluting the trace correlation field.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("")]
    public async Task OnTurnAsync_WhitespaceOnlyUpstreamCorrelationId_FallsBackToGuid(string blank)
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status", correlationId: blank);

        await ProcessAsync(harness, activity);

        var context = Assert.Single(harness.Dispatcher.Dispatched);
        Assert.True(Guid.TryParse(context.CorrelationId, out _));
    }
}
