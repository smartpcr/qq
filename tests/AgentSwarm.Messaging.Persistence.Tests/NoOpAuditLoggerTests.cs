namespace AgentSwarm.Messaging.Persistence.Tests;

/// <summary>
/// Behaviour tests for the <see cref="NoOpAuditLogger"/> stub. The Stage 1.3 brief is
/// explicit: the stub "accepts all <c>LogAsync</c> calls as no-ops (returns
/// <see cref="Task.CompletedTask"/>)". These tests lock in that contract — including the
/// permissive treatment of null entries and canceled tokens — so the stub remains a
/// frictionless default for Stage 2.1 DI registration until <c>SqlAuditLogger</c> ships
/// in Stage 5.2.
/// </summary>
public sealed class NoOpAuditLoggerTests
{
    private static AuditEntry SampleEntry()
    {
        var ts = DateTimeOffset.UtcNow;
        return new AuditEntry
        {
            Timestamp = ts,
            CorrelationId = "corr",
            EventType = AuditEventTypes.CommandReceived,
            ActorId = "actor",
            ActorType = AuditActorTypes.User,
            TenantId = "tenant",
            AgentId = null,
            TaskId = null,
            ConversationId = null,
            Action = "agent ask",
            PayloadJson = "{}",
            Outcome = AuditOutcomes.Success,
            Checksum = AuditEntry.ComputeChecksum(
                ts, "corr", AuditEventTypes.CommandReceived, "actor", AuditActorTypes.User,
                "tenant", null, null, null, "agent ask", "{}", AuditOutcomes.Success),
        };
    }

    [Fact]
    public async Task LogAsync_ValidEntry_ReturnsCompletedTask()
    {
        IAuditLogger logger = new NoOpAuditLogger();

        var task = logger.LogAsync(SampleEntry(), CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully, "NoOpAuditLogger must return a synchronously completed task.");
        await task;
    }

    [Fact]
    public async Task LogAsync_NullEntry_ReturnsCompletedTask()
    {
        // Per the Stage 1.3 brief, the no-op stub "accepts ALL LogAsync calls as no-ops".
        // It must not validate arguments — that responsibility belongs to the concrete
        // SqlAuditLogger landing in Stage 5.2.
        IAuditLogger logger = new NoOpAuditLogger();

        var task = logger.LogAsync(null!, CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully, "NoOpAuditLogger must accept null entry without throwing.");
        await task;
    }

    [Fact]
    public async Task LogAsync_CancelledToken_ReturnsCompletedTask()
    {
        // Per the Stage 1.3 brief, the no-op stub must return Task.CompletedTask for ALL
        // calls — it does not observe cancellation. Concrete loggers are free to do so.
        IAuditLogger logger = new NoOpAuditLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = logger.LogAsync(SampleEntry(), cts.Token);

        Assert.True(task.IsCompletedSuccessfully, "NoOpAuditLogger must ignore cancellation and return a completed task.");
        await task;
    }

    [Fact]
    public async Task LogAsync_NullEntryAndCancelledToken_StillReturnsCompletedTask()
    {
        // The most adversarial inputs the stub will ever see — both must be accepted.
        IAuditLogger logger = new NoOpAuditLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = logger.LogAsync(null!, cts.Token);

        Assert.True(task.IsCompletedSuccessfully, "NoOpAuditLogger must return CompletedTask for all calls.");
        await task;
    }

    [Fact]
    public void LogAsync_AlwaysReturnsTaskCompletedTaskSingleton()
    {
        // Document the stronger guarantee that the stub returns the singleton
        // Task.CompletedTask — useful for any caller that compares by reference to detect
        // a synchronous no-op path. Concrete loggers are NOT required to preserve this
        // identity.
        IAuditLogger logger = new NoOpAuditLogger();

        var task = logger.LogAsync(SampleEntry(), CancellationToken.None);

        Assert.Same(Task.CompletedTask, task);
    }

    [Fact]
    public void NoOpAuditLogger_ImplementsIAuditLogger()
    {
        Assert.True(typeof(IAuditLogger).IsAssignableFrom(typeof(NoOpAuditLogger)));
    }
}
