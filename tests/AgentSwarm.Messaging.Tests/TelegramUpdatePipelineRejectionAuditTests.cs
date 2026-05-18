using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 iter-7 evaluator item 1 — pins the contract that EVERY
/// inbound command rejected at the pipeline level (parse-empty,
/// parse-invalid, authorize-denied, role-denied) writes a durable
/// <see cref="AuditEntry"/> through <see cref="IAuditLogger"/> BEFORE
/// returning the denial response. Without these tests an iter-N+1
/// refactor could silently re-introduce the "log-only, no persist"
/// shape the iter-6 evaluator flagged.
/// </summary>
/// <remarks>
/// <para>
/// These tests construct the pipeline through the Stage 5.3 iter-7
/// eleven-argument constructor that takes an
/// <see cref="IAuditLogger"/>. The recording fake captures every
/// LogAsync invocation so the assertions can pin the per-site Action
/// verb (always <see cref="TelegramUpdatePipeline.PipelineDeniedAuditAction"/>
/// = "command.denied"), the per-site Details JSON
/// <c>phase</c> discriminator (
/// <see cref="TelegramUpdatePipeline.PipelineDeniedPhases"/>), and
/// the cross-row invariants (<c>EventFamily =
/// AuditEventFamilies.Lifecycle</c>, <c>MessageId =
/// messengerEvent.EventId</c>, <c>CorrelationId =
/// messengerEvent.CorrelationId</c>).
/// </para>
/// <para>
/// The audit write is fire-and-forget with a try/catch that LOGS but
/// does NOT rethrow — the denial response itself is the
/// security-critical path. <c>AuditWriteFails_DenialStillFires</c>
/// pins that contract: even when the audit logger throws, the
/// unauthorized caller still gets the Unauthorized reply.
/// </para>
/// </remarks>
public class TelegramUpdatePipelineRejectionAuditTests
{
    private const string OperatorTenantId = "tenant-iter7";
    private const string OperatorUserIdStr = "100";

    [Fact]
    public async Task Pipeline_ParseEmpty_WritesLifecycleAuditRow_BeforeDenial()
    {
        var harness = new Harness();
        var evt = harness.MakeCommand(rawCommand: " "); // blank => parse-empty
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);
        harness.Audit.Entries.Should().HaveCount(1, "parse-empty must persist exactly one lifecycle row");
        var row = harness.Audit.Entries[0];
        row.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        row.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        row.MessageId.Should().Be(evt.EventId, "MessageId must carry the inbound transport id");
        row.CorrelationId.Should().Be(evt.CorrelationId);
        row.UserId.Should().Be(evt.UserId);
        row.TenantId.Should().BeNull("no operator binding has been resolved for a parse-empty rejection");
        AssertDetailsPhase(row, TelegramUpdatePipeline.PipelineDeniedPhases.ParseEmpty);
    }

    [Fact]
    public async Task Pipeline_ParseInvalid_WritesLifecycleAuditRow_BeforeDenial()
    {
        var harness = new Harness();
        harness.ParserStub.Setup(p => p.Parse("/bogus"))
            .Returns(new ParsedCommand
            {
                CommandName = "bogus",
                RawText = "/bogus",
                IsValid = false,
                ValidationError = "unknown verb",
            });

        var evt = harness.MakeCommand(rawCommand: "/bogus");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);
        harness.Audit.Entries.Should().HaveCount(1, "parse-invalid must persist exactly one lifecycle row");
        var row = harness.Audit.Entries[0];
        row.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        row.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        row.MessageId.Should().Be(evt.EventId);
        row.CorrelationId.Should().Be(evt.CorrelationId);
        AssertDetailsPhase(row, TelegramUpdatePipeline.PipelineDeniedPhases.ParseInvalid);
        AssertDetailsHasReason(row, "unknown verb");
    }

    [Fact]
    public async Task Pipeline_AuthorizeDenied_WritesLifecycleAuditRow_BeforeDenial()
    {
        var harness = new Harness();
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "user not in allowlist",
            });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });

        var evt = harness.MakeCommand(rawCommand: "/status");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        harness.Audit.Entries.Should().HaveCount(1, "authorize-denied must persist exactly one lifecycle row");
        var row = harness.Audit.Entries[0];
        row.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        row.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        row.MessageId.Should().Be(evt.EventId);
        row.CorrelationId.Should().Be(evt.CorrelationId);
        row.UserId.Should().Be(evt.UserId);
        row.TenantId.Should().BeNull("no operator binding for a rejected unauthorized caller");
        AssertDetailsPhase(row, TelegramUpdatePipeline.PipelineDeniedPhases.AuthorizeDenied);
        AssertDetailsHasReason(row, "user not in allowlist");
    }

    [Fact]
    public async Task Pipeline_RoleDenied_WritesLifecycleAuditRow_WithOperatorTenant_BeforeDenial()
    {
        var harness = new Harness();
        // /approve requires the Approver role; bind the operator without it.
        var binding = harness.MakeBinding(roles: Array.Empty<string>(), tenantId: OperatorTenantId);
        harness.AuthorizeWith(binding);
        harness.ParserStub.Setup(p => p.Parse("/approve Q1"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Approve,
                RawText = "/approve Q1",
                IsValid = true,
            });

        var evt = harness.MakeCommand(rawCommand: "/approve Q1");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.InsufficientPermissions);
        harness.Audit.Entries.Should().HaveCount(1, "role-denied must persist exactly one lifecycle row");
        var row = harness.Audit.Entries[0];
        row.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        row.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        row.MessageId.Should().Be(evt.EventId);
        row.CorrelationId.Should().Be(evt.CorrelationId);
        row.TenantId.Should().Be(OperatorTenantId,
            "role-denied has a resolved operator binding so TenantId must land on the audit row");
        AssertDetailsPhase(row, TelegramUpdatePipeline.PipelineDeniedPhases.RoleDenied);
    }

    [Fact]
    public async Task Pipeline_RejectionAudit_HappyPath_DoesNotEmitDenialRowForRouterSuccess()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "Status: OK",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand(rawCommand: "/status");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        harness.Audit.Entries.Should().BeEmpty(
            "the rejection-audit path only fires on pipeline denials; the CommandRouter owns receipt/completion rows for successful routes");
    }

    [Fact]
    public async Task Pipeline_AuditWriteFails_DenialStillFires_AndErrorIsLogged()
    {
        // Stage 5.3 iter-7 evaluator item 1 — security-critical
        // contract. The denial response MUST reach the operator even
        // when the audit DB is momentarily unavailable, otherwise a
        // crashed audit DB would also crash the authorization
        // rejection path (DoS vector). The helper logs at Error
        // level and continues.
        var audit = new FailingAuditLogger(new InvalidOperationException("audit-db-down"));
        var harness = new Harness(audit: audit);
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult { IsAuthorized = false, DenialReason = "blocked" });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand { CommandName = TelegramCommands.Status, RawText = "/status", IsValid = true });

        var evt = harness.MakeCommand(rawCommand: "/status");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue("denial response must fire regardless of audit-DB outage");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        audit.Attempts.Should().Be(1, "the pipeline attempted exactly one rejection-audit write");
    }

    [Fact]
    public async Task Pipeline_AuditWriteFails_FallbackSinkReceivesTheAuditRow()
    {
        // Stage 5.3 iter-9 evaluator item 2 — when the primary
        // IAuditLogger.LogAsync throws, the pipeline MUST enqueue
        // the rejection audit row onto the durable
        // IAuditFallbackSink so the "log every inbound command"
        // contract survives the audit-DB outage. Without this
        // fallback, the prior log-and-swallow shape silently dropped
        // the audit row whenever the audit DB was unavailable.
        var audit = new FailingAuditLogger(new InvalidOperationException("audit-db-down"));
        var fallback = new RecordingAuditFallbackSink();
        var harness = new Harness(audit: audit, auditFallback: fallback);
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult { IsAuthorized = false, DenialReason = "blocked" });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand { CommandName = TelegramCommands.Status, RawText = "/status", IsValid = true });

        var evt = harness.MakeCommand(rawCommand: "/status");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue("denial response still fires even when both audit paths fail");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        audit.Attempts.Should().Be(1, "the pipeline tried the primary writer first");
        fallback.GeneralEntries.Should().HaveCount(1,
            "Stage 5.3 iter-9 evaluator item 2: on primary failure the row MUST land on the durable fallback so the audit trail is preserved");
        var row = fallback.GeneralEntries[0];
        row.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        row.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        row.MessageId.Should().Be(evt.EventId, "the fallback row must carry the same MessageId the primary write attempted");
        row.CorrelationId.Should().Be(evt.CorrelationId);
        row.UserId.Should().Be(evt.UserId);
        AssertDetailsPhase(row, TelegramUpdatePipeline.PipelineDeniedPhases.AuthorizeDenied);
    }

    [Fact]
    public async Task Pipeline_AuditWriteFails_AndFallbackAlsoFails_DenialStillFires()
    {
        // Stage 5.3 iter-9 evaluator item 2 — even when BOTH the
        // primary audit logger AND the durable fallback sink throw,
        // the denial response MUST reach the operator (rejection
        // reply is the security-critical user-facing path). The
        // pipeline logs at Critical so the operator is paged that
        // the audit trail is genuinely lost for this rejection.
        var audit = new FailingAuditLogger(new InvalidOperationException("audit-db-down"));
        var fallback = new FailingAuditFallbackSink(new IOException("disk full"));
        var harness = new Harness(audit: audit, auditFallback: fallback);
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult { IsAuthorized = false, DenialReason = "blocked" });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand { CommandName = TelegramCommands.Status, RawText = "/status", IsValid = true });

        var evt = harness.MakeCommand(rawCommand: "/status");
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue("denial response MUST fire even when both audit tiers fail");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        audit.Attempts.Should().Be(1);
        fallback.Attempts.Should().Be(1,
            "the pipeline must have attempted the fallback after primary failed");
    }

    private static void AssertDetailsPhase(AuditEntry row, string expectedPhase)
    {
        row.Details.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(row.Details!);
        doc.RootElement.GetProperty("phase").GetString().Should().Be(expectedPhase);
    }

    private static void AssertDetailsHasReason(AuditEntry row, string expectedReason)
    {
        row.Details.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(row.Details!);
        doc.RootElement.TryGetProperty("rejectReason", out var reason).Should().BeTrue(
            "the per-site rejection reason must land on the audit Details JSON for forensic review");
        reason.GetString().Should().Be(expectedReason);
    }

    private sealed class Harness
    {
        public Mock<IDeduplicationService> DedupStub { get; }
        public Mock<IUserAuthorizationService> AuthzStub { get; }
        public Mock<ICommandParser> ParserStub { get; }
        public Mock<ICommandRouter> RouterStub { get; }
        public Mock<ICallbackHandler> CallbackStub { get; }
        public Mock<IPendingQuestionStore> PendingStub { get; }
        public InMemoryPendingDisambiguationStore DisambiguationStore { get; }
        public TimeProvider Clock { get; }
        public RecordingAuditLogger Audit { get; }
        public TelegramUpdatePipeline Pipeline { get; }

        public Harness(IAuditLogger? audit = null, IAuditFallbackSink? auditFallback = null)
        {
            DedupStub = new Mock<IDeduplicationService>(MockBehavior.Loose);
            DedupStub.Setup(d => d.TryReserveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            DedupStub.Setup(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            DedupStub.Setup(d => d.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            DedupStub.Setup(d => d.IsProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            AuthzStub = new Mock<IUserAuthorizationService>();
            ParserStub = new Mock<ICommandParser>();
            RouterStub = new Mock<ICommandRouter>();
            CallbackStub = new Mock<ICallbackHandler>();
            PendingStub = new Mock<IPendingQuestionStore>();
            Clock = TimeProvider.System;
            DisambiguationStore = new InMemoryPendingDisambiguationStore(Clock);

            if (audit is RecordingAuditLogger recorder)
            {
                Audit = recorder;
            }
            else if (audit is null)
            {
                Audit = new RecordingAuditLogger();
            }
            else
            {
                // Caller-supplied logger (e.g. FailingAuditLogger);
                // expose an empty recorder so happy-path tests can
                // still read .Entries without crashing.
                Audit = new RecordingAuditLogger();
            }

            Pipeline = new TelegramUpdatePipeline(
                DedupStub.Object,
                AuthzStub.Object,
                ParserStub.Object,
                RouterStub.Object,
                CallbackStub.Object,
                PendingStub.Object,
                DisambiguationStore,
                Clock,
                NullLogger<TelegramUpdatePipeline>.Instance,
                processedEventSink: null,
                audit: audit ?? Audit,
                auditFallback: auditFallback ?? new NullAuditFallbackSink());
        }

        public void AuthorizeWith(params OperatorBinding[] bindings)
        {
            var result = new AuthorizationResult
            {
                IsAuthorized = bindings.Length > 0,
                Bindings = bindings,
            };
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }

        public OperatorBinding MakeBinding(string[]? roles = null, string tenantId = OperatorTenantId) =>
            new()
            {
                Id = Guid.NewGuid(),
                TelegramUserId = 100,
                TelegramChatId = 200,
                ChatType = ChatType.Private,
                OperatorAlias = "@op",
                TenantId = tenantId,
                WorkspaceId = "w-1",
                Roles = roles ?? Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
            };

        public MessengerEvent MakeCommand(string rawCommand) =>
            new()
            {
                EventId = "evt-" + Guid.NewGuid().ToString("N"),
                EventType = EventType.Command,
                RawCommand = rawCommand,
                UserId = OperatorUserIdStr,
                ChatId = "200",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = "trace-" + Guid.NewGuid().ToString("N"),
            };
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        private readonly List<AuditEntry> _entries = new();
        private readonly object _gate = new();

        public IReadOnlyList<AuditEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
            return Task.CompletedTask;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FailingAuditLogger : IAuditLogger
    {
        private readonly Exception _toThrow;
        private int _attempts;

        public FailingAuditLogger(Exception toThrow)
        {
            _toThrow = toThrow;
        }

        public int Attempts => _attempts;

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw _toThrow;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Stage 5.3 iter-9 evaluator item 2 — records every
    /// <see cref="IAuditFallbackSink.EnqueueAsync(AuditEntry, CancellationToken)"/>
    /// call so tests can assert the pipeline routed the dropped
    /// primary write into the durable fallback path.
    /// </summary>
    private sealed class RecordingAuditFallbackSink : IAuditFallbackSink
    {
        private readonly List<AuditEntry> _generalEntries = new();
        private readonly List<HumanResponseAuditEntry> _humanResponseEntries = new();
        private readonly object _gate = new();

        public IReadOnlyList<AuditEntry> GeneralEntries
        {
            get
            {
                lock (_gate)
                {
                    return _generalEntries.ToArray();
                }
            }
        }

        public IReadOnlyList<HumanResponseAuditEntry> HumanResponseEntries
        {
            get
            {
                lock (_gate)
                {
                    return _humanResponseEntries.ToArray();
                }
            }
        }

        public Task EnqueueAsync(AuditEntry entry, CancellationToken ct)
        {
            lock (_gate)
            {
                _generalEntries.Add(entry);
            }
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(HumanResponseAuditEntry entry, CancellationToken ct)
        {
            lock (_gate)
            {
                _humanResponseEntries.Add(entry);
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Pins the both-tiers-fail contract: even when the fallback
    /// sink ALSO throws, the pipeline MUST still return the denial
    /// response (the rejection reply is the security-critical
    /// user-facing path).
    /// </summary>
    private sealed class FailingAuditFallbackSink : IAuditFallbackSink
    {
        private readonly Exception _toThrow;
        private int _attempts;

        public FailingAuditFallbackSink(Exception toThrow)
        {
            _toThrow = toThrow;
        }

        public int Attempts => _attempts;

        public Task EnqueueAsync(AuditEntry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw _toThrow;
        }

        public Task EnqueueAsync(HumanResponseAuditEntry entry, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw _toThrow;
        }
    }
}
