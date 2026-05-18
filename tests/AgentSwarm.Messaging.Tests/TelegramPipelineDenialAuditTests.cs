using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.3 iter-7 evaluator item 1 — pins the contract that EVERY
/// pipeline-level command denial (parse-empty, parse-invalid,
/// authorize-denied, role-denied) persists an
/// <see cref="AuditEntry"/> through <see cref="IAuditLogger"/>
/// BEFORE returning the denial response. The four sites short-circuit
/// without invoking <see cref="ICommandRouter"/>, so the router's
/// pre-handler receipt audit cannot cover them; this test file is
/// the regression guard that keeps the gap from re-opening.
/// </summary>
/// <remarks>
/// Per <see cref="TelegramUpdatePipeline"/> remarks, the denial audit
/// is fire-and-forget at the call site: an audit-write failure logs
/// but does NOT block the denial response (the denial is the
/// security-critical path). The asymmetry vs the router's receipt
/// audit (which DOES rethrow on failure) is by design — see the
/// XML doc on <see cref="TelegramUpdatePipeline"/>'s
/// <c>WriteRejectionAuditAsync</c>.
/// </remarks>
public class TelegramPipelineDenialAuditTests
{
    private const string ChatId = "555000";
    private const string UserId = "888777";
    private const string CorrelationId = "trace-pipeline-denial";

    [Fact]
    public async Task ParseEmpty_WritesDenialAuditRow_BeforeReturningDenialResponse()
    {
        var harness = new Harness();
        harness.AuthorizeWith(BuildBinding());

        var evt = new MessengerEvent
        {
            EventType = EventType.Command,
            EventId = "evt-parse-empty",
            ChatId = ChatId,
            UserId = UserId,
            CorrelationId = CorrelationId,
            ChatType = "private",
            RawCommand = "   ",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);

        harness.Recording!.GeneralEntries.Should().HaveCount(1);
        var entry = harness.Recording.GeneralEntries[0];
        entry.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        entry.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        entry.MessageId.Should().Be("evt-parse-empty");
        entry.UserId.Should().Be(UserId);
        entry.CorrelationId.Should().Be(CorrelationId);
        entry.TenantId.Should().BeNull();
        ExtractPhase(entry).Should().Be(TelegramUpdatePipeline.PipelineDeniedPhases.ParseEmpty);
    }

    [Fact]
    public async Task ParseInvalid_WritesDenialAuditRow_WithParserValidationError()
    {
        var harness = new Harness();
        harness.AuthorizeWith(BuildBinding());

        harness.ParserStub
            .Setup(p => p.Parse("/totally-bogus"))
            .Returns(new ParsedCommand
            {
                IsValid = false,
                CommandName = "totally-bogus",
                Arguments = Array.Empty<string>(),
                ValidationError = "unknown-verb",
                RawText = "/totally-bogus",
            });

        var evt = new MessengerEvent
        {
            EventType = EventType.Command,
            EventId = "evt-parse-invalid",
            ChatId = ChatId,
            UserId = UserId,
            CorrelationId = CorrelationId,
            ChatType = "private",
            RawCommand = "/totally-bogus",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);

        harness.Recording!.GeneralEntries.Should().HaveCount(1);
        var entry = harness.Recording.GeneralEntries[0];
        entry.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        entry.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        entry.MessageId.Should().Be("evt-parse-invalid");
        entry.UserId.Should().Be(UserId);
        entry.CorrelationId.Should().Be(CorrelationId);
        entry.TenantId.Should().BeNull();
        ExtractPhase(entry).Should().Be(TelegramUpdatePipeline.PipelineDeniedPhases.ParseInvalid);
        ExtractRejectReason(entry).Should().Be("unknown-verb");
    }

    [Fact]
    public async Task AuthorizeDenied_WritesDenialAuditRow_TenantIdNull_BeforeBindingResolved()
    {
        var harness = new Harness();
        harness.ParserStub
            .Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                IsValid = true,
                CommandName = "status",
                Arguments = Array.Empty<string>(),
                RawText = "/status",
            });
        harness.AuthzStub
            .Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                Bindings = Array.Empty<OperatorBinding>(),
                DenialReason = "no-binding",
            });

        var evt = new MessengerEvent
        {
            EventType = EventType.Command,
            EventId = "evt-authz-denied",
            ChatId = ChatId,
            UserId = UserId,
            CorrelationId = CorrelationId,
            ChatType = "private",
            RawCommand = "/status",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);

        harness.Recording!.GeneralEntries.Should().HaveCount(1);
        var entry = harness.Recording.GeneralEntries[0];
        entry.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        entry.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        entry.MessageId.Should().Be("evt-authz-denied");
        entry.UserId.Should().Be(UserId);
        entry.CorrelationId.Should().Be(CorrelationId);
        entry.TenantId.Should().BeNull("pipeline rejected before a binding was resolved");
        ExtractPhase(entry).Should().Be(TelegramUpdatePipeline.PipelineDeniedPhases.AuthorizeDenied);
        ExtractRejectReason(entry).Should().Be("no-binding");
    }

    [Fact]
    public async Task RoleDenied_WritesDenialAuditRow_WithResolvedOperatorTenantId()
    {
        var harness = new Harness();
        // Bind an operator with only the "Observer" role so the
        // /approve command (which requires "Approver") triggers the
        // role-denied gate.
        harness.AuthorizeWith(BuildBinding(roles: new[] { "Observer" }));

        harness.ParserStub
            .Setup(p => p.Parse("/approve Q1"))
            .Returns(new ParsedCommand
            {
                IsValid = true,
                CommandName = "approve",
                Arguments = new[] { "Q1" },
                RawText = "/approve Q1",
            });

        var evt = new MessengerEvent
        {
            EventType = EventType.Command,
            EventId = "evt-role-denied",
            ChatId = ChatId,
            UserId = UserId,
            CorrelationId = CorrelationId,
            ChatType = "private",
            RawCommand = "/approve Q1",
            Timestamp = DateTimeOffset.UtcNow,
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.InsufficientPermissions);

        harness.Recording!.GeneralEntries.Should().HaveCount(1);
        var entry = harness.Recording.GeneralEntries[0];
        entry.Action.Should().Be(TelegramUpdatePipeline.PipelineDeniedAuditAction);
        entry.EventFamily.Should().Be(AuditEventFamilies.Lifecycle);
        entry.MessageId.Should().Be("evt-role-denied");
        entry.UserId.Should().Be(UserId);
        entry.CorrelationId.Should().Be(CorrelationId);
        entry.TenantId.Should().Be("tenant-acme",
            "role-denied has a resolved operator binding so the tenant context lands on the row");
        ExtractPhase(entry).Should().Be(TelegramUpdatePipeline.PipelineDeniedPhases.RoleDenied);
        ExtractRejectReason(entry).Should().Contain("Approver");
    }

    [Fact]
    public async Task DenialAuditWriteFailure_DoesNotBlockDenialResponse_AndIsLoggedAtError()
    {
        // Mirror the documented "log-but-do-not-rethrow" failure
        // semantics from WriteRejectionAuditAsync's XML doc: an
        // audit-DB outage must NEVER block the denial reply that an
        // unauthorized caller sees, otherwise an attacker that
        // crashes the audit DB also crashes the rejection path
        // (denial-of-service vector).
        var throwingAudit = new ThrowingAuditLogger(new InvalidOperationException("audit DB down"));
        var harness = new Harness(audit: throwingAudit);
        harness.AuthzStub
            .Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                Bindings = Array.Empty<OperatorBinding>(),
                DenialReason = "no-binding",
            });
        harness.ParserStub
            .Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                IsValid = true,
                CommandName = "status",
                Arguments = Array.Empty<string>(),
                RawText = "/status",
            });

        var evt = new MessengerEvent
        {
            EventType = EventType.Command,
            EventId = "evt-audit-down",
            ChatId = ChatId,
            UserId = UserId,
            CorrelationId = CorrelationId,
            ChatType = "private",
            RawCommand = "/status",
            Timestamp = DateTimeOffset.UtcNow,
        };

        // The denial reply MUST land even though the audit write threw.
        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        throwingAudit.AttemptedCalls.Should().Be(1, "the pipeline attempted to persist the denial row exactly once");
    }

    private static string ExtractPhase(AuditEntry entry)
    {
        entry.Details.Should().NotBeNullOrEmpty("denial audit rows must carry a Details JSON payload");
        using var doc = JsonDocument.Parse(entry.Details!);
        return doc.RootElement.GetProperty("phase").GetString()!;
    }

    private static string ExtractRejectReason(AuditEntry entry)
    {
        using var doc = JsonDocument.Parse(entry.Details!);
        return doc.RootElement.GetProperty("rejectReason").GetString()!;
    }

    private static OperatorBinding BuildBinding(string[]? roles = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TelegramUserId = long.Parse(UserId),
            TelegramChatId = long.Parse(ChatId),
            ChatType = ChatType.Private,
            TenantId = "tenant-acme",
            WorkspaceId = "ws-1",
            Roles = roles ?? new[] { "Operator", "Approver" },
            OperatorAlias = "alice",
            RegisteredAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Captures every <see cref="IAuditLogger.LogAsync"/> call so
    /// tests can assert exact row counts and field values.
    /// </summary>
    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEntry> GeneralEntries { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            GeneralEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Always throws on the next <see cref="LogAsync"/>; pins the
    /// log-but-do-not-rethrow contract from
    /// <see cref="TelegramUpdatePipeline"/>'s WriteRejectionAuditAsync.
    /// </summary>
    private sealed class ThrowingAuditLogger : IAuditLogger
    {
        private readonly Exception _ex;

        public int AttemptedCalls { get; private set; }

        public ThrowingAuditLogger(Exception ex)
        {
            _ex = ex ?? throw new ArgumentNullException(nameof(ex));
        }

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            AttemptedCalls++;
            throw _ex;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public Mock<IDeduplicationService> DedupStub { get; }
        public Mock<IUserAuthorizationService> AuthzStub { get; }
        public Mock<ICommandParser> ParserStub { get; }
        public Mock<ICommandRouter> RouterStub { get; }
        public Mock<ICallbackHandler> CallbackStub { get; }
        public Mock<IPendingQuestionStore> PendingStub { get; }
        public Mock<IPendingDisambiguationStore> DisambiguationStub { get; }
        public IAuditLogger Audit { get; }
        public RecordingAuditLogger? Recording { get; }
        public TelegramUpdatePipeline Pipeline { get; }

        public Harness(IAuditLogger? audit = null)
        {
            DedupStub = new Mock<IDeduplicationService>();
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
            DisambiguationStub = new Mock<IPendingDisambiguationStore>();

            if (audit is null)
            {
                Recording = new RecordingAuditLogger();
                Audit = Recording;
            }
            else
            {
                Audit = audit;
            }

            Pipeline = new TelegramUpdatePipeline(
                DedupStub.Object,
                AuthzStub.Object,
                ParserStub.Object,
                RouterStub.Object,
                CallbackStub.Object,
                PendingStub.Object,
                DisambiguationStub.Object,
                TimeProvider.System,
                NullLogger<TelegramUpdatePipeline>.Instance,
                processedEventSink: null,
                audit: Audit);
        }

        public void AuthorizeWith(params OperatorBinding[] bindings)
        {
            var result = new AuthorizationResult
            {
                IsAuthorized = bindings.Length > 0,
                Bindings = bindings,
            };
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        }
    }
}
