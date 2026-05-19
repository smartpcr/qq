using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using static AgentSwarm.Messaging.Teams.Tests.TeamsMessengerConnectorTests;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Iter-2 evaluator feedback item 1 — verifies that the Stage 6.3 step-5 structured
/// logging contract ("Serilog enrichers for CorrelationId, TenantId, UserId on every
/// log entry") is honored on EVERY public connector method that emits log entries.
/// Iter 1 covered <c>SendMessageAsync</c> and <c>SendQuestionAsync</c>; this file
/// covers the remaining card-lifecycle paths (<c>DeleteCardAsync</c>,
/// <c>UpdateCardAsync</c>) and the shared retry helper they invoke
/// (<c>ExecuteWithInlineRetryAsync</c> at <c>TeamsMessengerConnector.cs:~885</c>).
/// </summary>
/// <remarks>
/// These tests inject a <see cref="ScopeCapturingLogger{T}"/> in place of the usual
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> and assert
/// that <see cref="ILogger.BeginScope{TState}"/> is invoked with the canonical
/// <see cref="TeamsLogScope.CorrelationIdKey"/> entry containing the question
/// identifier, AND that any subsequent log entry written inside the scope sees the
/// scope in its scope-snapshot. That mirrors how Serilog's
/// <c>Microsoft.Extensions.Logging</c> bridge projects the scope dictionary onto
/// every <see cref="Serilog.Events.LogEvent"/> property bag.
/// </remarks>
public sealed class TeamsMessengerConnectorLogScopeTests
{
    private const string AppId = "11111111-1111-1111-1111-111111111111";
    private const string QuestionId = "q-card-scope-001";
    private const string ActivityId = "act-scope-001";
    private const string ConversationId = "19:conversation-scope";

    [Fact]
    public async Task DeleteCardAsync_OpensLogScopeWithCorrelationIdEqualToQuestionId()
    {
        // Drive the happy-path Delete flow with a recording logger; assert that the
        // public DeleteCardAsync entry point opened a TeamsLogScope so that every
        // log emitted inside (including the inner ContinueConversationAsync 404
        // information log AND the inline-retry transient-failure Warning) carries
        // the canonical CorrelationId enrichment.
        var logger = new ScopeCapturingLogger<TeamsMessengerConnector>();
        var harness = BuildHarness(logger);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();

        await ((ITeamsCardManager)harness.Connector).DeleteCardAsync(QuestionId, CancellationToken.None);

        AssertExactlyOneCorrelationIdScopeWasOpened(logger);
    }

    [Fact]
    public async Task UpdateCardAsync_OpensLogScopeWithCorrelationIdEqualToQuestionId()
    {
        // Same contract as DeleteCardAsync above — UpdateCardCoreAsync (the private
        // entry point for both UpdateCardAsync overloads) must open the scope so the
        // inline-retry transient-failure Warning at TeamsMessengerConnector.cs:~885
        // carries the canonical CorrelationId enrichment.
        var logger = new ScopeCapturingLogger<TeamsMessengerConnector>();
        var harness = BuildHarness(logger);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();

        await ((ITeamsCardManager)harness.Connector).UpdateCardAsync(
            QuestionId,
            CardUpdateAction.MarkAnswered,
            CancellationToken.None);

        AssertExactlyOneCorrelationIdScopeWasOpened(logger);
    }

    [Fact]
    public async Task DeleteCardAsync_LogScopeIsActiveAroundInnerLogEmission_OnStale404()
    {
        // End-to-end assertion: when the inner DeleteActivityAsync throws a 404
        // ("ActivityNotFound"), the connector emits an Information log "treating as
        // already-deleted" (TeamsMessengerConnector.cs line ~770). That log entry
        // MUST land while the TeamsLogScope is still active, otherwise the §6.3 step
        // 5 "every log entry" contract is broken. The ScopeCapturingLogger snapshots
        // the active-scope stack at the moment the entry is written and we assert
        // the CorrelationId is present in that snapshot.
        var adapter = new StaleActivity404CloudAdapter();
        var logger = new ScopeCapturingLogger<TeamsMessengerConnector>();
        var harness = BuildHarness(logger, adapter);
        harness.CardStateStore.Preload[QuestionId] = BuildCardState();

        await ((ITeamsCardManager)harness.Connector).DeleteCardAsync(QuestionId, CancellationToken.None);

        var entry = Assert.Single(logger.LogEntries, e =>
            e.Message.Contains("treating as already-deleted", StringComparison.Ordinal));
        AssertScopeContainsCorrelationId(entry, QuestionId);
    }

    private static void AssertExactlyOneCorrelationIdScopeWasOpened(ScopeCapturingLogger<TeamsMessengerConnector> logger)
    {
        var scope = Assert.Single(logger.ScopeStates);
        var dict = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(scope);
        Assert.True(
            dict.TryGetValue(TeamsLogScope.CorrelationIdKey, out var corr),
            $"Expected scope to contain '{TeamsLogScope.CorrelationIdKey}'.");
        Assert.Equal(QuestionId, corr);
    }

    private static void AssertScopeContainsCorrelationId(CapturedLogEntry entry, string expectedCorrelationId)
    {
        Assert.NotEmpty(entry.ActiveScopesSnapshot);
        var found = entry.ActiveScopesSnapshot
            .OfType<IReadOnlyDictionary<string, object?>>()
            .Any(d => d.TryGetValue(TeamsLogScope.CorrelationIdKey, out var c)
                && (string?)c == expectedCorrelationId);
        Assert.True(
            found,
            $"Expected an active scope with {TeamsLogScope.CorrelationIdKey} = '{expectedCorrelationId}' " +
            $"when log entry '{entry.Message}' was emitted; active scope count = {entry.ActiveScopesSnapshot.Count}.");
    }

    private static CardManagerHarness BuildHarness(
        ScopeCapturingLogger<TeamsMessengerConnector> logger,
        RecordingCloudAdapter? adapter = null)
    {
        adapter ??= new RecordingCloudAdapter();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };
        var convStore = new ConnectorRecordingConversationReferenceStore();
        var router = new RecordingConversationReferenceRouter();
        var qStore = new RecordingAgentQuestionStore();
        var cardStore = new RecordingCardStateStore();
        var renderer = new AdaptiveCardBuilder();
        IInboundEventReader reader = new ChannelInboundEventPublisher();
        var connector = new TeamsMessengerConnector(
            adapter,
            options,
            convStore,
            router,
            qStore,
            cardStore,
            renderer,
            reader,
            logger);
        return new CardManagerHarness
        {
            Connector = connector,
            Adapter = adapter,
            CardStateStore = cardStore,
            Options = options,
        };
    }

    private static TeamsCardState BuildCardState(string status = TeamsCardStatuses.Pending)
    {
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.example.test",
            Conversation = new ConversationAccount(id: ConversationId, tenantId: "tenant-a"),
            Bot = new ChannelAccount(id: $"28:{AppId}"),
            User = new ChannelAccount(id: "29:user-dave", aadObjectId: "aad-dave", name: "Dave Test"),
        };

        return new TeamsCardState
        {
            QuestionId = QuestionId,
            ActivityId = ActivityId,
            ConversationId = ConversationId,
            ConversationReferenceJson = JsonConvert.SerializeObject(reference),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
    }

    private sealed class CardManagerHarness
    {
        public required TeamsMessengerConnector Connector { get; init; }
        public required RecordingCloudAdapter Adapter { get; init; }
        public required RecordingCardStateStore CardStateStore { get; init; }
        public required TeamsMessagingOptions Options { get; init; }
    }

    /// <summary>
    /// Recording <see cref="ILogger{T}"/> that captures every <see cref="ILogger.BeginScope{TState}"/>
    /// state AND every <see cref="ILogger.Log{TState}"/> entry along with a snapshot
    /// of the active scope stack at the moment the entry was written. Used to assert
    /// that the §6.3 enrichment scope was open when downstream code emitted a log.
    /// </summary>
    private sealed class ScopeCapturingLogger<T> : ILogger<T>
    {
        private readonly AsyncLocal<Stack<object?>> _activeScopes = new();

        public List<object?> ScopeStates { get; } = new();
        public List<CapturedLogEntry> LogEntries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            ScopeStates.Add(state);
            var stack = _activeScopes.Value ??= new Stack<object?>();
            stack.Push(state);
            return new Pop(stack);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var stack = _activeScopes.Value;
            var snapshot = stack is null
                ? new List<object?>()
                : stack.ToList();
            LogEntries.Add(new CapturedLogEntry(
                Level: logLevel,
                Message: formatter(state, exception),
                Exception: exception,
                ActiveScopesSnapshot: snapshot));
        }

        private sealed class Pop : IDisposable
        {
            private readonly Stack<object?> _stack;
            private bool _disposed;
            public Pop(Stack<object?> stack) => _stack = stack;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_stack.Count > 0) _stack.Pop();
            }
        }
    }

    private sealed record CapturedLogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyList<object?> ActiveScopesSnapshot);

    /// <summary>
    /// Specialized adapter whose <c>ContinueConversationAsync</c> invokes the callback
    /// with a turn context that throws an <c>ErrorResponseException</c> with HTTP 404
    /// on <c>DeleteActivityAsync</c>, exercising the "stale activity 404 → treat as
    /// already-deleted" branch in <see cref="TeamsMessengerConnector.DeleteCardAsync"/>.
    /// </summary>
    private sealed class StaleActivity404CloudAdapter : RecordingCloudAdapter
    {
        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            Microsoft.Bot.Builder.BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add((botAppId, reference));
            var turnContext = new Stale404TurnContext(reference);
            return callback(turnContext, cancellationToken);
        }
    }

    private sealed class Stale404TurnContext : Microsoft.Bot.Builder.ITurnContext
    {
        private readonly ConversationReference _reference;

        public Stale404TurnContext(ConversationReference reference) => _reference = reference;

        public Microsoft.Bot.Builder.BotAdapter Adapter => null!;

        public Microsoft.Bot.Builder.TurnContextStateCollection TurnState { get; } = new();

        public Activity Activity => new Activity
        {
            ChannelId = _reference.ChannelId,
            From = _reference.User,
            Conversation = _reference.Conversation,
            ServiceUrl = _reference.ServiceUrl,
        };

        public bool Responded => true;

        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
            => throw NewNotFound();

        public Task DeleteActivityAsync(ConversationReference reference, CancellationToken cancellationToken = default)
            => throw NewNotFound();

        public Task<Microsoft.Bot.Schema.ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Microsoft.Bot.Schema.ResourceResponse>());

        public Task<Microsoft.Bot.Schema.ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string? inputHint = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Bot.Schema.ResourceResponse());

        public Task<Microsoft.Bot.Schema.ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Bot.Schema.ResourceResponse());

        public Task<Microsoft.Bot.Schema.ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => Task.FromResult(new Microsoft.Bot.Schema.ResourceResponse());

        public Microsoft.Bot.Builder.ITurnContext OnSendActivities(Microsoft.Bot.Builder.SendActivitiesHandler handler) => this;

        public Microsoft.Bot.Builder.ITurnContext OnUpdateActivity(Microsoft.Bot.Builder.UpdateActivityHandler handler) => this;

        public Microsoft.Bot.Builder.ITurnContext OnDeleteActivity(Microsoft.Bot.Builder.DeleteActivityHandler handler) => this;

        private static ErrorResponseException NewNotFound()
        {
            var http = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                ReasonPhrase = "Not Found",
            };
            return new ErrorResponseException("ActivityNotFound", null)
            {
                Response = new Microsoft.Rest.HttpResponseMessageWrapper(http, string.Empty),
            };
        }
    }
}
