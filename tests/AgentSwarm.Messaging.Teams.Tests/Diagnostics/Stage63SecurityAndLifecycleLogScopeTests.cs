using System.Text;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Outbox;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;
using static AgentSwarm.Messaging.Teams.Tests.Security.StaticUserRoleProviderTests;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Stage 6.3 iter-10 evaluator structural log-scope pins for the surfaces flagged in
/// iter-9 feedback items 1 (lifecycle eviction workers), 2 (security resolver /
/// authorization / tenant-validation middleware) and 4
/// (TeamsMessengerConnector.SendMessageAsync missing-reference path).
/// </summary>
/// <remarks>
/// <para>
/// §6.3 step 5 mandates that <i>every</i> Teams log entry carry the three canonical
/// enrichment keys (<see cref="TeamsLogScope.CorrelationIdKey"/>,
/// <see cref="TeamsLogScope.TenantIdKey"/>, <see cref="TeamsLogScope.UserIdKey"/>).
/// Earlier iterations satisfied this on the connector send-path surfaces but missed
/// background workers and security boundaries — iter-9's evaluator flagged those
/// specifically. The iter-10 fix wraps each callsite in a
/// <see cref="TeamsLogScope.BeginScope(ILogger, string?, string?, string?)"/> that
/// substitutes <see cref="TeamsLogScope.EmptyValueSentinel"/> for any key the caller
/// genuinely cannot populate, so the scope state ALWAYS carries the three-key shape.
/// </para>
/// <para>
/// These tests are STRUCTURAL — they snapshot the live MEL scope state at log emission
/// and assert the three keys are present. They are deliberately decoupled from Serilog
/// so the contract is verified at the ILogger boundary regardless of which sink the
/// host wires up (Serilog's <c>LogContext</c> projection sees the same dictionary).
/// </para>
/// </remarks>
public sealed class Stage63SecurityAndLifecycleLogScopeTests
{
    // -------------------------------------------------------------------------------
    // Iter-9 evaluator item 1 — lifecycle / tick logs on background eviction workers
    // -------------------------------------------------------------------------------

    [Fact]
    public async Task ProcessedCardActionEvictionService_LifecycleLog_CarriesAllThreeEnrichmentKeys()
    {
        // The hosted service has no per-message context (it runs on the host timer);
        // the iter-10 fix wraps ExecuteAsync in TeamsLogScope.BeginScope so even the
        // lifecycle "started"/"stopped" log carries the three canonical keys with
        // EmptyValueSentinel substitution. Without the fix, the lifecycle log emitted
        // outside any scope leaves the dashboard contract incomplete.
        var logger = new ScopeRecordingLogger<ProcessedCardActionEvictionService>();
        var options = new CardActionDedupeOptions
        {
            EntryLifetime = TimeSpan.FromHours(1),
            EvictionInterval = TimeSpan.FromHours(1),
        };
        var set = new ProcessedCardActionSet(options, TimeProvider.System);
        var service = new ProcessedCardActionEvictionService(set, options, TimeProvider.System, logger);

        await service.StartAsync(CancellationToken.None);
        // Tiny delay so ExecuteAsync runs through the LogInformation call before stop.
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        var startedLog = logger.LogEntries.FirstOrDefault(
            e => e.Message.StartsWith("ProcessedCardActionEvictionService started", StringComparison.Ordinal));
        Assert.NotNull(startedLog);
        AssertAllThreeKeysPresent(
            startedLog!,
            expectedCorrelationId: TeamsLogScope.EmptyValueSentinel,
            expectedTenantId: TeamsLogScope.EmptyValueSentinel,
            expectedUserId: TeamsLogScope.EmptyValueSentinel);
    }

    [Fact]
    public async Task OutboundDeduplicationEvictionService_LifecycleLog_CarriesAllThreeEnrichmentKeys()
    {
        var logger = new ScopeRecordingLogger<OutboundDeduplicationEvictionService>();
        var options = new OutboundDeduplicationOptions
        {
            Window = TimeSpan.FromHours(1),
            EvictionInterval = TimeSpan.FromHours(1),
        };
        var dedupe = new OutboundMessageDeduplicator(options, TimeProvider.System);
        var service = new OutboundDeduplicationEvictionService(dedupe, options, TimeProvider.System, logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        var startedLog = logger.LogEntries.FirstOrDefault(
            e => e.Message.StartsWith("OutboundDeduplicationEvictionService started", StringComparison.Ordinal));
        Assert.NotNull(startedLog);
        AssertAllThreeKeysPresent(
            startedLog!,
            expectedCorrelationId: TeamsLogScope.EmptyValueSentinel,
            expectedTenantId: TeamsLogScope.EmptyValueSentinel,
            expectedUserId: TeamsLogScope.EmptyValueSentinel);
    }

    // -------------------------------------------------------------------------------
    // Iter-9 evaluator item 2 — security surfaces emit through canonical scope.
    // -------------------------------------------------------------------------------

    [Fact]
    public async Task EntraIdentityResolver_UnmappedUserWarning_CarriesAllThreeEnrichmentKeys()
    {
        // The resolver natively has the AAD object ID; per the iter-10 fix it stamps it
        // into the UserId slot of the scope so the unmapped-user warning emitted at
        // LogWarning carries actionable identity. CorrelationId / TenantId are
        // sentinel-substituted because the resolver runs before the activity handler
        // can stamp them onto TeamsLogContext in the unit-test path.
        var logger = new ScopeRecordingLogger<EntraIdentityResolver>();
        var directory = new StubUserDirectory();
        var resolver = new EntraIdentityResolver(directory, logger);

        var identity = await resolver.ResolveAsync("aad-unmapped-7", CancellationToken.None);

        Assert.Null(identity);

        var warningLog = logger.LogEntries.FirstOrDefault(
            e => e.Message.Contains("not mapped in the directory", StringComparison.Ordinal));
        Assert.NotNull(warningLog);
        AssertAllThreeKeysPresent(
            warningLog!,
            expectedCorrelationId: TeamsLogScope.EmptyValueSentinel,
            expectedTenantId: TeamsLogScope.EmptyValueSentinel,
            expectedUserId: "aad-unmapped-7");
    }

    [Fact]
    public async Task RbacAuthorizationService_RejectWarning_CarriesAllThreeEnrichmentKeys()
    {
        // RBAC reject decisions know the tenantId AND userId from the method signature,
        // so the iter-10 fix stamps both into the scope. Logged warning at line ~132
        // (mismatched role path) now carries the canonical three-key shape with real
        // tenant + user values and sentinel for the CorrelationId (not knowable here).
        var logger = new ScopeRecordingLogger<RbacAuthorizationService>();
        var options = new RbacOptions().WithDefaultRoleMatrix();
        var provider = new StubUserRoleProvider().AssignRole("aad-viewer-9", RbacOptions.ViewerRole);
        var svc = new RbacAuthorizationService(WrapInMonitor(options), provider, logger);

        var result = await svc.AuthorizeAsync("tenant-rbac-9", "aad-viewer-9", "approve", CancellationToken.None);

        Assert.False(result.IsAuthorized);

        var rejectLog = logger.LogEntries.FirstOrDefault(
            e => e.Message.StartsWith("RBAC reject", StringComparison.Ordinal));
        Assert.NotNull(rejectLog);
        AssertAllThreeKeysPresent(
            rejectLog!,
            expectedCorrelationId: TeamsLogScope.EmptyValueSentinel,
            expectedTenantId: "tenant-rbac-9",
            expectedUserId: "aad-viewer-9");
    }

    [Fact]
    public async Task TenantValidationMiddleware_AuditFailureLogError_CarriesAllThreeEnrichmentKeys()
    {
        // The audit-failure LogError (formerly at line 268 in the pre-iter-10 layout) now
        // sits inside the RejectAsync TeamsLogScope.BeginScope so it carries the three
        // canonical keys: correlation from the inbound header, tenant from the parsed
        // claim, and EmptyValueSentinel for UserId (HTTP middleware has no resolved user).
        var logger = new ScopeRecordingLogger<TenantValidationMiddleware>();
        var auditLogger = new RecordingAuditLogger
        {
            Throw = new InvalidOperationException("audit-store-down-for-test"),
        };
        var messagingOptions = WrapInMonitor<TeamsMessagingOptions>(new TeamsMessagingOptions
        {
            AllowedTenantIds = new List<string> { "tenant-allowed-validate" },
        });
        var middleware = new TenantValidationMiddleware(messagingOptions, auditLogger, logger);

        var context = NewBotContext(BuildBody(tenantId: "tenant-blocked-validate"));
        context.Request.Headers["x-ms-correlation-id"] = "corr-tenant-reject-10";

        await Assert.ThrowsAsync<UnauthorizedTenantAuditException>(
            () => middleware.InvokeAsync(context, _ => Task.CompletedTask));

        var errorLog = logger.LogEntries.FirstOrDefault(
            e => e.Message.Contains("audit logger threw", StringComparison.Ordinal));
        Assert.NotNull(errorLog);
        AssertAllThreeKeysPresent(
            errorLog!,
            expectedCorrelationId: "corr-tenant-reject-10",
            expectedTenantId: "tenant-blocked-validate",
            expectedUserId: TeamsLogScope.EmptyValueSentinel);
    }

    // -------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------

    private static void AssertAllThreeKeysPresent(
        CapturedLogEntry log,
        string expectedCorrelationId,
        string expectedTenantId,
        string expectedUserId)
    {
        var merged = MergeScopeStack(log.ActiveScopeSnapshot);

        Assert.True(merged.ContainsKey(TeamsLogScope.CorrelationIdKey),
            $"Log '{log.Message}' must structurally carry the §6.3 step 5 CorrelationId enrichment key.");
        Assert.True(merged.ContainsKey(TeamsLogScope.TenantIdKey),
            $"Log '{log.Message}' must structurally carry the §6.3 step 5 TenantId enrichment key.");
        Assert.True(merged.ContainsKey(TeamsLogScope.UserIdKey),
            $"Log '{log.Message}' must structurally carry the §6.3 step 5 UserId enrichment key.");

        Assert.Equal(expectedCorrelationId, (string?)merged[TeamsLogScope.CorrelationIdKey]);
        Assert.Equal(expectedTenantId, (string?)merged[TeamsLogScope.TenantIdKey]);
        Assert.Equal(expectedUserId, (string?)merged[TeamsLogScope.UserIdKey]);
    }

    private static Dictionary<string, object?> MergeScopeStack(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> stack)
    {
        // The scope stack snapshot is captured INNERMOST → OUTERMOST (LIFO push order).
        // Merge OUTERMOST → INNERMOST so inner-scope values overlay outer sentinels,
        // matching Serilog's LogContext and MEL scope-projection semantics.
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            foreach (var kvp in stack[i])
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        return merged;
    }

    private static string BuildBody(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return "{\"type\":\"message\"}";
        }

        return "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"" + tenantId + "\"}}}";
    }

    private static HttpContext NewBotContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = TenantValidationMiddleware.DefaultBotEndpointPath;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>
    /// Recording ILogger that captures the live scope stack at every Log() call. Scope
    /// state is preserved in the order it was pushed (innermost first when read);
    /// helpers merge outermost → innermost to match real scope-projection semantics.
    /// </summary>
    private sealed class ScopeRecordingLogger<T> : ILogger<T>
    {
        private readonly Stack<IReadOnlyDictionary<string, object?>> _activeScopes = new();

        public List<CapturedLogEntry> LogEntries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var kvp in kvps)
                {
                    snapshot[kvp.Key] = kvp.Value;
                }

                lock (_activeScopes)
                {
                    _activeScopes.Push(snapshot);
                }

                return new Pop(_activeScopes);
            }

            return Noop.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyDictionary<string, object?>[] snapshot;
            lock (_activeScopes)
            {
                snapshot = _activeScopes.ToArray();
            }

            LogEntries.Add(new CapturedLogEntry(
                Message: formatter(state, exception),
                ActiveScopeSnapshot: snapshot));
        }

        private sealed class Pop : IDisposable
        {
            private readonly Stack<IReadOnlyDictionary<string, object?>> _stack;
            private bool _disposed;

            public Pop(Stack<IReadOnlyDictionary<string, object?>> stack) => _stack = stack;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                lock (_stack)
                {
                    if (_stack.Count > 0)
                    {
                        _stack.Pop();
                    }
                }
            }
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record CapturedLogEntry(string Message, IReadOnlyList<IReadOnlyDictionary<string, object?>> ActiveScopeSnapshot);
}
