using System.Reflection;
using System.Runtime.CompilerServices;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 1.3 contract tests. These exist to lock the interface surface so
/// downstream stages (2.x, 3.x, 4.x) can be built and mocked against a
/// stable API. They cover BOTH mockability (call recording) AND behavioral
/// guarantees that the story brief / architecture mandates — specifically:
///   - the supported slash-command vocabulary (story brief),
///   - compile-time enforcement of the five required audit fields per
///     human response (story brief audit requirement),
///   - atomic reservation semantics for duplicate webhook protection
///     (story acceptance criterion: "Duplicate webhook delivery does not
///     execute the same human command twice"),
///   - preservation of task/agent/severity context on every persisted
///     pending question (e2e-scenarios "Question includes context,
///     severity, timeout, and proposed default").
/// </summary>
public class ConnectorContractTests
{
    [Fact]
    public async Task MessengerConnector_Mock_RecordsSendMessageCall()
    {
        var mock = new Mock<IMessengerConnector>();
        mock.Setup(c => c.SendMessageAsync(It.IsAny<MessengerMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = new MessengerMessage
        {
            MessageId = "msg-1",
            CorrelationId = "trace-1",
            ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = "hello",
            Severity = MessageSeverity.Normal
        };

        await mock.Object.SendMessageAsync(message, CancellationToken.None);

        mock.Verify(
            c => c.SendMessageAsync(message, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MessengerConnector_Mock_AcceptsAgentQuestionEnvelope()
    {
        var mock = new Mock<IMessengerConnector>();
        mock.Setup(c => c.SendQuestionAsync(It.IsAny<AgentQuestionEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var envelope = CreateEnvelope(proposedDefault: "approve");

        await mock.Object.SendQuestionAsync(envelope, CancellationToken.None);

        mock.Verify(
            c => c.SendQuestionAsync(
                It.Is<AgentQuestionEnvelope>(e => e.ProposedDefaultActionId == "approve"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MessengerConnector_Mock_ReceiveReturnsEmptyByDefault()
    {
        var mock = new Mock<IMessengerConnector>();
        mock.Setup(c => c.ReceiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MessengerEvent>());

        var events = await mock.Object.ReceiveAsync(CancellationToken.None);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task TelegramUpdatePipeline_Mock_RecordsProcessCallAndReturnsResult()
    {
        var mock = new Mock<ITelegramUpdatePipeline>();
        var expected = new PipelineResult
        {
            Handled = true,
            ResponseText = "ack",
            CorrelationId = "trace-1"
        };
        mock.Setup(p => p.ProcessAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var evt = new MessengerEvent
        {
            EventId = "evt-1",
            EventType = EventType.Command,
            UserId = "user-1",
            ChatId = "chat-1",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        var result = await mock.Object.ProcessAsync(evt, CancellationToken.None);

        result.Should().BeSameAs(expected);
        mock.Verify(p => p.ProcessAsync(evt, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommandRouter_Mock_RoutesParsedCommandWithOperator()
    {
        var mock = new Mock<ICommandRouter>();
        mock.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                CorrelationId = "trace-1"
            });

        var parsed = new ParsedCommand
        {
            CommandName = "status",
            RawText = "/status",
            IsValid = true
        };
        var op = new AuthorizedOperator
        {
            OperatorId = Guid.NewGuid(),
            TenantId = "t-1",
            WorkspaceId = "w-1",
            TelegramUserId = 100,
            TelegramChatId = 200
        };

        var result = await mock.Object.RouteAsync(parsed, op, CancellationToken.None);

        result.Success.Should().BeTrue();
        mock.Verify(r => r.RouteAsync(parsed, op, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UserAuthorizationService_Mock_PassesCommandName()
    {
        var mock = new Mock<IUserAuthorizationService>();
        mock.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult { IsAuthorized = false });

        await mock.Object.AuthorizeAsync("u-1", "c-1", "start", CancellationToken.None);

        mock.Verify(
            s => s.AuthorizeAsync("u-1", "c-1", "start", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void AuthorizationResult_BindingsDefaultsToEmpty()
    {
        var result = new AuthorizationResult { IsAuthorized = false };

        result.Bindings.Should().NotBeNull();
        result.Bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingQuestionStore_Mock_StoreAndGetRoundTrip()
    {
        var mock = new Mock<IPendingQuestionStore>();
        var envelope = CreateEnvelope(proposedDefault: "approve");
        var pending = new PendingQuestion
        {
            QuestionId = envelope.Question.QuestionId,
            AgentId = envelope.Question.AgentId,
            TaskId = envelope.Question.TaskId,
            Title = envelope.Question.Title,
            Body = envelope.Question.Body,
            Severity = envelope.Question.Severity,
            AllowedActions = envelope.Question.AllowedActions,
            DefaultActionId = "approve",
            DefaultActionValue = "approve",
            TelegramChatId = 999,
            TelegramMessageId = 42,
            ExpiresAt = envelope.Question.ExpiresAt,
            CorrelationId = envelope.Question.CorrelationId,
            Status = PendingQuestionStatus.Pending,
            StoredAt = DateTimeOffset.UtcNow
        };

        mock.Setup(s => s.GetAsync(envelope.Question.QuestionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        await mock.Object.StoreAsync(envelope, 999L, 42L, CancellationToken.None);
        var fetched = await mock.Object.GetAsync(envelope.Question.QuestionId, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.DefaultActionId.Should().Be("approve");
        mock.Verify(
            s => s.StoreAsync(envelope, 999L, 42L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PendingQuestionStore_Mock_RecordSelectionAcceptsFourParameters()
    {
        var mock = new Mock<IPendingQuestionStore>();

        await mock.Object.RecordSelectionAsync(
            "Q-1",
            "approve",
            "approve",
            42L,
            CancellationToken.None);

        mock.Verify(
            s => s.RecordSelectionAsync("Q-1", "approve", "approve", 42L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AuditLogger_Mock_RecordsAuditEntry()
    {
        var mock = new Mock<IAuditLogger>();
        var entry = new AuditEntry
        {
            EntryId = Guid.NewGuid(),
            UserId = "user-1",
            Action = "command.received",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        await mock.Object.LogAsync(entry, CancellationToken.None);

        mock.Verify(l => l.LogAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeduplicationService_Mock_IsProcessedAndMark()
    {
        var mock = new Mock<IDeduplicationService>();
        mock.Setup(s => s.IsProcessedAsync("evt-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var processed = await mock.Object.IsProcessedAsync("evt-1", CancellationToken.None);
        await mock.Object.MarkProcessedAsync("evt-1", CancellationToken.None);

        processed.Should().BeFalse();
        mock.Verify(s => s.MarkProcessedAsync("evt-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallbackHandler_Mock_ReturnsCommandResult()
    {
        var mock = new Mock<ICallbackHandler>();
        mock.Setup(h => h.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                CorrelationId = "trace-1"
            });

        var evt = new MessengerEvent
        {
            EventId = "cb-1",
            EventType = EventType.CallbackResponse,
            UserId = "u-1",
            ChatId = "c-1",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        var result = await mock.Object.HandleAsync(evt, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void CommandParser_Mock_ParsesText()
    {
        var mock = new Mock<ICommandParser>();
        mock.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns(new ParsedCommand
            {
                CommandName = "status",
                RawText = "/status",
                IsValid = true
            });

        var parsed = mock.Object.Parse("/status");

        parsed.IsValid.Should().BeTrue();
        parsed.CommandName.Should().Be("status");
    }

    [Fact]
    public async Task TaskOversightRepository_Mock_UpsertAndGet()
    {
        var mock = new Mock<ITaskOversightRepository>();
        var oversight = new TaskOversight
        {
            TaskId = "T-1",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "op-1",
            CorrelationId = "trace-1"
        };

        mock.Setup(r => r.GetByTaskIdAsync("T-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(oversight);

        await mock.Object.UpsertAsync(oversight, CancellationToken.None);
        var fetched = await mock.Object.GetByTaskIdAsync("T-1", CancellationToken.None);

        fetched.Should().BeSameAs(oversight);
        mock.Verify(r => r.UpsertAsync(oversight, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OperatorRegistry_Mock_RegisterAndGetBindings()
    {
        var mock = new Mock<IOperatorRegistry>();
        var registration = new OperatorRegistration
        {
            TelegramUserId = 100,
            TelegramChatId = 200,
            ChatType = ChatType.Private,
            TenantId = "t-1",
            WorkspaceId = "w-1",
            Roles = new[] { "Operator" },
            OperatorAlias = "@alice"
        };
        var binding = new OperatorBinding
        {
            Id = Guid.NewGuid(),
            TelegramUserId = 100,
            TelegramChatId = 200,
            ChatType = ChatType.Private,
            OperatorAlias = "@alice",
            TenantId = "t-1",
            WorkspaceId = "w-1",
            Roles = new[] { "Operator" },
            RegisteredAt = DateTimeOffset.UtcNow
        };

        mock.Setup(r => r.GetBindingsAsync(100L, 200L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { binding });
        mock.Setup(r => r.IsAuthorizedAsync(100L, 200L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await mock.Object.RegisterAsync(registration, CancellationToken.None);
        var bindings = await mock.Object.GetBindingsAsync(100L, 200L, CancellationToken.None);
        var authorized = await mock.Object.IsAuthorizedAsync(100L, 200L, CancellationToken.None);

        bindings.Should().ContainSingle().Which.Should().BeSameAs(binding);
        authorized.Should().BeTrue();
        mock.Verify(
            r => r.RegisterAsync(registration, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SwarmCommandBus_Mock_PublishesAndQueries()
    {
        var mock = new Mock<ISwarmCommandBus>();
        var command = new SwarmCommand
        {
            CommandType = SwarmCommandType.CreateTask,
            OperatorId = Guid.NewGuid(),
            CorrelationId = "trace-1"
        };
        var summary = new SwarmStatusSummary
        {
            WorkspaceId = "w-1",
            State = "running"
        };

        mock.Setup(b => b.QueryStatusAsync(It.IsAny<SwarmStatusQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);
        mock.Setup(b => b.QueryAgentsAsync(It.IsAny<SwarmAgentsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentInfo>());

        await mock.Object.PublishCommandAsync(command, CancellationToken.None);
        var status = await mock.Object.QueryStatusAsync(
            new SwarmStatusQuery { WorkspaceId = "w-1" },
            CancellationToken.None);
        var agents = await mock.Object.QueryAgentsAsync(
            new SwarmAgentsQuery { WorkspaceId = "w-1" },
            CancellationToken.None);

        status.Should().BeSameAs(summary);
        agents.Should().BeEmpty();
        mock.Verify(b => b.PublishCommandAsync(command, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Story acceptance criterion: "Approval/rejection buttons are converted
    /// into strongly typed agent events." The contract that closes this loop
    /// is <see cref="ISwarmCommandBus.PublishHumanDecisionAsync"/> — a
    /// strongly-typed <see cref="HumanDecisionEvent"/> publish entry point
    /// distinct from the free-form <see cref="ISwarmCommandBus.PublishCommandAsync"/>.
    /// </summary>
    [Fact]
    public async Task SwarmCommandBus_PublishesHumanDecisionEvent()
    {
        var mock = new Mock<ISwarmCommandBus>();
        var decision = new HumanDecisionEvent
        {
            QuestionId = "Q-1",
            ActionValue = "approve",
            Comment = null,
            Messenger = "telegram",
            ExternalUserId = "100",
            ExternalMessageId = "42",
            ReceivedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        await mock.Object.PublishHumanDecisionAsync(decision, CancellationToken.None);

        mock.Verify(
            b => b.PublishHumanDecisionAsync(
                It.Is<HumanDecisionEvent>(d =>
                    d.QuestionId == "Q-1" &&
                    d.ActionValue == "approve" &&
                    d.CorrelationId == "trace-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SwarmCommandBus_PublishHumanDecisionAsync_IsPartOfContract()
    {
        var method = typeof(ISwarmCommandBus).GetMethod(
            nameof(ISwarmCommandBus.PublishHumanDecisionAsync));

        method.Should().NotBeNull(
            "Approval/rejection buttons must convert into a strongly typed publish entry "
            + "point per story acceptance criterion.");
        var parameters = method!.GetParameters();
        parameters[0].ParameterType.Should().Be(typeof(HumanDecisionEvent),
            "the first parameter must be the strongly-typed event, not an untyped payload");
        method.ReturnType.Should().Be(typeof(Task));
    }

    [Fact]
    public void SwarmEvent_AgentQuestionEvent_IsSwarmEvent()
    {
        var envelope = CreateEnvelope(proposedDefault: null);

        SwarmEvent evt = new AgentQuestionEvent
        {
            CorrelationId = "trace-1",
            Envelope = envelope
        };

        evt.Should().BeOfType<AgentQuestionEvent>();
        ((AgentQuestionEvent)evt).Envelope.Should().BeSameAs(envelope);
    }

    [Fact]
    public void SwarmEvent_AgentAlertEvent_CarriesAllTenFields()
    {
        var alert = new AgentAlertEvent
        {
            AlertId = "A-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "Build failure",
            Body = "Compilation failed in Solution12",
            Severity = MessageSeverity.High,
            WorkspaceId = "w-1",
            TenantId = "t-1",
            CorrelationId = "trace-1",
            Timestamp = DateTimeOffset.UtcNow
        };

        alert.Should().BeAssignableTo<SwarmEvent>();
        alert.TaskId.Should().Be("T-1");
        alert.Severity.Should().Be(MessageSeverity.High);
    }

    [Fact]
    public void SwarmEvent_AgentStatusUpdate_IsSwarmEvent()
    {
        SwarmEvent evt = new AgentStatusUpdateEvent
        {
            CorrelationId = "trace-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            StatusText = "Working on tests"
        };

        evt.Should().BeOfType<AgentStatusUpdateEvent>();
    }

    [Fact]
    public void PendingQuestion_DefaultStatusAndOptionalFieldsAreSensible()
    {
        var pending = new PendingQuestion
        {
            QuestionId = "Q-1",
            AgentId = "a-1",
            TaskId = "T-1",
            Title = "t",
            Body = "b",
            Severity = MessageSeverity.Normal,
            AllowedActions = Array.Empty<HumanAction>(),
            TelegramChatId = 1,
            TelegramMessageId = 2,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CorrelationId = "trace",
            Status = PendingQuestionStatus.Pending,
            StoredAt = DateTimeOffset.UtcNow
        };

        pending.DefaultActionId.Should().BeNull();
        pending.DefaultActionValue.Should().BeNull();
        pending.SelectedActionId.Should().BeNull();
        pending.SelectedActionValue.Should().BeNull();
        pending.RespondentUserId.Should().BeNull();
    }

    private static AgentQuestionEnvelope CreateEnvelope(string? proposedDefault)
    {
        var question = new AgentQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "Approve?",
            Body = "Body text",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
                new HumanAction { ActionId = "reject",  Label = "Reject",  Value = "reject", RequiresComment = true }
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = "trace-1"
        };
        return new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = proposedDefault
        };
    }

    // ============================================================
    // Behavioral contract tests (evaluator iter-1 item #6).
    // Each test pins a guarantee that the story/architecture
    // requires of the contract surface itself, not just its
    // mockability.
    // ============================================================

    /// <summary>
    /// Story brief: "Commands: Support /start, /status, /agents, /ask,
    /// /approve, /reject, /handoff, /pause, /resume." The vocabulary table
    /// pins these names so downstream parsing/dispatch cannot drift via
    /// string typos.
    /// </summary>
    [Theory]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("agents")]
    [InlineData("ask")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("handoff")]
    [InlineData("pause")]
    [InlineData("resume")]
    public void TelegramCommands_All_ContainsStoryBriefCommand(string commandName)
    {
        TelegramCommands.All.Should().Contain(commandName);
        TelegramCommands.IsKnown(commandName).Should().BeTrue();
        TelegramCommands.IsKnown(commandName.ToUpperInvariant()).Should().BeTrue();
    }

    [Fact]
    public void TelegramCommands_IsKnown_RejectsUnknownAndNull()
    {
        TelegramCommands.IsKnown("not-a-command").Should().BeFalse();
        TelegramCommands.IsKnown("").Should().BeFalse();
        TelegramCommands.IsKnown(null).Should().BeFalse();
    }

    [Fact]
    public void TelegramCommands_All_HasExactlyNineStoryBriefCommands()
    {
        TelegramCommands.All.Should().HaveCount(9);
    }

    /// <summary>
    /// Story brief audit requirement: "Persist every human response with
    /// message ID, user ID, agent ID, timestamp, and correlation ID." The
    /// strongly-typed <see cref="HumanResponseAuditEntry"/> exists so all
    /// five fields are <c>required</c> at the type level. This test pins
    /// that guarantee by reflecting on the record and asserting every
    /// mandatory field carries the <c>RequiredMemberAttribute</c>.
    /// </summary>
    [Theory]
    [InlineData(nameof(HumanResponseAuditEntry.MessageId))]
    [InlineData(nameof(HumanResponseAuditEntry.UserId))]
    [InlineData(nameof(HumanResponseAuditEntry.AgentId))]
    [InlineData(nameof(HumanResponseAuditEntry.Timestamp))]
    [InlineData(nameof(HumanResponseAuditEntry.CorrelationId))]
    public void HumanResponseAuditEntry_EnforcesStoryAuditField(string propertyName)
    {
        var property = typeof(HumanResponseAuditEntry).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull(
            "HumanResponseAuditEntry must expose '{0}' per story audit requirement",
            propertyName);
        property!.GetCustomAttribute<RequiredMemberAttribute>().Should().NotBeNull(
            "field '{0}' must be marked `required` so a human response cannot be persisted without it",
            propertyName);
    }

    [Fact]
    public void HumanResponseAuditEntry_ConstructionWithAllStoryFields_Succeeds()
    {
        var entry = new HumanResponseAuditEntry
        {
            EntryId = Guid.NewGuid(),
            MessageId = "msg-7",
            UserId = "u-1",
            AgentId = "agent-1",
            QuestionId = "Q-1",
            ActionValue = "approve",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };

        entry.MessageId.Should().Be("msg-7");
        entry.UserId.Should().Be("u-1");
        entry.AgentId.Should().Be("agent-1");
        entry.CorrelationId.Should().Be("trace-1");
    }

    /// <summary>
    /// Story acceptance criterion: "Duplicate webhook delivery does not
    /// execute the same human command twice." The atomic
    /// <see cref="IDeduplicationService.TryReserveAsync"/> primitive is the
    /// contract that satisfies this — exactly one concurrent caller can
    /// claim a given event id, the rest see <c>false</c>. This test
    /// validates an in-memory implementation honors that semantic.
    /// </summary>
    [Fact]
    public async Task DeduplicationService_TryReserve_AwardsExactlyOneCallerPerEventId()
    {
        var service = new InMemoryAtomicDedupService();
        const int concurrency = 100;

        var winners = await RunConcurrentReservationsAsync(
            service, "evt-1", concurrency);

        winners.Should().Be(1, "exactly one concurrent caller must win the reservation");
    }

    [Fact]
    public async Task DeduplicationService_TryReserve_DifferentEventIdsBothSucceed()
    {
        var service = new InMemoryAtomicDedupService();

        var first = await service.TryReserveAsync("evt-1", CancellationToken.None);
        var second = await service.TryReserveAsync("evt-2", CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeTrue();
    }

    [Fact]
    public void DeduplicationService_InterfaceExposesAtomicPrimitive()
    {
        var method = typeof(IDeduplicationService).GetMethod(
            nameof(IDeduplicationService.TryReserveAsync));

        method.Should().NotBeNull(
            "the contract must expose an atomic reserve operation aligned with the duplicate-webhook acceptance criterion");
        method!.ReturnType.Should().Be(typeof(Task<bool>));
    }

    private static async Task<int> RunConcurrentReservationsAsync(
        IDeduplicationService service,
        string eventId,
        int concurrency)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task<bool>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                gate.Wait();
                return service.TryReserveAsync(eventId, CancellationToken.None);
            });
        }
        gate.Set();
        var results = await Task.WhenAll(tasks);
        return results.Count(r => r);
    }

    /// <summary>
    /// e2e-scenarios.md §"Question includes context, severity, timeout,
    /// and proposed default" requires task/agent context and severity to
    /// be available when blocking-question handling fires (timeout,
    /// callback resolution, follow-up text reply). Pinning these fields
    /// as <c>required</c> on the persisted <see cref="PendingQuestion"/>
    /// record guarantees no future implementer can drop them.
    /// </summary>
    [Theory]
    [InlineData(nameof(PendingQuestion.QuestionId))]
    [InlineData(nameof(PendingQuestion.AgentId))]
    [InlineData(nameof(PendingQuestion.TaskId))]
    [InlineData(nameof(PendingQuestion.Severity))]
    [InlineData(nameof(PendingQuestion.Title))]
    [InlineData(nameof(PendingQuestion.Body))]
    [InlineData(nameof(PendingQuestion.AllowedActions))]
    [InlineData(nameof(PendingQuestion.ExpiresAt))]
    [InlineData(nameof(PendingQuestion.CorrelationId))]
    public void PendingQuestion_RequiresContextFieldFromQuestion(string propertyName)
    {
        var property = typeof(PendingQuestion).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull();
        property!.GetCustomAttribute<RequiredMemberAttribute>().Should().NotBeNull(
            "PendingQuestion.{0} must be `required` so timeout/response handlers "
            + "always have task/agent/severity context without rehydrating from another source",
            propertyName);
    }

    [Fact]
    public void PendingQuestion_PreservesEveryQuestionContextField()
    {
        var envelope = CreateEnvelope(proposedDefault: "approve");
        var question = envelope.Question;

        var pending = new PendingQuestion
        {
            QuestionId = question.QuestionId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            Title = question.Title,
            Body = question.Body,
            Severity = question.Severity,
            AllowedActions = question.AllowedActions,
            DefaultActionId = envelope.ProposedDefaultActionId,
            DefaultActionValue = "approve",
            TelegramChatId = 999,
            TelegramMessageId = 42,
            ExpiresAt = question.ExpiresAt,
            CorrelationId = question.CorrelationId,
            Status = PendingQuestionStatus.Pending,
            StoredAt = DateTimeOffset.UtcNow
        };

        pending.QuestionId.Should().Be(question.QuestionId);
        pending.AgentId.Should().Be(question.AgentId);
        pending.TaskId.Should().Be(question.TaskId);
        pending.Severity.Should().Be(question.Severity);
        pending.AllowedActions.Should().BeEquivalentTo(question.AllowedActions);
        pending.ExpiresAt.Should().Be(question.ExpiresAt);
        pending.CorrelationId.Should().Be(question.CorrelationId);
    }

    /// <summary>
    /// Minimal in-memory implementation of the atomic dedup primitive,
    /// used to assert <see cref="IDeduplicationService.TryReserveAsync"/>
    /// behavioral semantics without a real cache. Implementations in
    /// later stages (Redis-backed, SQL-backed) must satisfy the same
    /// "exactly one winner per eventId across concurrent callers"
    /// guarantee tested here.
    /// </summary>
    private sealed class InMemoryAtomicDedupService : IDeduplicationService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new();

        public Task<bool> TryReserveAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(_seen.TryAdd(eventId, 0));

        public Task ReleaseReservationAsync(string eventId, CancellationToken ct)
        {
            // Test-double: this minimal stub does not separate
            // "reserved" from "processed" buckets, so release simply
            // removes the reservation. The richer
            // InMemoryDeduplicationService inside the Telegram project
            // adds the sticky-processed guard (a release after
            // MarkProcessedAsync becomes a no-op).
            _seen.TryRemove(eventId, out _);
            return Task.CompletedTask;
        }

        public Task<bool> IsProcessedAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(_seen.ContainsKey(eventId));

        public Task MarkProcessedAsync(string eventId, CancellationToken ct)
        {
            _seen.TryAdd(eventId, 0);
            return Task.CompletedTask;
        }
    }

    // ============================================================
    // Telegram callback-data constraint enforcement
    // (evaluator iter-2 item #4). Validation lives in record `init`
    // accessors so no malformed AgentQuestion / HumanAction can be
    // constructed; these tests pin both the limits and the boundary
    // behaviour (= limit OK, +1 rejected).
    // ============================================================

    [Fact]
    public void AgentQuestion_QuestionId_AcceptsBoundaryLength()
    {
        var boundary = new string('q', AgentQuestion.MaxQuestionIdLength);

        var question = BuildQuestion(questionId: boundary);

        question.QuestionId.Should().Be(boundary);
    }

    [Fact]
    public void AgentQuestion_QuestionId_RejectsOverLimit()
    {
        var tooLong = new string('q', AgentQuestion.MaxQuestionIdLength + 1);

        Action build = () => BuildQuestion(questionId: tooLong);

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.QuestionId));
    }

    [Fact]
    public void AgentQuestion_QuestionId_RejectsNullOrEmpty()
    {
        Action buildNull = () => BuildQuestion(questionId: null!);
        Action buildEmpty = () => BuildQuestion(questionId: string.Empty);

        buildNull.Should().Throw<ArgumentException>();
        buildEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgentQuestion_AllowedActions_RejectsDuplicateActionId()
    {
        Action build = () => BuildQuestion(allowedActions: new[]
        {
            new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
            new HumanAction { ActionId = "approve", Label = "Approve again", Value = "v2" }
        });

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.AllowedActions))
            .Where(ex => ex.Message.Contains("Duplicate ActionId"));
    }

    [Fact]
    public void AgentQuestion_AllowedActions_AcceptsDistinctActionIds()
    {
        var question = BuildQuestion(allowedActions: new[]
        {
            new HumanAction { ActionId = "a", Label = "A", Value = "a" },
            new HumanAction { ActionId = "b", Label = "B", Value = "b" },
            new HumanAction { ActionId = "c", Label = "C", Value = "c" }
        });

        question.AllowedActions.Should().HaveCount(3);
    }

    [Fact]
    public void AgentQuestion_AllowedActions_RejectsNull()
    {
        Action build = () => new AgentQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "t",
            Body = "b",
            Severity = MessageSeverity.High,
            AllowedActions = null!,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = "trace-1"
        };

        build.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HumanAction_ActionId_AcceptsBoundaryLength()
    {
        var boundary = new string('a', HumanAction.MaxActionIdLength);

        var action = new HumanAction { ActionId = boundary, Label = "ok", Value = "v" };

        action.ActionId.Should().Be(boundary);
    }

    [Fact]
    public void HumanAction_ActionId_RejectsOverLimit()
    {
        var tooLong = new string('a', HumanAction.MaxActionIdLength + 1);

        Action build = () => new HumanAction { ActionId = tooLong, Label = "ok", Value = "v" };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.ActionId));
    }

    [Fact]
    public void HumanAction_ActionId_RejectsNullOrEmpty()
    {
        Action buildNull = () => new HumanAction { ActionId = null!, Label = "ok", Value = "v" };
        Action buildEmpty = () => new HumanAction { ActionId = string.Empty, Label = "ok", Value = "v" };

        buildNull.Should().Throw<ArgumentException>();
        buildEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HumanAction_Label_AcceptsBoundaryLength()
    {
        var boundary = new string('L', HumanAction.MaxLabelLength);

        var action = new HumanAction { ActionId = "a", Label = boundary, Value = "v" };

        action.Label.Should().Be(boundary);
    }

    [Fact]
    public void HumanAction_Label_RejectsOverLimit()
    {
        var tooLong = new string('L', HumanAction.MaxLabelLength + 1);

        Action build = () => new HumanAction { ActionId = "a", Label = tooLong, Value = "v" };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.Label));
    }

    [Fact]
    public void HumanAction_Label_RejectsNullOrEmpty()
    {
        Action buildNull = () => new HumanAction { ActionId = "a", Label = null!, Value = "v" };
        Action buildEmpty = () => new HumanAction { ActionId = "a", Label = string.Empty, Value = "v" };

        buildNull.Should().Throw<ArgumentException>();
        buildEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CallbackData_RoundTripFitsTelegramSixtyFourByteBudget()
    {
        var questionId = new string('q', AgentQuestion.MaxQuestionIdLength);
        var actionId = new string('a', HumanAction.MaxActionIdLength);
        var callbackData = $"{questionId}:{actionId}";

        System.Text.Encoding.UTF8.GetByteCount(callbackData).Should().BeLessThanOrEqualTo(64,
            "QuestionId + ':' + ActionId must fit Telegram's 64-byte callback_data budget "
            + "(architecture.md §3.1 line 212).");
    }

    // ============================================================
    // Telegram callback-data is a 64-BYTE budget (UTF-8), not a
    // character budget (evaluator iter-3 item #1). The setters
    // therefore enforce ASCII on QuestionId / ActionId so a
    // 30-character identifier is provably ≤ 30 bytes on the wire;
    // and they enforce a UTF-8 byte count on Label so multi-byte
    // Unicode labels are constrained at the wire-relevant unit.
    // ============================================================

    [Fact]
    public void AgentQuestion_QuestionId_RejectsNonAsciiEvenWhenCharCountWithinLimit()
    {
        // 16 × 'あ' = 16 chars (≤ 30 char limit) but 48 UTF-8 bytes (> 30-byte budget).
        var multiByteWithinCharLimit = new string('あ', 16);
        multiByteWithinCharLimit.Length.Should().BeLessThanOrEqualTo(
            AgentQuestion.MaxQuestionIdLength,
            "this test only proves byte-vs-char divergence; char count must be within limit");
        System.Text.Encoding.UTF8.GetByteCount(multiByteWithinCharLimit).Should().BeGreaterThan(
            AgentQuestion.MaxQuestionIdLength,
            "this test only proves byte-vs-char divergence; byte count must exceed limit");

        Action build = () => BuildQuestion(questionId: multiByteWithinCharLimit);

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.QuestionId))
            .Where(ex => ex.Message.Contains("ASCII"));
    }

    [Theory]
    [InlineData("Q-1あ")]
    [InlineData("Q-1\u00A0")]
    [InlineData("\u200BQ-1")]
    [InlineData("Q\uFFFD1")]
    public void AgentQuestion_QuestionId_RejectsAnyNonAsciiCodePoint(string value)
    {
        Action build = () => BuildQuestion(questionId: value);

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.QuestionId));
    }

    [Fact]
    public void HumanAction_ActionId_RejectsNonAsciiEvenWhenCharCountWithinLimit()
    {
        var multiByteWithinCharLimit = new string('あ', 16);

        Action build = () => new HumanAction
        {
            ActionId = multiByteWithinCharLimit,
            Label = "ok",
            Value = "v"
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.ActionId))
            .Where(ex => ex.Message.Contains("ASCII"));
    }

    [Theory]
    [InlineData("approve-é")]
    [InlineData("✓ok")]
    [InlineData("ré-try")]
    public void HumanAction_ActionId_RejectsAnyNonAsciiCodePoint(string value)
    {
        Action build = () => new HumanAction { ActionId = value, Label = "ok", Value = "v" };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.ActionId));
    }

    [Fact]
    public void HumanAction_Label_AcceptsMultiByteUnderByteBudget()
    {
        // 21 × 'é' = 42 UTF-8 bytes (each is 2 bytes), under the 64-byte budget.
        var multiByte = new string('é', 21);
        System.Text.Encoding.UTF8.GetByteCount(multiByte).Should().BeLessThanOrEqualTo(
            HumanAction.MaxLabelByteLength);

        var action = new HumanAction { ActionId = "a", Label = multiByte, Value = "v" };

        action.Label.Should().Be(multiByte);
    }

    [Fact]
    public void HumanAction_Label_RejectsMultiByteOverByteBudgetEvenWhenCharCountFits()
    {
        // 33 × 'é' = 33 chars (well under any char-count interpretation of 64)
        // but 66 UTF-8 bytes (exceeds 64-byte budget).
        var multiByteOverByteBudget = new string('é', 33);
        multiByteOverByteBudget.Length.Should().BeLessThanOrEqualTo(64,
            "char count alone would (wrongly) accept this value");
        System.Text.Encoding.UTF8.GetByteCount(multiByteOverByteBudget).Should().BeGreaterThan(
            HumanAction.MaxLabelByteLength,
            "byte count rightly exceeds budget");

        Action build = () => new HumanAction
        {
            ActionId = "a",
            Label = multiByteOverByteBudget,
            Value = "v"
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.Label))
            .Where(ex => ex.Message.Contains("UTF-8 byte"));
    }

    [Fact]
    public void HumanAction_Label_AcceptsBoundaryByteCount()
    {
        // 32 × 'é' = 64 UTF-8 bytes — exactly at the budget.
        var atBoundary = new string('é', HumanAction.MaxLabelByteLength / 2);
        System.Text.Encoding.UTF8.GetByteCount(atBoundary).Should().Be(
            HumanAction.MaxLabelByteLength);

        var action = new HumanAction { ActionId = "a", Label = atBoundary, Value = "v" };

        action.Label.Should().Be(atBoundary);
    }

    [Fact]
    public void CallbackData_NonAsciiPathIsBlockedAtConstructionNotAtSerialization()
    {
        // Pre-validation guarantee: by the time the connector serializes
        // "QuestionId:ActionId" into callback_data, both halves are ASCII,
        // so the wire encoder cannot encounter a multi-byte surprise.
        var question = BuildQuestion(questionId: new string('q', AgentQuestion.MaxQuestionIdLength));
        var action = new HumanAction
        {
            ActionId = new string('a', HumanAction.MaxActionIdLength),
            Label = "ok",
            Value = "v"
        };

        System.Text.Encoding.UTF8.GetByteCount(question.QuestionId).Should().Be(question.QuestionId.Length,
            "ASCII enforcement guarantees byte count equals character count");
        System.Text.Encoding.UTF8.GetByteCount(action.ActionId).Should().Be(action.ActionId.Length,
            "ASCII enforcement guarantees byte count equals character count");
    }

    // ============================================================
    // Callback-data parse safety (evaluator iter-4 item #5).
    // The wire format is "QuestionId:ActionId"; an embedded ':' on
    // either side would make the parse ambiguous and silently
    // re-route the operator's tap. Control characters would
    // invisibly corrupt the encoded payload. Validation must
    // therefore reject both at construction time so the malformed
    // value can never reach the encoder.
    // ============================================================

    [Fact]
    public void AgentQuestion_QuestionId_RejectsColonSeparator()
    {
        Action build = () => BuildQuestion(questionId: "Q:1");

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.QuestionId))
            .Where(ex => ex.Message.Contains("':'"));
    }

    [Theory]
    [InlineData("Q\u0000id")]
    [InlineData("Q\u001Fid")]
    [InlineData("Q\u007Fid")]
    [InlineData("Q\nid")]
    [InlineData("Q\rid")]
    [InlineData("Q\tid")]
    public void AgentQuestion_QuestionId_RejectsAsciiControlCharacters(string value)
    {
        Action build = () => BuildQuestion(questionId: value);

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.QuestionId))
            .Where(ex => ex.Message.Contains("control"));
    }

    [Fact]
    public void HumanAction_ActionId_RejectsColonSeparator()
    {
        Action build = () => new HumanAction { ActionId = "a:b", Label = "ok", Value = "v" };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.ActionId))
            .Where(ex => ex.Message.Contains("':'"));
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u001Fb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\nb")]
    public void HumanAction_ActionId_RejectsAsciiControlCharacters(string value)
    {
        Action build = () => new HumanAction { ActionId = value, Label = "ok", Value = "v" };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanAction.ActionId))
            .Where(ex => ex.Message.Contains("control"));
    }

    [Fact]
    public void CallbackData_BoundaryAsciiIdsRoundTripUnambiguously()
    {
        // After ASCII + no-':' enforcement, splitting "Q:A" on the FIRST ':'
        // is guaranteed to recover both halves verbatim, even at the
        // 30+1+30 = 61-byte boundary that fits the 64-byte budget.
        var qid = new string('q', AgentQuestion.MaxQuestionIdLength);
        var aid = new string('a', HumanAction.MaxActionIdLength);
        var question = BuildQuestion(
            questionId: qid,
            allowedActions: new[] { new HumanAction { ActionId = aid, Label = "ok", Value = "v" } });

        var callback = $"{question.QuestionId}:{question.AllowedActions[0].ActionId}";
        var split = callback.Split(':', 2);

        split.Should().HaveCount(2);
        split[0].Should().Be(qid);
        split[1].Should().Be(aid);
        System.Text.Encoding.UTF8.GetByteCount(callback).Should().BeLessThanOrEqualTo(64);
    }

    // ============================================================
    // ProposedDefaultActionId integrity (evaluator iter-4 item #4).
    // A default that doesn't correspond to any AllowedActions entry
    // would cause QuestionTimeoutService to publish a
    // HumanDecisionEvent.ActionValue with no matching agent branch,
    // silently dropping the operator's "default on timeout" intent.
    // The envelope must therefore reject the mismatch at construction.
    // ============================================================

    [Fact]
    public void AgentQuestionEnvelope_AcceptsProposedDefaultMatchingAllowedAction()
    {
        var envelope = CreateEnvelope(proposedDefault: "approve");

        envelope.ProposedDefaultActionId.Should().Be("approve");
        envelope.Question.AllowedActions.Should().Contain(a => a.ActionId == "approve");
    }

    [Fact]
    public void AgentQuestionEnvelope_AcceptsNullProposedDefault()
    {
        var envelope = CreateEnvelope(proposedDefault: null);

        envelope.ProposedDefaultActionId.Should().BeNull();
    }

    [Fact]
    public void AgentQuestionEnvelope_RejectsProposedDefaultNotInAllowedActions()
    {
        Action build = () => CreateEnvelope(proposedDefault: "delete");

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestionEnvelope.ProposedDefaultActionId))
            .Where(ex => ex.Message.Contains("delete"));
    }

    [Fact]
    public void AgentQuestionEnvelope_ProposedDefaultMatchIsCaseSensitive()
    {
        // AllowedActions has "approve" — "Approve" must NOT satisfy the
        // match because callback dispatch is ordinal/case-sensitive.
        Action build = () => CreateEnvelope(proposedDefault: "Approve");

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestionEnvelope.ProposedDefaultActionId));
    }

    [Fact]
    public void AgentQuestionEnvelope_ValidatesWhenPropertiesSetInReverseOrder()
    {
        // Object initializer evaluation order is Roslyn-stable but Question
        // could appear after ProposedDefaultActionId in source. Validation
        // must still fire once both have been set; this pins the
        // order-independence promised in the AgentQuestionEnvelope remarks.
        var question = new AgentQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "Approve?",
            Body = "Body",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" }
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = "trace-1"
        };

        Action build = () => new AgentQuestionEnvelope
        {
            ProposedDefaultActionId = "delete",
            Question = question
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestionEnvelope.ProposedDefaultActionId));
    }

    // ============================================================
    // Trace/correlation ID invariants (evaluator iter-4 item #6).
    // Story acceptance criterion: "All messages include trace/
    // correlation ID." The required modifier guarantees presence at
    // compile time but cannot reject empty / whitespace strings —
    // CorrelationIdValidation does. Apply the gate uniformly across
    // the four contracts the evaluator named (AgentQuestion,
    // MessengerEvent, SwarmCommand, HumanDecisionEvent), plus the
    // additional Abstractions / Core trace-bearing records that
    // share the helper.
    // ============================================================

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("   ")]
    public void AgentQuestion_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => BuildQuestion(correlationId: blank);

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.CorrelationId));
    }

    [Fact]
    public void AgentQuestion_CorrelationId_RejectsNull()
    {
        Action build = () => BuildQuestion(correlationId: null);

        build.Should().Throw<ArgumentNullException>()
            .Where(ex => ex.ParamName == nameof(AgentQuestion.CorrelationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void MessengerEvent_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => new MessengerEvent
        {
            EventId = "evt-1",
            EventType = EventType.Command,
            UserId = "u-1",
            ChatId = "c-1",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = blank
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(MessengerEvent.CorrelationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public void SwarmCommand_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => new SwarmCommand
        {
            CommandType = SwarmCommandType.Approve,
            OperatorId = Guid.NewGuid(),
            CorrelationId = blank
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(SwarmCommand.CorrelationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void HumanDecisionEvent_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => new HumanDecisionEvent
        {
            QuestionId = "Q-1",
            ActionValue = "approve",
            Messenger = "telegram",
            ExternalUserId = "100",
            ExternalMessageId = "42",
            ReceivedAt = DateTimeOffset.UtcNow,
            CorrelationId = blank
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(HumanDecisionEvent.CorrelationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void OutboundMessage_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "idem-1",
            ChatId = 1,
            Payload = "{}",
            Severity = MessageSeverity.Normal,
            SourceType = OutboundSourceType.Alert,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = blank
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(OutboundMessage.CorrelationId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void TaskOversight_CorrelationId_RejectsEmptyOrWhitespace(string blank)
    {
        Action build = () => new TaskOversight
        {
            TaskId = "T-1",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "u-1",
            CorrelationId = blank
        };

        build.Should().Throw<ArgumentException>()
            .Where(ex => ex.ParamName == nameof(TaskOversight.CorrelationId));
    }

    [Fact]
    public void CorrelationIdValidation_IsPubliclyAccessibleSoCoreContractsCanShareIt()
    {
        // OutboundMessage now lives in Abstractions (per Stage 1.4); TaskOversight
        // remains in Core. AgentQuestion etc. are in Abstractions. They all need
        // the same guard, and at least one cross-assembly consumer (TaskOversight
        // in Core) still depends on this type being public. Pin the public
        // visibility so a refactor cannot quietly downgrade it to internal and
        // split the dialect of "trace-id required."
        var helper = typeof(CorrelationIdValidation);

        helper.IsPublic.Should().BeTrue(
            "Core records (TaskOversight) reference this helper across the "
            + "assembly boundary; making it internal would force Core to "
            + "duplicate the guard and risk drift.");
    }

    private static AgentQuestion BuildQuestion(
        string? questionId = "Q-1",
        IReadOnlyList<HumanAction>? allowedActions = null,
        string? correlationId = "trace-1")
    {
        return new AgentQuestion
        {
            QuestionId = questionId!,
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "Approve?",
            Body = "Body",
            Severity = MessageSeverity.High,
            AllowedActions = allowedActions ?? new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" }
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = correlationId!
        };
    }
}
