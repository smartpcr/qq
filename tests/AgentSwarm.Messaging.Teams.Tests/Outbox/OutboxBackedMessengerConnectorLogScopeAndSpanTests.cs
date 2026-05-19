using System.Diagnostics;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Outbox;
using AgentSwarm.Messaging.Teams.Tests.Diagnostics;
using Microsoft.Extensions.Logging;
using OtelActivity = System.Diagnostics.Activity;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// Stage 6.3 iter-7 evaluator feedback items 1 and 2 — pin the contracts that
/// <see cref="OutboxBackedMessengerConnector"/> (the production
/// <see cref="IMessengerConnector"/> in every <c>AddTeamsOutboxEngine</c>
/// composition) (a) wraps every log entry in a canonical <see cref="TeamsLogScope"/>
/// per §6.3 step 5 and (b) starts the canonical <c>TeamsConnector.SendMessage</c> /
/// <c>TeamsConnector.SendQuestion</c> OpenTelemetry spans on the production
/// outbox-backed boundary per §6.3 step 1. Iter-6 left both gaps uncovered, which
/// surfaced in the iter-6 evaluator review as items 1 and 2.
/// </summary>
[Collection(TeamsTelemetryCollection.Name)]
public sealed class OutboxBackedMessengerConnectorLogScopeAndSpanTests
{
    // -----------------------------------------------------------------------------------
    // Iter-7 evaluator feedback item 1 — log-scope coverage
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task SendMessageAsync_LayersCorrelationAndTenantUserScopes_OnSuccessfulEnqueue()
    {
        // The decorator opens an OUTER correlation-only scope at the SendMessageAsync
        // boundary so the dedupe-coordination logs (loser-suppressed Information at
        // line 254, retry-exhaustion Warning at 275) carry the CorrelationId enrichment
        // even before the conversation reference is resolved. Once the router returns
        // the resolved reference inside EnqueueCoreAsync, a NESTED scope layers
        // TenantId + InternalUserId from the reference on top of the outer scope so
        // the "Enqueued outbox entry" Information emitted at line 344 (and the
        // post-enqueue PreEnqueueSaveQuestionAsync log on the question path) carries
        // the full §6.3 step 5 three-key contract.
        //
        // Stage 6.3 iter-10 evaluator fix item 3 — the OUTER scope now also carries
        // all three keys, with TenantId / UserId substituted to
        // TeamsLogScope.EmptyValueSentinel ("-") since MessengerMessage payloads
        // carry no tenant/user identifier. This pin asserts BOTH scope frames in the
        // iter-10 shape so a regression that drops sentinel substitution surfaces here.
        var logger = new ScopeRecordingLogger<OutboxBackedMessengerConnector>();
        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-scope"] = NewReference(tenantId: "tenant-scope");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            logger,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: null);

        await decorator.SendMessageAsync(SampleMessage("m-scope-1", conversationId: "conv-scope"), CancellationToken.None);

        // Outer scope — CorrelationId real, TenantId/UserId sentinel-substituted.
        var outerScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c) && (string?)c == "corr-m-scope-1"
                 && d.TryGetValue(TeamsLogScope.TenantIdKey, out var t) && (string?)t == TeamsLogScope.EmptyValueSentinel);
        Assert.Equal(TeamsLogScope.EmptyValueSentinel, outerScope[TeamsLogScope.UserIdKey]);

        // Inner scope — tenant + user from the resolved TeamsConversationReference.
        // CorrelationId is inherited via TeamsLogContext's AsyncLocal parent pointer.
        var innerScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.TenantIdKey, out var t) && (string?)t == "tenant-scope");
        Assert.Equal("user-tenant-scope", innerScope[TeamsLogScope.UserIdKey]);
        Assert.Equal("corr-m-scope-1", innerScope[TeamsLogScope.CorrelationIdKey]);

        // And the canonical "Enqueued outbox entry" Information landed WHILE the
        // inner scope was active — proving the log site is enriched with all three
        // keys per the §6.3 step 5 contract.
        var enqueuedLog = Assert.Single(logger.LogEntries,
            e => e.Message.Contains("Enqueued outbox entry", StringComparison.Ordinal));
        AssertActiveScopeCarriesThreeKeys(
            enqueuedLog,
            expectedCorrelationId: "corr-m-scope-1",
            expectedTenantId: "tenant-scope",
            expectedUserId: "user-tenant-scope");
    }

    [Fact]
    public async Task SendQuestionAsync_OpensFullThreeKeyScope_AtConnectorBoundary()
    {
        // Unlike MessengerMessage, AgentQuestion natively carries TenantId AND the
        // natural-key user identifier so the decorator can open the FULL
        // (CorrelationId, TenantId, UserId) scope at the outer boundary, ensuring
        // PreEnqueueSaveQuestionAsync's "row already present, skipping duplicate save"
        // Information at line ~481 is enriched even though it fires BEFORE the
        // EnqueueAsync call.
        var logger = new ScopeRecordingLogger<OutboxBackedMessengerConnector>();
        var router = new RecordingConversationReferenceStore();
        router.UserReferences[("tenant-q", "user-q-7")] = NewReference(tenantId: "tenant-q");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            logger,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: null);

        await decorator.SendQuestionAsync(
            SampleQuestion("q-scope-7", tenantId: "tenant-q", userId: "user-q-7"),
            CancellationToken.None);

        // The outer scope opened by SendQuestionAsync carries all three keys directly
        // (the AgentQuestion payload supplies TenantId and TargetUserId).
        var outerScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c) && (string?)c == "corr-q-scope-7");
        Assert.Equal("tenant-q", outerScope[TeamsLogScope.TenantIdKey]);
        Assert.Equal("user-q-7", outerScope[TeamsLogScope.UserIdKey]);

        var enqueuedLog = Assert.Single(logger.LogEntries,
            e => e.Message.Contains("Enqueued outbox entry", StringComparison.Ordinal));
        AssertActiveScopeCarriesThreeKeys(
            enqueuedLog,
            expectedCorrelationId: "corr-q-scope-7",
            expectedTenantId: "tenant-q",
            expectedUserId: "user-q-7");
    }

    [Fact]
    public async Task SendQuestionAsync_ChannelTarget_OpensCorrelationTenantUserScope_WithSentinelForUserId()
    {
        // Stage 6.3 iter-10 evaluator fix item 3 — channel-targeted questions carry
        // no TargetUserId; TeamsLogScope.BeginScope substitutes
        // TeamsLogScope.EmptyValueSentinel ("-") into the UserId slot so the scope
        // structurally carries all three §6.3 step 5 keys — never a missing slot.
        // The substitute value is deliberately NOT the channel ID (mirrors the
        // iter-5 evaluator STRUCTURAL fix on TeamsMessengerConnector.SendQuestionAsync)
        // so dashboards / RBAC queries are not polluted with channel IDs that look
        // like user IDs.
        var logger = new ScopeRecordingLogger<OutboxBackedMessengerConnector>();
        var router = new RecordingConversationReferenceStore();
        router.ChannelReferences[("tenant-q", "chan-q-7")] = NewReference(tenantId: "tenant-q");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            logger,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: null);

        await decorator.SendQuestionAsync(
            SampleQuestion("q-scope-chan", tenantId: "tenant-q", channelId: "chan-q-7"),
            CancellationToken.None);

        var outerScope = Assert.Single(
            logger.ScopeDictionaries,
            d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c) && (string?)c == "corr-q-scope-chan");
        Assert.Equal("tenant-q", outerScope[TeamsLogScope.TenantIdKey]);
        Assert.True(outerScope.ContainsKey(TeamsLogScope.UserIdKey),
            "Channel-targeted AgentQuestion must structurally carry the UserId enrichment key per §6.3 step 5.");
        Assert.Equal(TeamsLogScope.EmptyValueSentinel, outerScope[TeamsLogScope.UserIdKey]);
        Assert.NotEqual(
            "chan-q-7",
            outerScope[TeamsLogScope.UserIdKey]);
    }

    // -----------------------------------------------------------------------------------
    // Iter-7 evaluator feedback item 2 — span coverage on the production outbox-backed
    // boundary. The reliable deployment composition replaces IMessengerConnector with
    // OutboxBackedMessengerConnector and NEVER invokes the inner TeamsMessengerConnector
    // for outbound sends, so the canonical TeamsConnector.SendMessage / SendQuestion
    // spans MUST be started here as well.
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task SendMessageAsync_StartsTeamsConnectorSendMessageSpan_WithCanonicalAttributes()
    {
        var captured = new List<OtelActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TeamsConnectorTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var router = new RecordingConversationReferenceStore();
        router.ConversationIdReferences["conv-span-1"] = NewReference(tenantId: "tenant-span-1");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await decorator.SendMessageAsync(
            SampleMessage("m-span-1", conversationId: "conv-span-1"),
            CancellationToken.None);

        var span = Assert.Single(captured, a => a.OperationName == TeamsConnectorTelemetry.SendMessageActivityName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        // Iter-7 evaluator fix item 4 — correlationId IS on spans (bounded by sampling)
        // even though it's removed from metric tags.
        Assert.Equal("corr-m-span-1", span.GetTagItem(TeamsConnectorTelemetry.CorrelationIdTag));
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeMessengerMessage, span.GetTagItem(TeamsConnectorTelemetry.MessageTypeTag));
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeConversation, span.GetTagItem(TeamsConnectorTelemetry.DestinationTypeTag));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task SendQuestionAsync_StartsTeamsConnectorSendQuestionSpan_WithCanonicalAttributes()
    {
        var captured = new List<OtelActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TeamsConnectorTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var router = new RecordingConversationReferenceStore();
        router.UserReferences[("tenant-span-q", "user-span-q")] = NewReference(tenantId: "tenant-span-q");

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            router,
            router,
            new RecordingAgentQuestionStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await decorator.SendQuestionAsync(
            SampleQuestion("q-span-1", tenantId: "tenant-span-q", userId: "user-span-q"),
            CancellationToken.None);

        var span = Assert.Single(captured, a => a.OperationName == TeamsConnectorTelemetry.SendQuestionActivityName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("corr-q-span-1", span.GetTagItem(TeamsConnectorTelemetry.CorrelationIdTag));
        Assert.Equal(TeamsConnectorTelemetry.MessageTypeAgentQuestion, span.GetTagItem(TeamsConnectorTelemetry.MessageTypeTag));
        // User-scoped natural-key routing emits DestinationType=User on the span tag
        // so trace consumers can slice the SendQuestion span volume by destination kind.
        Assert.Equal(TeamsConnectorTelemetry.DestinationTypeUser, span.GetTagItem(TeamsConnectorTelemetry.DestinationTypeTag));
    }

    [Fact]
    public async Task SendMessageAsync_RouterReturnsNull_MarksSpanError_AndPropagatesException()
    {
        // The missing-reference failure path inside EnqueueCoreAsync MUST set the
        // span's status to Error (and stamp the exception.type tag) so trace
        // consumers can slice failed sends without re-deriving the outcome from the
        // payload. Pins the catch-when-activity branch in EnqueueCoreAsync.
        var captured = new List<OtelActivity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == TeamsConnectorTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => captured.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        using var telemetry = new TeamsConnectorTelemetry(NullOutboxQueueDepthProvider.Instance);

        var decorator = new OutboxBackedMessengerConnector(
            new RecordingMessengerConnector(),
            new InMemoryRecordingOutbox(),
            new RecordingConversationReferenceStore(),
            new RecordingConversationReferenceStore(),
            new RecordingAgentQuestionStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxBackedMessengerConnector>.Instance,
            timeProvider: null,
            outboundDeduplicator: null,
            telemetry: telemetry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => decorator.SendMessageAsync(SampleMessage("m-span-err"), CancellationToken.None));

        var span = Assert.Single(captured, a => a.OperationName == TeamsConnectorTelemetry.SendMessageActivityName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("exception.type"));
    }

    // -----------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------

    private static void AssertActiveScopeCarriesThreeKeys(
        ScopeRecordingLogger<OutboxBackedMessengerConnector>.CapturedLogEntry entry,
        string expectedCorrelationId,
        string expectedTenantId,
        string expectedUserId)
    {
        // ScopeRecordingLogger snapshots active scopes at log time via Stack.ToArray()
        // which returns LIFO order (innermost first). Merge in REVERSE so the
        // innermost scope wins per the canonical MEL scope-projection contract —
        // a nested TeamsLogScope BeginScope should shadow the outer scope's value
        // for any key both frames carry. Stage 6.3 iter-10: the outer scope now
        // sentinel-substitutes empty values, so merging in stack order (innermost
        // last) would incorrectly let the outer sentinel overwrite the inner real
        // value.
        var mergedKeys = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = entry.ActiveScopeSnapshot.Count - 1; i >= 0; i--)
        {
            foreach (var kvp in entry.ActiveScopeSnapshot[i])
            {
                mergedKeys[kvp.Key] = kvp.Value;
            }
        }

        Assert.Equal(expectedCorrelationId, mergedKeys[TeamsLogScope.CorrelationIdKey]);
        Assert.Equal(expectedTenantId, mergedKeys[TeamsLogScope.TenantIdKey]);
        Assert.Equal(expectedUserId, mergedKeys[TeamsLogScope.UserIdKey]);
    }

    private static TeamsConversationReference NewReference(string tenantId) => new()
    {
        Id = $"ref-{tenantId}",
        TenantId = tenantId,
        InternalUserId = $"user-{tenantId}",
        ServiceUrl = "https://smba.trafficmanager.net/test/",
        ConversationId = $"conv-{tenantId}",
        BotId = "bot-1",
        ReferenceJson = $"{{\"tenant\":\"{tenantId}\"}}",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static MessengerMessage SampleMessage(string id, string conversationId = "conv-1") => new(
        MessageId: id,
        CorrelationId: $"corr-{id}",
        AgentId: "agent-1",
        TaskId: "task-1",
        ConversationId: conversationId,
        Body: "hello",
        Severity: MessageSeverities.Info,
        Timestamp: DateTimeOffset.UnixEpoch);

    private static AgentQuestion SampleQuestion(string id, string tenantId = "tenant-1", string? userId = null, string? channelId = null) => new()
    {
        QuestionId = id,
        TenantId = tenantId,
        TargetUserId = userId,
        TargetChannelId = channelId,
        CorrelationId = $"corr-{id}",
        AgentId = "agent-1",
        TaskId = "task-1",
        Title = "Title",
        Body = "body",
        Severity = MessageSeverities.Info,
        Status = AgentQuestionStatuses.Open,
        AllowedActions = new[] { new HumanAction("yes", "Yes", "yes", false) },
        ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1),
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>
    /// Recording <see cref="ILogger{T}"/> that captures both BeginScope state and
    /// the active scope stack at log emission time. The scope stack snapshot enables
    /// assertions that the (CorrelationId, TenantId, UserId) three-key contract is
    /// satisfied even when the keys are split across LAYERED scopes (an outer
    /// correlation-only scope + an inner tenant/user scope is a valid configuration
    /// because Serilog's LogContext / MEL scope-projection both merge the active
    /// scope stack into the log envelope).
    /// </summary>
    internal sealed class ScopeRecordingLogger<T> : ILogger<T>
    {
        private readonly AsyncLocal<Stack<IReadOnlyDictionary<string, object?>>> _activeScopes = new();

        public List<IReadOnlyDictionary<string, object?>> ScopeDictionaries { get; } = new();
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

                ScopeDictionaries.Add(snapshot);
                var stack = _activeScopes.Value ??= new Stack<IReadOnlyDictionary<string, object?>>();
                stack.Push(snapshot);
                return new Pop(stack);
            }

            return Noop.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var stack = _activeScopes.Value;
            var snapshot = stack is null
                ? Array.Empty<IReadOnlyDictionary<string, object?>>()
                : stack.ToArray();
            LogEntries.Add(new CapturedLogEntry(
                Message: formatter(state, exception),
                ActiveScopeSnapshot: snapshot));
        }

        public sealed record CapturedLogEntry(string Message, IReadOnlyList<IReadOnlyDictionary<string, object?>> ActiveScopeSnapshot);

        private sealed class Pop : IDisposable
        {
            private readonly Stack<IReadOnlyDictionary<string, object?>> _stack;
            private bool _disposed;
            public Pop(Stack<IReadOnlyDictionary<string, object?>> stack) => _stack = stack;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_stack.Count > 0) _stack.Pop();
            }
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }
}
