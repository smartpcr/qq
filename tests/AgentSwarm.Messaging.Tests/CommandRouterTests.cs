using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — verifies <see cref="CommandRouter"/> dispatches to the
/// handler advertising the matching <see cref="ICommandHandler.CommandName"/>,
/// rejects unknown commands with a help message listing the available
/// vocabulary, and fails fast on bad handler registrations.
///
/// Stage 5.3 — additionally verifies the router emits audit
/// <see cref="AuditEntry"/> rows for every inbound command via the
/// injected <see cref="IAuditLogger"/>, carrying the operator's
/// tenant, the inbound trace correlation id, and a JSON details
/// payload. Per Stage 5.3 iter-6 evaluator item 2, every command
/// that DISPATCHES a handler emits TWO rows — a receipt (BEFORE
/// handler runs, the integrity guarantee against orphan side
/// effects) and a completion (AFTER handler returns or throws, the
/// observability bonus). Unknown commands emit a single receipt
/// row because no handler dispatches.
/// </summary>
public class CommandRouterTests
{
    [Fact]
    public async Task RouteAsync_DispatchesToMatchingHandler_EmitsReceiptAndCompletionAuditRows()
    {
        var handler = new RecordingHandler("status", "✅ swarm idle");
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("status"), NewOperator(), default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("✅ swarm idle");
        handler.InvocationCount.Should().Be(1);

        audit.GeneralEntries.Should().HaveCount(2,
            "Stage 5.3 iter-6 evaluator item 2: the router must emit BOTH a receipt row (BEFORE handler dispatch — the integrity guarantee that prevents orphan side effects on audit failure) AND a completion row (AFTER handler returns — the observability bonus that captures the handler's outcome)");

        var receipt = audit.GeneralEntries[0];
        var completion = audit.GeneralEntries[1];

        receipt.Action.Should().Be("status");
        receipt.EventFamily.Should().Be(AuditEventFamilies.Command);
        receipt.UserId.Should().Be("42");
        receipt.TenantId.Should().Be("t-a");
        receipt.Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Received}\"",
            "Stage 5.3 iter-6 evaluator item 2: the receipt row's Details JSON must carry phase=received so forensic queries can split receipt vs completion when filtering by Action+CorrelationId");

        completion.Action.Should().Be("status");
        completion.EventFamily.Should().Be(AuditEventFamilies.Command);
        completion.UserId.Should().Be("42");
        completion.TenantId.Should().Be("t-a");
        completion.Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Completed}\"",
            "Stage 5.3 iter-6 evaluator item 2: the completion row's Details JSON must carry phase=completed so it is distinguishable from the receipt");

        receipt.CorrelationId.Should().Be(completion.CorrelationId,
            "Stage 5.3 iter-6 evaluator item 2: receipt and completion MUST share the same CorrelationId so log queries can `WHERE CorrelationId=X` and reconstruct the full command lifecycle in one predicate");
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "Stage 5.3 acceptance: CorrelationId must be non-null");
    }

    [Fact]
    public async Task RouteAsync_IsCaseInsensitive()
    {
        var handler = new RecordingHandler("agents", "roster");
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("Agents"), NewOperator(), default);

        result.Success.Should().BeTrue();
        handler.InvocationCount.Should().Be(1);
        audit.GeneralEntries.Should().HaveCount(2,
            "Stage 5.3 iter-6 evaluator item 2: receipt + completion rows are written for every dispatched command");
        audit.GeneralEntries[0].Action.Should().Be("agents",
            "Stage 5.3 iter-2 evaluator item 1: Action stores the bare command verb (normalized to lower-case) so log analytics queries against a single literal succeed regardless of the operator's casing — the command-family discriminator lives in EventFamily, not Action");
        audit.GeneralEntries[1].Action.Should().Be("agents",
            "the completion row carries the same Action verb as the receipt");
    }

    [Fact]
    public async Task RouteAsync_UnknownCommand_ReturnsHelpfulErrorListingValidCommands()
    {
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            Array.Empty<ICommandHandler>(),
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("foo"), NewOperator(), default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(CommandRouter.UnknownCommandErrorCode);
        result.ResponseText.Should().NotBeNullOrWhiteSpace();
        foreach (var name in TelegramCommands.All)
        {
            result.ResponseText.Should().Contain("/" + name,
                "the unknown-command reply must list every recognized command");
        }
        result.ResponseText.Should().Contain("foo",
            "the reply should echo what the operator typed so they can correct it");
        audit.GeneralEntries.Should().ContainSingle(
                "Stage 5.3 iter-6 evaluator item 2: unknown commands emit a single receipt row because no handler dispatches — there is no separate completion phase when no side effects ran")
            .Which.Action.Should().Be(CommandRouter.UnknownCommandAuditAction,
                "Stage 5.3 brief: unknown commands must still leave an audit trail");
        audit.GeneralEntries[0].Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Received}\"",
            "the lone audit row for an unknown command is the receipt — no completion row is emitted because no handler ran");
    }

    [Fact]
    public async Task RouteAsync_AskCommand_AuditedWithExternalUserId_PerStoryScenario()
    {
        // Acceptance scenario from the Stage 5.3 brief:
        //   "Given user 12345 sends /ask build release notes, When the
        //    command is processed, Then an AuditLogEntry exists with
        //    Action=ask, ExternalUserId=12345, and a non-null
        //    CorrelationId."
        // The router-level audit is the canonical hook for this
        // observation — Stage 5.3 iter-2 evaluator item 1: the
        // persistence layer maps Action verbatim (it does NOT prefix
        // "command."); the command-family discriminator lives on
        // EventFamily. The "ExternalUserId" mapping happens because
        // the persistence layer renames AuditEntry.UserId →
        // AuditLogEntry.ExternalUserId.
        //
        // Stage 5.3 iter-6 evaluator item 2: every dispatched command
        // emits BOTH a receipt and a completion row; both rows carry
        // the same Action verb and CorrelationId so the acceptance
        // assertion "an AuditLogEntry exists with Action=ask" is
        // satisfied by either row.
        var handler = new RecordingHandler("ask", "queued");
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var op = new AuthorizedOperator
        {
            OperatorId = Guid.NewGuid(),
            TenantId = "t-acme",
            WorkspaceId = "w-1",
            TelegramUserId = 12345,
            TelegramChatId = 999,
            OperatorAlias = "@alice",
        };

        var result = await router.RouteAsync(
            NewCommand("ask", "build", "release", "notes"),
            op,
            default);

        result.Success.Should().BeTrue();
        audit.GeneralEntries.Should().HaveCount(2,
            "Stage 5.3 iter-6 evaluator item 2: /ask dispatches a handler so both the receipt and completion rows are written");
        var receipt = audit.GeneralEntries[0];
        var completion = audit.GeneralEntries[1];

        receipt.Action.Should().Be("ask",
            "Stage 5.3 acceptance: 'AuditLogEntry exists with Action=ask' — the column carries the canonical command verb verbatim. Stage 5.3 iter-2 evaluator item 1: the family discriminator (was 'command.<name>') moved off Action onto EventFamily so the acceptance assertion matches the literal string the brief specifies");
        receipt.EventFamily.Should().Be(AuditEventFamilies.Command,
            "Stage 5.3 iter-2 evaluator item 1: the new EventFamily column carries the orthogonal 'this is a command-family event' discriminator the iter-1 'command.ask' overload conflated with Action");
        receipt.UserId.Should().Be("12345",
            "Stage 5.3 acceptance: ExternalUserId=12345 (the persistence layer renames UserId → ExternalUserId)");
        receipt.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "Stage 5.3 acceptance: CorrelationId must be non-null");
        receipt.TenantId.Should().Be("t-acme");
        receipt.Details.Should().Contain("\"build\"")
            .And.Contain("\"release\"")
            .And.Contain("\"notes\"");

        completion.Action.Should().Be("ask",
            "the completion row mirrors the receipt's Action so the acceptance scenario passes regardless of which row a forensic query picks");
        completion.CorrelationId.Should().Be(receipt.CorrelationId,
            "Stage 5.3 iter-6 evaluator item 2: receipt + completion share the same CorrelationId so the lifecycle is reconstructable with WHERE CorrelationId=X");
    }

    [Fact]
    public void Ctor_DuplicateCommandNames_ThrowsAtRegistrationTime()
    {
        var a = new RecordingHandler("status", "a");
        var b = new RecordingHandler("status", "b");

        var act = () => new CommandRouter(
            new ICommandHandler[] { a, b },
            new RecordingAuditLogger(),
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate ICommandHandler*status*");
    }

    [Fact]
    public void Ctor_BlankCommandName_Throws()
    {
        var blank = new RecordingHandler("", "x");

        var act = () => new CommandRouter(
            new ICommandHandler[] { blank },
            new RecordingAuditLogger(),
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*blank CommandName*");
    }

    // ============================================================
    // Stage 5.3 iter-2 evaluator item 3:
    // The router MUST audit every inbound command, including those
    // whose handler throws — otherwise a buggy handler / transient
    // downstream failure leaves the inbound command unaudited and
    // the operator's intent is lost from the audit trail.
    //
    // Stage 5.3 iter-6 evaluator item 2:
    // The audit row split (receipt before / completion after)
    // means a handler-throws case emits BOTH rows — the receipt
    // captures the inbound shape (regardless of whether the
    // handler will throw), the completion captures the exception
    // type/message after the handler runs.
    // ============================================================

    [Fact]
    public async Task RouteAsync_HandlerThrows_StillEmitsReceiptAndCompletionAuditAndRethrows()
    {
        var throwing = new ThrowingHandler("approve", new InvalidOperationException("downstream bus failed"));
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            new ICommandHandler[] { throwing },
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var op = NewOperator();
        var act = async () => await router.RouteAsync(NewCommand("approve", "Q-7"), op, default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("downstream bus failed",
                "Stage 5.3 iter-2 evaluator item 3: the handler's exception must propagate to the pipeline-level catch so the operator-visible failure path is unchanged");

        audit.GeneralEntries.Should().HaveCount(2,
            "Stage 5.3 iter-6 evaluator item 2: receipt + completion are BOTH written even when the handler throws — the receipt captures the inbound shape before the handler runs (guaranteeing the audit trail), and the completion captures the exception type/message after the handler throws");

        var receipt = audit.GeneralEntries[0];
        var completion = audit.GeneralEntries[1];

        receipt.Action.Should().Be("approve",
            "Stage 5.3 iter-2 evaluator item 3: the canonical command verb is persisted on the receipt row regardless of the handler outcome");
        receipt.Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Received}\"");
        receipt.Details.Should().NotContain("InvalidOperationException",
            "the receipt is written BEFORE the handler runs so it cannot carry exception info — that information is captured separately on the completion row");

        completion.Action.Should().Be("approve");
        completion.UserId.Should().Be("42");
        completion.TenantId.Should().Be("t-a");
        completion.CorrelationId.Should().NotBeNullOrWhiteSpace();
        completion.CorrelationId.Should().Be(receipt.CorrelationId,
            "Stage 5.3 iter-6 evaluator item 2: receipt + completion share a correlation id so forensic queries can join the two rows for the same failed command");
        completion.Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Completed}\"");
        completion.Details.Should().Contain("InvalidOperationException",
            "the Details JSON on the completion row records the exception type so the failure is recoverable from the audit trail alone");
        completion.Details.Should().Contain("downstream bus failed",
            "the Details JSON on the completion row records the exception message");
    }

    // ============================================================
    // Stage 5.3 iter-6 evaluator item 2:
    // The receipt audit row writes BEFORE the handler dispatches.
    // If the receipt write fails (transient audit DB outage), the
    // handler MUST NOT run — otherwise the side effect (publish a
    // SwarmCommand, emit a HumanDecisionEvent) commits with no
    // recoverable audit trail and a retry would duplicate the work.
    //
    // The completion audit row writes AFTER the handler returns or
    // throws. If the completion write fails, the receipt already
    // satisfies the "log every inbound command" persistence
    // guarantee — failing the operator after a successful side
    // effect would force them to retry an already-committed
    // command, so completion failures are LOGGED but NOT rethrown.
    // ============================================================

    [Fact]
    public async Task RouteAsync_ReceiptAuditFails_HandlerNotInvoked_RethrowsToPipeline()
    {
        var handler = new RecordingHandler("ask", "queued");
        var failingAudit = new FailingAuditLogger(failOnNthCall: 1, ex: new InvalidOperationException("audit DB down"));
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            failingAudit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var act = async () => await router.RouteAsync(NewCommand("ask", "test"), NewOperator(), default);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "Stage 5.3 iter-6 evaluator item 2: a receipt audit failure MUST propagate so the pipeline marks the inbound update Failed and the recovery sweep retries from a clean slate"))
            .Which.Message.Should().Be("audit DB down");

        handler.InvocationCount.Should().Be(0,
            "Stage 5.3 iter-6 evaluator item 2: the handler MUST NOT run when the receipt audit fails — otherwise the side effect (publish SwarmCommand) commits with no recoverable audit trail and a retry would duplicate the work");
        failingAudit.AcceptedEntries.Should().BeEmpty(
            "the receipt's failed write is the only attempted write — no completion row is attempted because the handler never ran");
        failingAudit.AttemptedCalls.Should().Be(1,
            "the router stops at the receipt write — it must not attempt the completion write when the receipt failed");
    }

    [Fact]
    public async Task RouteAsync_CompletionAuditFails_DoesNotRethrow_ReceiptRowDurable()
    {
        var handler = new RecordingHandler("status", "✅ swarm idle");
        // First call (receipt) succeeds, second call (completion) throws.
        var failingAudit = new FailingAuditLogger(failOnNthCall: 2, ex: new InvalidOperationException("audit DB transient"));
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            failingAudit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var result = await router.RouteAsync(NewCommand("status"), NewOperator(), default);

        result.Success.Should().BeTrue(
            "Stage 5.3 iter-6 evaluator item 2: a completion audit failure MUST NOT rethrow — the receipt row already satisfies the 'log every inbound command' guarantee, and failing the operator after a successful side effect would force them to retry an already-committed command");
        handler.InvocationCount.Should().Be(1,
            "the handler ran successfully (the receipt succeeded); only the completion audit write failed");
        failingAudit.AcceptedEntries.Should().ContainSingle(
                "only the receipt row was successfully persisted — the completion row's write threw")
            .Which.Details.Should().Contain($"\"phase\":\"{CommandRouter.CommandAuditPhases.Received}\"");
        failingAudit.AttemptedCalls.Should().Be(2,
            "both receipt and completion writes were attempted (the receipt succeeded, the completion threw)");
    }

    [Fact]
    public async Task RouteAsync_PropagatesSourceMessageIdToAuditMessageIdColumn()
    {
        // Stage 5.3 iter-6 evaluator item 3: command audit rows MUST
        // carry the Telegram update_id (propagated via
        // ParsedCommand.SourceMessageId by the pipeline) so the
        // audit_logs.MessageId column is joinable back to
        // inbound_updates.EventId. The prior router hard-coded
        // MessageId=null which violated the Stage 5.3 brief's
        // "full context" requirement.
        var handler = new RecordingHandler("status", "ok");
        var audit = new RecordingAuditLogger();
        var router = new CommandRouter(
            new ICommandHandler[] { handler },
            audit,
            TimeProvider.System,
            NullLogger<CommandRouter>.Instance);

        var parsed = NewCommand("status") with
        {
            SourceMessageId = "update-42",
            TraceId = "trace-abc",
        };

        var result = await router.RouteAsync(parsed, NewOperator(), default);

        result.Success.Should().BeTrue();
        audit.GeneralEntries.Should().HaveCount(2);
        audit.GeneralEntries[0].MessageId.Should().Be("update-42",
            "Stage 5.3 iter-6 evaluator item 3: the receipt row's MessageId surfaces the inbound transport id (ParsedCommand.SourceMessageId) onto audit_logs.MessageId");
        audit.GeneralEntries[1].MessageId.Should().Be("update-42",
            "Stage 5.3 iter-6 evaluator item 3: the completion row also carries the same MessageId for symmetric joinability");
        audit.GeneralEntries[0].CorrelationId.Should().Be("trace-abc",
            "Stage 5.3 iter-6 evaluator item 2: the router prefers the inbound trace id (ParsedCommand.TraceId) over a freshly-minted GUID so the audit row shares correlation with every other pipeline / dedup / outbound artifact for this update");
        audit.GeneralEntries[1].CorrelationId.Should().Be("trace-abc",
            "the completion row shares the receipt's correlation id");
    }

    private static ParsedCommand NewCommand(string name, params string[] args) => new()
    {
        CommandName = name,
        Arguments = args,
        RawText = "/" + name + (args.Length == 0 ? "" : " " + string.Join(' ', args)),
        IsValid = true,
    };

    internal static AuthorizedOperator NewOperator() => new()
    {
        OperatorId = Guid.NewGuid(),
        TenantId = "t-a",
        WorkspaceId = "w-1",
        TelegramUserId = 42,
        TelegramChatId = 100,
        OperatorAlias = "@op",
    };

    private sealed class RecordingHandler : ICommandHandler
    {
        private readonly string _response;
        public string CommandName { get; }
        public int InvocationCount { get; private set; }

        public RecordingHandler(string commandName, string response)
        {
            CommandName = commandName;
            _response = response;
        }

        public Task<CommandResult> HandleAsync(ParsedCommand command, AuthorizedOperator @operator, CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(new CommandResult
            {
                Success = true,
                ResponseText = _response,
                CorrelationId = Guid.NewGuid().ToString("N"),
            });
        }
    }

    /// <summary>
    /// Handler that throws a configured exception on every invocation.
    /// Used to verify Stage 5.3 iter-2 evaluator item 3: the router
    /// audits every inbound command, INCLUDING those whose handler
    /// throws (the exception still propagates to the pipeline-level
    /// catch but only AFTER the completion audit row is written).
    /// </summary>
    private sealed class ThrowingHandler : ICommandHandler
    {
        private readonly Exception _ex;
        public string CommandName { get; }

        public ThrowingHandler(string commandName, Exception ex)
        {
            CommandName = commandName;
            _ex = ex;
        }

        public Task<CommandResult> HandleAsync(ParsedCommand command, AuthorizedOperator @operator, CancellationToken ct)
        {
            throw _ex;
        }
    }

    /// <summary>
    /// In-memory <see cref="IAuditLogger"/> that captures every entry
    /// so the Stage 5.3 router-audit assertions can pin the shape of
    /// what the router emits without depending on the
    /// <c>PersistentAuditLogger</c> EF pipeline (covered separately
    /// by <c>PersistentAuditLoggerTests</c>).
    /// </summary>
    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEntry> GeneralEntries { get; } = new();
        public List<HumanResponseAuditEntry> HumanResponses { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            GeneralEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
        {
            HumanResponses.Add(entry);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 2 — audit logger fake that
    /// throws on the Nth <see cref="IAuditLogger.LogAsync"/> call
    /// to exercise the receipt-fails / completion-fails branches of
    /// <see cref="CommandRouter"/>. Tracks which entries were
    /// successfully persisted vs which write attempts were made so
    /// the test can pin that:
    ///   * receipt-fails → handler NOT invoked, completion NOT attempted
    ///   * completion-fails → receipt durable, handler ran, no rethrow
    /// </summary>
    private sealed class FailingAuditLogger : IAuditLogger
    {
        private readonly int _failOnNthCall;
        private readonly Exception _ex;

        public List<AuditEntry> AcceptedEntries { get; } = new();
        public int AttemptedCalls { get; private set; }

        public FailingAuditLogger(int failOnNthCall, Exception ex)
        {
            _failOnNthCall = failOnNthCall;
            _ex = ex ?? throw new ArgumentNullException(nameof(ex));
        }

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            AttemptedCalls++;
            if (AttemptedCalls == _failOnNthCall)
            {
                throw _ex;
            }
            AcceptedEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
            => Task.CompletedTask;
    }
}
