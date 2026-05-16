using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Swarm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.7 — locks the four scenarios on the implementation plan for
/// <see cref="SwarmEventSubscriptionService"/>:
/// <list type="number">
///   <item>Question event routed to <see cref="IMessengerConnector.SendQuestionAsync"/>
///   with the originating <see cref="AgentQuestionEnvelope"/>.</item>
///   <item>Alert event with a <c>TaskOversight</c> record routes to the
///   designated operator's chat.</item>
///   <item>Alert event without a <c>TaskOversight</c> record falls back
///   to the workspace's first active operator
///   (<see cref="IOperatorRegistry.GetByWorkspaceAsync"/>) per
///   architecture.md §5.6.</item>
///   <item>Subscription error → warning log + exponential backoff +
///   reconnection.</item>
/// </list>
///
/// Plus AC coverage:
///   - Status update broadcast when stub <c>TaskOversight</c> returns null.
///   - Stub registrations sit behind <c>TryAddSingleton</c> so the Stage
///     3.x / 6.3 production replacements supersede them.
/// </summary>
public class SwarmEventSubscriptionServiceTests
{
    // ============================================================
    // Scenario 1 — Question event routed to Telegram (SendQuestionAsync)
    // ============================================================

    [Fact]
    public async Task QuestionEvent_RoutedToSendQuestionAsync_WithEnvelope()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: 1001L);
        var oversight = new FakeTaskOversightRepository();
        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        var envelope = BuildEnvelope(
            questionId: "Q-1",
            agentId: "build-agent-1",
            chatIdMetadata: "1001");
        var ev = new AgentQuestionEvent
        {
            CorrelationId = "trace-q-1",
            Envelope = envelope,
        };
        bus.Publish("t-1", ev);

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Questions.Should().ContainSingle();
        var sent = connector.Questions[0];
        sent.Question.QuestionId.Should().Be("Q-1");
        sent.Question.AgentId.Should().Be("build-agent-1");
        sent.RoutingMetadata[TelegramMessengerConnector.TelegramChatIdMetadataKey].Should().Be("1001");
    }

    [Fact]
    public async Task QuestionEvent_WithoutRoutingMetadata_FallsBackToFirstTenantBinding()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        var fallbackChat = 4242L;
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: fallbackChat);
        var oversight = new FakeTaskOversightRepository();
        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        // No TelegramChatId in RoutingMetadata.
        var envelope = BuildEnvelope(questionId: "Q-2", agentId: "agent-2", chatIdMetadata: null);
        bus.Publish("t-1", new AgentQuestionEvent
        {
            CorrelationId = "trace-q-2",
            Envelope = envelope,
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Questions.Should().ContainSingle();
        connector.Questions[0].RoutingMetadata[TelegramMessengerConnector.TelegramChatIdMetadataKey]
            .Should().Be(fallbackChat.ToString(CultureInfo.InvariantCulture));
    }

    // ============================================================
    // Scenario 2 — Alert event routed via TaskOversight
    // ============================================================

    [Fact]
    public async Task AlertEvent_WithTaskOversight_RoutesToOversightOperator()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        var op1ChatId = 1111L;
        var op2ChatId = 2222L;
        var op1 = registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: op1ChatId, alias: "@op-1");
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: op2ChatId, alias: "@op-2");

        var oversight = new FakeTaskOversightRepository();
        oversight.Set("TASK-099", new TaskOversight
        {
            TaskId = "TASK-099",
            OperatorBindingId = op1.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "@op-1",
            CorrelationId = "trace-alert-1",
        });

        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        bus.Publish("t-1", new AgentAlertEvent
        {
            CorrelationId = "trace-alert-1",
            AlertId = "ALERT-001",
            AgentId = "monitor-1",
            TaskId = "TASK-099",
            Title = "Build failure",
            Body = "Compile error on main",
            Severity = MessageSeverity.Critical,
            WorkspaceId = "w-1",
            TenantId = "t-1",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Messages.Should().ContainSingle();
        var sent = connector.Messages[0];
        sent.Severity.Should().Be(MessageSeverity.Critical);
        sent.Text.Should().Contain("Build failure");
        sent.Text.Should().Contain("Compile error on main");
        sent.Metadata[TelegramMessengerConnector.TelegramChatIdMetadataKey]
            .Should().Be(op1ChatId.ToString(CultureInfo.InvariantCulture));
        sent.Metadata[TelegramMessengerConnector.SourceTypeMetadataKey]
            .Should().Be(nameof(OutboundSourceType.Alert));
        sent.Metadata[TelegramMessengerConnector.AlertIdMetadataKey].Should().Be("ALERT-001");
        sent.CorrelationId.Should().Be("trace-alert-1");
        sent.TaskId.Should().Be("TASK-099");
    }

    // ============================================================
    // Scenario 3 — Alert event falls back to workspace default operator
    // ============================================================

    [Fact]
    public async Task AlertEvent_WithoutTaskOversight_FallsBackToFirstActiveWorkspaceBinding()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        var firstChat = 3001L;
        var secondChat = 3002L;
        registry.AddBinding(tenantId: "t-1", workspaceId: "factory-1", chatId: firstChat, alias: "@first");
        registry.AddBinding(tenantId: "t-1", workspaceId: "factory-1", chatId: secondChat, alias: "@second");

        var oversight = new FakeTaskOversightRepository(); // empty — TASK-100 has no row

        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        bus.Publish("t-1", new AgentAlertEvent
        {
            CorrelationId = "trace-alert-fallback",
            AlertId = "ALERT-002",
            AgentId = "monitor-2",
            TaskId = "TASK-100",
            Title = "Disk usage",
            Body = "Disk over 90 percent",
            Severity = MessageSeverity.High,
            WorkspaceId = "factory-1",
            TenantId = "t-1",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Messages.Should().ContainSingle();
        connector.Messages[0].Metadata[TelegramMessengerConnector.TelegramChatIdMetadataKey]
            .Should().Be(firstChat.ToString(CultureInfo.InvariantCulture));
        connector.Messages[0].Severity.Should().Be(MessageSeverity.High);
    }

    [Fact]
    public async Task AlertEvent_WhenWorkspaceHasNoActiveBindings_IsDropped()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" }); // no bindings
        var oversight = new FakeTaskOversightRepository();
        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        bus.Publish("t-1", new AgentAlertEvent
        {
            CorrelationId = "trace-alert-drop",
            AlertId = "ALERT-X",
            AgentId = "agent-x",
            TaskId = "TASK-X",
            Title = "Nobody home",
            Body = "drop me",
            Severity = MessageSeverity.High,
            WorkspaceId = "empty-ws",
            TenantId = "t-1",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Messages.Should().BeEmpty();
    }

    // ============================================================
    // Scenario for status updates — broadcast when stub returns null
    // ============================================================

    [Fact]
    public async Task StatusUpdate_WithStubOversight_BroadcastsToActiveBindings()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        var chat1 = 5001L;
        var chat2 = 5002L;
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: chat1, alias: "@a");
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-2", chatId: chat2, alias: "@b");

        var oversight = new FakeTaskOversightRepository(); // returns null
        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        bus.Publish("t-1", new AgentStatusUpdateEvent
        {
            CorrelationId = "trace-status",
            AgentId = "build-agent-7",
            TaskId = "TASK-500",
            StatusText = "Compilation 50% complete",
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Messages.Should().HaveCount(2);
        connector.Messages.Should().AllSatisfy(m => m.Severity.Should().Be(MessageSeverity.Normal));
        var chatIds = connector.Messages
            .Select(m => long.Parse(
                m.Metadata[TelegramMessengerConnector.TelegramChatIdMetadataKey],
                CultureInfo.InvariantCulture))
            .ToHashSet();
        chatIds.Should().BeEquivalentTo(new[] { chat1, chat2 });
        connector.Messages.Should().AllSatisfy(m => m.Text.Should().Contain("50%"));
    }

    [Fact]
    public async Task StatusUpdate_WithConcreteOversight_RoutesToBoundOperatorOnly()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        var chat1 = 6001L;
        var chat2 = 6002L;
        var op1 = registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: chat1, alias: "@o1");
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-2", chatId: chat2, alias: "@o2");

        var oversight = new FakeTaskOversightRepository();
        oversight.Set("TASK-600", new TaskOversight
        {
            TaskId = "TASK-600",
            OperatorBindingId = op1.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "@o1",
            CorrelationId = "trace-status-routed",
        });

        var connector = new RecordingMessengerConnector();
        var service = CreateService(bus, registry, oversight, connector);

        bus.Publish("t-1", new AgentStatusUpdateEvent
        {
            CorrelationId = "trace-status-routed",
            AgentId = "agent-r",
            TaskId = "TASK-600",
            StatusText = "started",
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1");

        connector.Messages.Should().ContainSingle();
        connector.Messages[0].Metadata[TelegramMessengerConnector.TelegramChatIdMetadataKey]
            .Should().Be(chat1.ToString(CultureInfo.InvariantCulture));
    }

    // ============================================================
    // Scenario 4 — Subscription reconnects on error
    // ============================================================

    [Fact]
    public async Task SubscriptionError_LogsWarning_AndReconnects()
    {
        var bus = new FakeSwarmCommandBus(new[] { "t-1" });
        var registry = new FakeOperatorRegistry(activeTenants: new[] { "t-1" });
        registry.AddBinding(tenantId: "t-1", workspaceId: "w-1", chatId: 7777L);
        var oversight = new FakeTaskOversightRepository();
        var connector = new RecordingMessengerConnector();
        var logger = new ListLogger<SwarmEventSubscriptionService>();
        var service = CreateService(bus, registry, oversight, connector, logger);

        // First subscribe call throws — service should log warning and
        // back off, then re-subscribe successfully on iteration 2.
        bus.ThrowOnNextSubscribe(new InvalidOperationException("simulated transient failure"));

        bus.Publish("t-1", new AgentStatusUpdateEvent
        {
            CorrelationId = "trace-reconnect",
            AgentId = "a",
            TaskId = "T",
            StatusText = "after-reconnect",
        });

        await bus.WaitForConsumptionAsync("t-1");
        await RunAndCancelAsync(service, "t-1", warmupMs: 200);

        connector.Messages.Should().NotBeEmpty(
            "the second subscribe iteration must successfully drain the published status event");

        logger.Entries.Should().Contain(
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                 e.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase),
            "the disconnect must surface as a warning log entry");
        logger.Entries.Should().Contain(
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information &&
                 e.Message.Contains("reconnected", StringComparison.OrdinalIgnoreCase),
            "the recovery must surface as an information log entry");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static SwarmEventSubscriptionService CreateService(
        FakeSwarmCommandBus bus,
        FakeOperatorRegistry registry,
        FakeTaskOversightRepository oversight,
        RecordingMessengerConnector connector,
        Microsoft.Extensions.Logging.ILogger<SwarmEventSubscriptionService>? logger = null)
    {
        return new SwarmEventSubscriptionService(
            bus,
            registry,
            oversight,
            connector,
            logger ?? NullLogger<SwarmEventSubscriptionService>.Instance)
        {
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
        };
    }

    private static async Task RunAndCancelAsync(
        SwarmEventSubscriptionService service,
        string tenantId,
        int warmupMs = 100)
    {
        using var cts = new CancellationTokenSource();
        var loop = service.RunTenantLoopAsync(tenantId, cts.Token);
        await Task.Delay(warmupMs);
        cts.Cancel();
        await loop;
    }

    private static AgentQuestionEnvelope BuildEnvelope(
        string questionId,
        string agentId,
        string? chatIdMetadata)
    {
        var question = new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = agentId,
            TaskId = "TASK-Q",
            Title = "Should we proceed?",
            Body = "Long context here",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "yes" },
                new HumanAction { ActionId = "reject", Label = "Reject", Value = "no" },
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            CorrelationId = "trace-" + questionId,
        };

        var routing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (chatIdMetadata is not null)
        {
            routing[TelegramMessengerConnector.TelegramChatIdMetadataKey] = chatIdMetadata;
        }

        return new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = "approve",
            RoutingMetadata = routing,
        };
    }

    // ------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------

    private sealed class FakeSwarmCommandBus : ISwarmCommandBus
    {
        private readonly Dictionary<string, Channel<SwarmEvent>> _channels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _publishedCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _consumedCounts = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private Exception? _pendingThrow;

        public FakeSwarmCommandBus(IEnumerable<string> tenants)
        {
            foreach (var t in tenants)
            {
                _channels[t] = Channel.CreateUnbounded<SwarmEvent>(new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                });
                _publishedCounts[t] = 0;
                _consumedCounts[t] = 0;
            }
        }

        public void ThrowOnNextSubscribe(Exception ex)
        {
            lock (_lock) { _pendingThrow = ex; }
        }

        public void Publish(string tenantId, SwarmEvent ev)
        {
            var channel = _channels[tenantId];
            channel.Writer.TryWrite(ev);
            lock (_lock) { _publishedCounts[tenantId]++; }
        }

        public async Task WaitForConsumptionAsync(string tenantId, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_consumedCounts[tenantId] >= _publishedCounts[tenantId])
                    {
                        return;
                    }
                }
                await Task.Delay(10);
            }
        }

        public Task PublishCommandAsync(SwarmCommand command, CancellationToken ct) => Task.CompletedTask;
        public Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct) => Task.CompletedTask;
        public Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct)
            => Task.FromResult(new SwarmStatusSummary { WorkspaceId = query.WorkspaceId, State = "test" });
        public Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentInfo>>(Array.Empty<AgentInfo>());

        public async IAsyncEnumerable<SwarmEvent> SubscribeAsync(
            string tenantId,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Exception? toThrow;
            lock (_lock)
            {
                toThrow = _pendingThrow;
                _pendingThrow = null;
            }
            if (toThrow is not null)
            {
                throw toThrow;
            }

            var channel = _channels[tenantId];
            await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return ev;
                lock (_lock) { _consumedCounts[tenantId]++; }
            }
        }
    }

    private sealed class FakeOperatorRegistry : IOperatorRegistry
    {
        private readonly List<OperatorBinding> _bindings = new();
        private readonly IReadOnlyList<string> _activeTenants;

        public FakeOperatorRegistry(IEnumerable<string> activeTenants)
        {
            _activeTenants = activeTenants.ToList();
        }

        public OperatorBinding AddBinding(
            string tenantId,
            string workspaceId,
            long chatId,
            long userId = 0,
            string alias = "@op")
        {
            var actualUser = userId == 0 ? chatId + 100 : userId;
            var binding = new OperatorBinding
            {
                Id = StubOperatorRegistry.DeriveBindingId(actualUser, chatId, tenantId, workspaceId),
                TelegramUserId = actualUser,
                TelegramChatId = chatId,
                ChatType = ChatType.Private,
                OperatorAlias = alias,
                TenantId = tenantId,
                WorkspaceId = workspaceId,
                Roles = Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
                IsActive = true,
            };
            _bindings.Add(binding);
            return binding;
        }

        public Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(long telegramUserId, long chatId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OperatorBinding>>(
                _bindings.Where(b => b.TelegramUserId == telegramUserId && b.TelegramChatId == chatId).ToList());

        public Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(long telegramUserId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OperatorBinding>>(
                _bindings.Where(b => b.TelegramUserId == telegramUserId).ToList());

        public Task<OperatorBinding?> GetByAliasAsync(string operatorAlias, string tenantId, CancellationToken ct)
            => Task.FromResult(_bindings.FirstOrDefault(b =>
                b.OperatorAlias == operatorAlias && b.TenantId == tenantId));

        public Task<IReadOnlyList<OperatorBinding>> GetByWorkspaceAsync(string workspaceId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OperatorBinding>>(
                _bindings.Where(b => b.WorkspaceId == workspaceId).ToList());

        public Task RegisterAsync(OperatorRegistration registration, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct)
            => Task.FromResult(_bindings.Any(b => b.TelegramUserId == telegramUserId && b.TelegramChatId == chatId));

        public Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct)
            => Task.FromResult(_activeTenants);

        public Task<IReadOnlyList<OperatorBinding>> GetByTenantAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OperatorBinding>>(
                _bindings.Where(b => b.TenantId == tenantId).ToList());
    }

    private sealed class FakeTaskOversightRepository : ITaskOversightRepository
    {
        private readonly Dictionary<string, TaskOversight> _map = new(StringComparer.Ordinal);

        public void Set(string taskId, TaskOversight oversight) => _map[taskId] = oversight;

        public Task<TaskOversight?> GetByTaskIdAsync(string taskId, CancellationToken ct)
        {
            _map.TryGetValue(taskId, out var value);
            return Task.FromResult<TaskOversight?>(value);
        }

        public Task UpsertAsync(TaskOversight oversight, CancellationToken ct)
        {
            _map[oversight.TaskId] = oversight;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaskOversight>> GetByOperatorAsync(Guid operatorBindingId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TaskOversight>>(
                _map.Values.Where(o => o.OperatorBindingId == operatorBindingId).ToList());
    }

    private sealed class RecordingMessengerConnector : IMessengerConnector
    {
        public List<MessengerMessage> Messages { get; } = new();
        public List<AgentQuestionEnvelope> Questions { get; } = new();

        public Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
        {
            lock (Messages) { Messages.Add(message); }
            return Task.CompletedTask;
        }

        public Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct)
        {
            lock (Questions) { Questions.Add(envelope); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MessengerEvent>>(Array.Empty<MessengerEvent>());
    }

    private sealed record LogEntry(Microsoft.Extensions.Logging.LogLevel Level, string Message);

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        IDisposable Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) { Entries.Add(new LogEntry(logLevel, formatter(state, exception))); }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
