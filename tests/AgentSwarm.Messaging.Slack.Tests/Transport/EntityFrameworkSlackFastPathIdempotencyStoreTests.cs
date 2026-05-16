// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackFastPathIdempotencyStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.1 iter-3 evaluator item 2 regression tests for
/// <see cref="EntityFrameworkSlackFastPathIdempotencyStore{TContext}"/>.
/// Drives the store against an in-memory SQLite-backed
/// <see cref="SlackTestDbContext"/> so the production
/// <c>slack_inbound_request_record</c> schema is exercised.
/// </summary>
public sealed class EntityFrameworkSlackFastPathIdempotencyStoreTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public EntityFrameworkSlackFastPathIdempotencyStoreTests()
    {
        this.connection = new SqliteConnection("DataSource=:memory:");
        this.connection.Open();

        ServiceCollection services = new();
        services.AddDbContext<SlackTestDbContext>(opts => opts.UseSqlite(this.connection));
        this.serviceProvider = services.BuildServiceProvider();

        using IServiceScope bootstrap = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = bootstrap.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        this.serviceProvider.Dispose();
        this.connection.Dispose();
    }

    [Fact]
    public async Task TryAcquireAsync_inserts_a_reserved_record_and_returns_Acquired_on_first_call()
    {
        EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> store = this.BuildStore();
        SlackInboundEnvelope envelope = BuildEnvelope(idempotencyKey: "cmd:T1:U1:/agent:trig-1");

        SlackFastPathIdempotencyResult result = await store
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);

        result.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Acquired);
        result.ShouldProceed.Should().BeTrue();

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        ctx.InboundRequests.Should().HaveCount(1);
        ctx.InboundRequests.Single().ProcessingStatus.Should().Be(
            EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>.ProcessingStatusReserved,
            "the row is reserved during the views.open round-trip");
    }

    [Fact]
    public async Task TryAcquireAsync_second_call_with_same_key_returns_Duplicate_and_does_not_insert_again()
    {
        EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> store = this.BuildStore();
        SlackInboundEnvelope envelope = BuildEnvelope(idempotencyKey: "cmd:T1:U1:/agent:trig-1");

        SlackFastPathIdempotencyResult first = await store
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);
        first.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Acquired);

        SlackFastPathIdempotencyResult second = await store
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);
        second.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.Duplicate,
            "the durable row exists -- a Slack retry that crosses replicas must be rejected before views.open is called twice");
        second.Diagnostic.Should().NotBeNullOrWhiteSpace();

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        ctx.InboundRequests.Should().HaveCount(1, "only one row exists per idempotency_key");
    }

    [Fact]
    public async Task MarkOpenedAsync_flips_processing_status_to_modal_opened_and_stamps_completed_at()
    {
        EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> store = this.BuildStore();
        SlackInboundEnvelope envelope = BuildEnvelope(idempotencyKey: "cmd:T1:U1:/agent:trig-1");

        await store.TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);
        await store.MarkOpenedAsync(envelope.IdempotencyKey, CancellationToken.None);

        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        AgentSwarm.Messaging.Slack.Entities.SlackInboundRequestRecord row = ctx.InboundRequests.Single();
        row.ProcessingStatus.Should().Be(
            EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>.ProcessingStatusModalOpened);
        row.CompletedAt.Should().NotBeNull("the timestamp is needed for retention pruning and audit");
    }

    [Fact]
    public async Task ReleaseAsync_only_deletes_rows_that_are_still_in_reserved_state()
    {
        EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> store = this.BuildStore();
        SlackInboundEnvelope envelope = BuildEnvelope(idempotencyKey: "cmd:T1:U1:/agent:trig-1");

        // Reserve, then release -- row should be removed so a retry can proceed.
        await store.TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);
        await store.ReleaseAsync(envelope.IdempotencyKey, CancellationToken.None);

        using (IServiceScope scope1 = this.serviceProvider.CreateScope())
        {
            scope1.ServiceProvider.GetRequiredService<SlackTestDbContext>()
                .InboundRequests.Should().BeEmpty("the reserved row was released before views.open completed");
        }

        // Re-acquire + mark opened: subsequent Release must NOT delete the
        // terminal record (otherwise duplicate retries after success would
        // open a second modal).
        await store.TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);
        await store.MarkOpenedAsync(envelope.IdempotencyKey, CancellationToken.None);
        await store.ReleaseAsync(envelope.IdempotencyKey, CancellationToken.None);

        using IServiceScope scope2 = this.serviceProvider.CreateScope();
        scope2.ServiceProvider.GetRequiredService<SlackTestDbContext>()
            .InboundRequests.Single().ProcessingStatus.Should().Be(
                EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>.ProcessingStatusModalOpened,
                "Release MUST be a no-op against terminal rows so dedup state survives the success path");
    }

    [Fact]
    public async Task TryAcquireAsync_returns_StoreUnavailable_when_the_underlying_db_is_dead()
    {
        // Build a separate store + provider whose SQLite connection is
        // closed immediately, so every EF call throws InvalidOperationException.
        SqliteConnection dead = new("DataSource=:memory:");
        dead.Open();
        ServiceCollection services = new();
        services.AddDbContext<SlackTestDbContext>(opts => opts.UseSqlite(dead));
        using ServiceProvider provider = services.BuildServiceProvider();

        using (IServiceScope bootstrap = provider.CreateScope())
        {
            bootstrap.ServiceProvider.GetRequiredService<SlackTestDbContext>().Database.EnsureCreated();
        }

        dead.Close();
        dead.Dispose();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> store = new(
            scopeFactory,
            NullLogger<EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>>.Instance);

        SlackInboundEnvelope envelope = BuildEnvelope(idempotencyKey: "cmd:T1:U1:/agent:trig-dead");
        SlackFastPathIdempotencyResult result = await store
            .TryAcquireAsync(envelope.IdempotencyKey, envelope, lifetime: null, ct: CancellationToken.None);

        result.Outcome.Should().Be(SlackFastPathIdempotencyOutcome.StoreUnavailable,
            "transient DB failures must degrade gracefully so a DB blip does not break every modal command");
        result.Diagnostic.Should().NotBeNullOrWhiteSpace();
        result.ShouldProceed.Should().BeTrue(
            "the handler proceeds with L1-only dedup so the user gets their modal");
    }

    private EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext> BuildStore()
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>(
            scopeFactory,
            NullLogger<EntityFrameworkSlackFastPathIdempotencyStore<SlackTestDbContext>>.Instance);
    }

    private static SlackInboundEnvelope BuildEnvelope(string idempotencyKey)
    {
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C1",
            UserId: "U1",
            RawPayload: "team_id=T1&user_id=U1&command=/agent&trigger_id=trig",
            TriggerId: "trig",
            ReceivedAt: DateTimeOffset.UtcNow);
    }
}
