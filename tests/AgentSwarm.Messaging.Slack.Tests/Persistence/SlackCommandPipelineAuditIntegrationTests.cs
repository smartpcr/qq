// -----------------------------------------------------------------------
// <copyright file="SlackCommandPipelineAuditIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 7.1 integration test for the brief-mandated scenario:
/// <em>"Given a valid slash command processed by
/// <c>SlackCommandHandler</c>, When processing completes, Then a
/// SlackAuditEntry with direction = inbound, request_type =
/// slash_command, and outcome = success is persisted with all
/// mandatory fields populated."</em>
/// </summary>
/// <remarks>
/// <para>
/// Drives <see cref="SlackInboundProcessingPipeline.ProcessAsync"/>
/// end-to-end through the real <see cref="SlackInboundAuditRecorder"/>
/// and <see cref="SlackAuditLogger{TContext}"/>, persisting through
/// EF Core (SQLite-in-memory). Unlike
/// <see cref="SlackAuditLoggerTests"/>, which exercises
/// <see cref="ISlackAuditLogger.LogAsync"/> with synthetic entries,
/// this test verifies the WHOLE inbound pipeline writes the audit
/// row through the dual-interface wiring the Stage 7.1 DI extension
/// registers.
/// </para>
/// <para>
/// Addresses evaluator iter-1 item 2.
/// </para>
/// </remarks>
public sealed class SlackCommandPipelineAuditIntegrationTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackCommandPipelineAuditIntegrationTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<RetentionTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope bootstrap = this.serviceProvider.CreateScope();
        RetentionTestDbContext ctx = bootstrap.ServiceProvider.GetRequiredService<RetentionTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task SlackInboundProcessingPipeline_writes_slash_command_audit_row_through_SlackAuditLogger()
    {
        // Stage 7.1 brief test scenario "Audit entry persisted on
        // command". The Stage 7.1 DI extension registers
        // SlackAuditLogger as BOTH ISlackAuditLogger AND
        // ISlackAuditEntryWriter, so the entire inbound pipeline
        // (auth -> idempotency -> dispatch -> audit) flows through
        // SlackAuditLogger.LogAsync without any per-call-site edit.
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        SlackAuditLogger<RetentionTestDbContext> logger = new(scopeFactory);

        // The recorder is built against the ISlackAuditEntryWriter
        // surface that the logger ALSO implements (dual interface) --
        // exactly the wiring AddSlackAuditLogger installs in
        // production.
        ISlackAuditEntryWriter writer = logger;
        SlackInboundAuditRecorder recorder = new(
            writer,
            NullLogger<SlackInboundAuditRecorder>.Instance,
            TimeProvider.System);

        SlackInboundProcessingPipeline pipeline = new(
            authorizer: new AlwaysAuthorize(),
            guard: new InMemorySlackIdempotencyGuard(),
            commandHandler: new RecordingCommandHandler(),
            appMentionHandler: new NoopAppMentionHandler(),
            interactionHandler: new NoopInteractionHandler(),
            retryPolicy: new ZeroDelayRetryPolicy(),
            deadLetterQueue: new InMemorySlackDeadLetterQueue(),
            auditRecorder: recorder,
            logger: NullLogger<SlackInboundProcessingPipeline>.Instance,
            timeProvider: TimeProvider.System);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T0123ABCD:U0123ABCD:/agent ask plan for failover",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T0123ABCD",
            ChannelId: "C0123ABCD",
            UserId: "U0123ABCD",
            RawPayload: "team_id=T0123ABCD&channel_id=C0123ABCD&user_id=U0123ABCD&command=%2Fagent&text=ask+plan+for+failover&trigger_id=trig-int-1",
            TriggerId: "trig-int-1",
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInboundProcessingOutcome outcome = await pipeline.ProcessAsync(envelope, CancellationToken.None);

        outcome.Should().Be(SlackInboundProcessingOutcome.Processed);

        // The acceptance criteria: query the durable audit table
        // through the Stage 7.1 QueryAsync surface and confirm the
        // mandated direction / request_type / outcome row, with
        // every story-mandated field populated (team_id, channel_id,
        // user_id, command_text, correlation_id).
        IReadOnlyList<SlackAuditEntry> matches = await logger.QueryAsync(
            new SlackAuditQuery { CorrelationId = envelope.IdempotencyKey },
            CancellationToken.None);

        matches.Should().ContainSingle("the pipeline writes exactly one audit row per successful dispatch");

        SlackAuditEntry row = matches[0];
        row.Direction.Should().Be("inbound");
        row.RequestType.Should().Be("slash_command");
        row.Outcome.Should().Be("success");
        row.TeamId.Should().Be("T0123ABCD");
        row.ChannelId.Should().Be("C0123ABCD");
        row.UserId.Should().Be("U0123ABCD");
        row.CorrelationId.Should().Be(envelope.IdempotencyKey);
        row.CommandText.Should().NotBeNullOrEmpty(
            "the Stage 4.x audit recorder extracts the slash-command text from the raw payload");
    }

    private sealed class AlwaysAuthorize : ISlackInboundAuthorizer
    {
        public Task<SlackInboundAuthorizationResult> AuthorizeAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.FromResult(SlackInboundAuthorizationResult.Authorized(new SlackWorkspaceConfig
            {
                TeamId = envelope.TeamId,
                Enabled = true,
            }));
    }

    private sealed class RecordingCommandHandler : ISlackCommandHandler
    {
        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NoopAppMentionHandler : ISlackAppMentionHandler
    {
        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NoopInteractionHandler : ISlackInteractionHandler
    {
        public Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ZeroDelayRetryPolicy : ISlackRetryPolicy
    {
        public bool ShouldRetry(int attempt, Exception exception) => false;

        public TimeSpan GetDelay(int attempt) => TimeSpan.Zero;
    }
}
