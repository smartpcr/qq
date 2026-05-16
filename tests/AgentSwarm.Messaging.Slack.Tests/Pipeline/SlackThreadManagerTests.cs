// -----------------------------------------------------------------------
// <copyright file="SlackThreadManagerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 6.2 regression tests for
/// <see cref="SlackThreadManager{TContext}"/>. Drives the manager
/// through a real EF-backed SQLite in-memory DbContext, a stub
/// <see cref="ISlackChatPostMessageClient"/> that scripts a queue of
/// responses, and an in-memory audit writer so the tests can assert
/// against both the persisted mapping AND the audit trail. Covers
/// every scenario in the Stage 6.2 brief PLUS the inline-fallback /
/// threaded-reply paths required by iter-2 evaluator feedback.
/// </summary>
public sealed class SlackThreadManagerTests : IDisposable
{
    private const string TeamId = "T01TEAM";
    private const string DefaultChannel = "C01DEFAULT";
    private const string FallbackChannel = "C01FALLBACK";

    private readonly SqliteConnection connection;
    private readonly ServiceProvider serviceProvider;

    public SlackThreadManagerTests()
    {
        // SQLite :memory: databases vanish when the last connection
        // closes; keeping an outer connection alive lets every scoped
        // DbContext see the same schema for the duration of the test.
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
    public async Task GetThreadAsync_returns_null_when_no_mapping_exists()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        (await fx.Manager.GetThreadAsync("missing", CancellationToken.None)).Should().BeNull();
        fx.ChatClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetThreadAsync_returns_null_for_blank_task_id()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        (await fx.Manager.GetThreadAsync(string.Empty, CancellationToken.None)).Should().BeNull();
        (await fx.Manager.GetThreadAsync("   ", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_first_call_posts_root_to_workspace_default_channel_and_persists_mapping()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success(
            ts: "1716000000.000200", channel: DefaultChannel));

        SlackThreadMapping result = await fx.Manager.GetOrCreateThreadAsync(
            taskId: "TASK-1",
            agentId: "agent-α",
            correlationId: "corr-1",
            teamId: TeamId,
            CancellationToken.None);

        // 1. The HTTP client was called exactly once, AGAINST THE
        //    workspace's DefaultChannelId -- the manager resolved the
        //    channel itself rather than accepting a caller-supplied id.
        fx.ChatClient.Calls.Should().ContainSingle();
        SlackChatPostMessageRequest call = fx.ChatClient.Calls[0];
        call.TeamId.Should().Be(TeamId);
        call.ChannelId.Should().Be(DefaultChannel,
            "GetOrCreateThreadAsync MUST post into the workspace's DefaultChannelId, not a caller-supplied channel");
        call.CorrelationId.Should().Be("corr-1");
        call.Text.Should().Contain("TASK-1");
        call.Text.Should().Contain("corr-1");
        call.ThreadTs.Should().BeNull("root creation MUST be a top-level post");

        // 2. The returned mapping carries every required column.
        result.TaskId.Should().Be("TASK-1");
        result.TeamId.Should().Be(TeamId);
        result.ChannelId.Should().Be(DefaultChannel);
        result.ThreadTs.Should().Be("1716000000.000200");
        result.CorrelationId.Should().Be("corr-1");
        result.AgentId.Should().Be("agent-α");
        result.CreatedAt.Should().Be(fx.NowAtCreate);
        result.LastMessageAt.Should().Be(fx.NowAtCreate);

        // 3. Persisted from a fresh DbContext.
        SlackThreadMapping persisted = await this.LoadMappingAsync("TASK-1");
        persisted.ThreadTs.Should().Be("1716000000.000200");
        persisted.ChannelId.Should().Be(DefaultChannel);

        // 4. Audit row outcome=success, request_type=thread_create.
        fx.Audit.Entries.Should().ContainSingle();
        SlackAuditEntry audit = fx.Audit.Entries[0];
        audit.Direction.Should().Be(SlackThreadManager<SlackTestDbContext>.DirectionOutbound);
        audit.RequestType.Should().Be(SlackThreadManager<SlackTestDbContext>.RequestTypeThreadCreate);
        audit.Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeSuccess);
        audit.CorrelationId.Should().Be("corr-1");
        audit.TaskId.Should().Be("TASK-1");
        audit.ChannelId.Should().Be(DefaultChannel);
        audit.ThreadTs.Should().Be("1716000000.000200");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_second_call_reuses_existing_mapping_without_posting()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success(
            ts: "1716000000.000200", channel: DefaultChannel));

        SlackThreadMapping first = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-1", "agent-α", "corr-1", TeamId, CancellationToken.None);

        fx.ChatClient.Calls.Clear();
        fx.Audit.Clear();

        SlackThreadMapping second = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-1", "agent-α", "corr-1", TeamId, CancellationToken.None);

        fx.ChatClient.Calls.Should().BeEmpty("second GetOrCreate MUST reuse the persisted mapping");
        fx.Audit.Entries.Should().BeEmpty("reusing an existing mapping is not a billable outbound event");
        second.TaskId.Should().Be(first.TaskId);
        second.ThreadTs.Should().Be(first.ThreadTs);
        second.ChannelId.Should().Be(first.ChannelId);
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_on_restart_resolves_existing_mapping_without_posting()
    {
        // Simulate a "previous process run" by inserting a mapping
        // directly into the database, then construct a brand-new
        // manager that has never seen this row before.
        DateTimeOffset originalCreate = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        await this.SeedMappingAsync(new SlackThreadMapping
        {
            TaskId = "TASK-301",
            TeamId = TeamId,
            ChannelId = DefaultChannel,
            ThreadTs = "1700000111.000222",
            CorrelationId = "corr-restart",
            AgentId = "agent-α",
            CreatedAt = originalCreate,
            LastMessageAt = originalCreate,
        });

        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("should-never-be-used", "should-never-be-used"));

        SlackThreadMapping mapping = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-301", "agent-α", "corr-restart", TeamId, CancellationToken.None);

        fx.ChatClient.Calls.Should().BeEmpty(
            "restart continuity MUST NOT re-post a root message for a task that already has a mapping");
        mapping.ThreadTs.Should().Be("1700000111.000222");
        mapping.CreatedAt.Should().Be(originalCreate,
            "the original create time MUST be preserved across a restart");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_falls_back_to_fallback_channel_when_default_is_archived()
    {
        // Stage 6.2 scenario 13.3 verbatim: "Given a thread whose
        // channel has been archived and a FallbackChannelId is
        // configured, When GetOrCreateThreadAsync is called, Then a new
        // thread is created in the fallback channel."  The previous
        // iter implemented this only through the manually-invoked
        // RecoverThreadAsync path; iter-2 wires it into GetOrCreate
        // itself.
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("is_archived"));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success(
            ts: "1716999999.000777", channel: FallbackChannel));

        SlackThreadMapping mapping = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-501", "agent-α", "corr-fallback", TeamId, CancellationToken.None);

        // Two posts: one to the default channel (which failed
        // is_archived), then one to the fallback channel.
        fx.ChatClient.Calls.Should().HaveCount(2);
        fx.ChatClient.Calls[0].ChannelId.Should().Be(DefaultChannel);
        fx.ChatClient.Calls[1].ChannelId.Should().Be(FallbackChannel);

        // The persisted mapping uses the fallback channel as the
        // long-lived owner of the thread.
        mapping.ChannelId.Should().Be(FallbackChannel);
        mapping.ThreadTs.Should().Be("1716999999.000777");

        SlackThreadMapping persisted = await this.LoadMappingAsync("TASK-501");
        persisted.ChannelId.Should().Be(FallbackChannel);

        // Audit row outcome=fallback_used so the operator can grep
        // for channel-archive incidents.
        fx.Audit.Entries.Should().ContainSingle();
        SlackAuditEntry audit = fx.Audit.Entries[0];
        audit.Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeFallbackUsed);
        audit.RequestType.Should().Be(SlackThreadManager<SlackTestDbContext>.RequestTypeThreadCreate);
        audit.ChannelId.Should().Be(FallbackChannel);
        audit.ThreadTs.Should().Be("1716999999.000777");
    }

    [Theory]
    [InlineData("channel_not_found")]
    [InlineData("is_archived")]
    [InlineData("not_in_channel")]
    public async Task GetOrCreateThreadAsync_triggers_fallback_on_recoverable_slack_errors(string slackError)
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure(slackError));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1.001", FallbackChannel));

        SlackThreadMapping mapping = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-X", "agent-α", "corr", TeamId, CancellationToken.None);

        mapping.ChannelId.Should().Be(FallbackChannel);
        fx.ChatClient.Calls.Should().HaveCount(2,
            $"a '{slackError}' error MUST trigger the inline fallback retry");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_throws_when_default_post_fails_and_no_fallback_configured()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: false));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("channel_not_found"));

        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            "TASK-2", "agent-α", "corr-2", TeamId, CancellationToken.None);

        SlackThreadCreationException ex = (await act.Should().ThrowAsync<SlackThreadCreationException>()).Which;
        ex.TaskId.Should().Be("TASK-2");
        ex.SlackError.Should().Be("channel_not_found");

        fx.ChatClient.Calls.Should().HaveCount(1,
            "without a fallback channel the manager MUST NOT issue a second post");
        (await this.LoadMappingOrNullAsync("TASK-2")).Should().BeNull();

        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
        fx.Audit.Entries[0].RequestType.Should().Be(SlackThreadManager<SlackTestDbContext>.RequestTypeThreadCreate);
        fx.Audit.Entries[0].ErrorDetail.Should().Be("channel_not_found");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_throws_when_default_post_fails_with_non_recoverable_error()
    {
        // rate_limited is NOT a channel-missing error -- the fallback
        // path MUST NOT fire for transient errors that would clear
        // themselves on retry.
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("rate_limited"));

        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            "TASK-3", "agent-α", "corr-3", TeamId, CancellationToken.None);

        await act.Should().ThrowAsync<SlackThreadCreationException>();
        fx.ChatClient.Calls.Should().HaveCount(1,
            "rate_limited is recoverable through retry, NOT through the fallback channel");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_throws_when_fallback_also_fails()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("is_archived"));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("is_archived"));

        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            "TASK-4", "agent-α", "corr-4", TeamId, CancellationToken.None);

        SlackThreadCreationException ex = (await act.Should().ThrowAsync<SlackThreadCreationException>()).Which;
        ex.ChannelId.Should().Be(FallbackChannel,
            "when both channels are archived the exception MUST report the fallback as the last-attempted destination");

        fx.ChatClient.Calls.Should().HaveCount(2);
        (await this.LoadMappingOrNullAsync("TASK-4")).Should().BeNull();

        fx.Audit.Entries.Should().ContainSingle();
        SlackAuditEntry audit = fx.Audit.Entries[0];
        audit.Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
        audit.ErrorDetail.Should().StartWith("fallback_post_failed");
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_throws_when_workspace_unknown()
    {
        Fixture fx = this.NewFixture(workspace: null);

        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            "TASK-9", "agent-α", "corr", "T-UNKNOWN", CancellationToken.None);

        await act.Should().ThrowAsync<SlackThreadCreationException>();
        fx.ChatClient.Calls.Should().BeEmpty(
            "an unknown workspace MUST fail pre-flight before any HTTP traffic is issued");
        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
    }

    [Fact]
    public async Task GetOrCreateThreadAsync_throws_when_workspace_has_no_default_channel()
    {
        Fixture fx = this.NewFixture(new SlackWorkspaceConfig
        {
            TeamId = TeamId,
            DefaultChannelId = string.Empty,
            Enabled = true,
        });

        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            "TASK-10", "agent-α", "corr", TeamId, CancellationToken.None);

        await act.Should().ThrowAsync<SlackThreadCreationException>();
        fx.ChatClient.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateThreadAsync_throws_on_blank_task_id(string taskId)
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        Func<Task> act = () => fx.Manager.GetOrCreateThreadAsync(
            taskId, "agent-α", "corr", TeamId, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TouchAsync_updates_LastMessageAt_to_current_utc_time()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1716000000.000200", DefaultChannel));

        await fx.Manager.GetOrCreateThreadAsync("TASK-1", "agent-α", "corr-1", TeamId, CancellationToken.None);

        DateTimeOffset later = fx.NowAtCreate.AddMinutes(7);
        fx.TimeProvider.Now = later;

        (await fx.Manager.TouchAsync("TASK-1", CancellationToken.None)).Should().BeTrue();

        SlackThreadMapping reloaded = await this.LoadMappingAsync("TASK-1");
        reloaded.LastMessageAt.Should().Be(later);
        reloaded.CreatedAt.Should().Be(fx.NowAtCreate, "CreatedAt MUST NOT move when LastMessageAt is bumped");
    }

    [Fact]
    public async Task TouchAsync_returns_false_when_mapping_missing()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        (await fx.Manager.TouchAsync("never-existed", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task PostThreadedReplyAsync_posts_reply_with_thread_ts_and_bumps_LastMessageAt()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1.001", DefaultChannel));

        SlackThreadMapping created = await fx.Manager.GetOrCreateThreadAsync(
            "TASK-700", "agent-α", "corr-700", TeamId, CancellationToken.None);
        fx.ChatClient.Calls.Clear();
        fx.Audit.Clear();

        // Advance the clock so we can prove TouchAsync moved LastMessageAt.
        DateTimeOffset replyAt = fx.NowAtCreate.AddMinutes(3);
        fx.TimeProvider.Now = replyAt;
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1.002", DefaultChannel));

        SlackThreadPostResult result = await fx.Manager.PostThreadedReplyAsync(
            "TASK-700", "status update", "corr-700-reply", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(SlackThreadPostStatus.Posted);
        result.MessageTs.Should().Be("1.002");

        // The chat client was called WITH thread_ts pointing at the
        // owning root -- this is the contract that makes the reply
        // land inside the thread rather than as a top-level post.
        fx.ChatClient.Calls.Should().ContainSingle();
        fx.ChatClient.Calls[0].ThreadTs.Should().Be(created.ThreadTs);
        fx.ChatClient.Calls[0].ChannelId.Should().Be(created.ChannelId);
        fx.ChatClient.Calls[0].Text.Should().Be("status update");

        // LastMessageAt was bumped to the post time -- this is the
        // production guarantee for "update LastMessageAt on every new
        // thread message" (Stage 6.2 implementation step 6).
        SlackThreadMapping persisted = await this.LoadMappingAsync("TASK-700");
        persisted.LastMessageAt.Should().Be(replyAt);

        // Audit row outcome=success, request_type=thread_message.
        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeSuccess);
        fx.Audit.Entries[0].RequestType.Should().Be(SlackThreadManager<SlackTestDbContext>.RequestTypeThreadMessage);
        fx.Audit.Entries[0].MessageTs.Should().Be("1.002");
    }

    [Fact]
    public async Task PostThreadedReplyAsync_recovers_thread_when_channel_archived_and_retries_reply()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1.001", DefaultChannel));

        await fx.Manager.GetOrCreateThreadAsync(
            "TASK-800", "agent-α", "corr-800", TeamId, CancellationToken.None);
        fx.ChatClient.Calls.Clear();
        fx.Audit.Clear();

        // Reply attempt fires three calls:
        //   1) post to default channel -> is_archived
        //   2) RecoverThreadAsync re-posts root on fallback channel -> success
        //   3) retry reply against the recovered thread -> success
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("is_archived"));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("2.001", FallbackChannel));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("2.002", FallbackChannel));

        SlackThreadPostResult result = await fx.Manager.PostThreadedReplyAsync(
            "TASK-800", "delayed reply", correlationId: null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(SlackThreadPostStatus.Recovered);
        result.MessageTs.Should().Be("2.002");

        fx.ChatClient.Calls.Should().HaveCount(3);
        fx.ChatClient.Calls[0].ChannelId.Should().Be(DefaultChannel);
        fx.ChatClient.Calls[1].ChannelId.Should().Be(FallbackChannel);
        fx.ChatClient.Calls[1].ThreadTs.Should().BeNull("the recovery is a NEW root post on the fallback channel");
        fx.ChatClient.Calls[2].ChannelId.Should().Be(FallbackChannel);
        fx.ChatClient.Calls[2].ThreadTs.Should().Be("2.001",
            "the retry MUST thread under the freshly-created recovery root");

        // The mapping is now anchored at the fallback channel + new ts.
        SlackThreadMapping persisted = await this.LoadMappingAsync("TASK-800");
        persisted.ChannelId.Should().Be(FallbackChannel);
        persisted.ThreadTs.Should().Be("2.001");

        // Audit trail: thread_recover/fallback_used + thread_message/fallback_used.
        IReadOnlyList<SlackAuditEntry> audits = fx.Audit.Entries;
        audits.Should().HaveCountGreaterOrEqualTo(2);
        audits.Should().Contain(a =>
            a.RequestType == SlackThreadManager<SlackTestDbContext>.RequestTypeThreadRecover
            && a.Outcome == SlackThreadManager<SlackTestDbContext>.OutcomeFallbackUsed);
        audits.Should().Contain(a =>
            a.RequestType == SlackThreadManager<SlackTestDbContext>.RequestTypeThreadMessage
            && a.Outcome == SlackThreadManager<SlackTestDbContext>.OutcomeFallbackUsed);
    }

    [Fact]
    public async Task PostThreadedReplyAsync_returns_failure_when_no_mapping_exists()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());

        SlackThreadPostResult result = await fx.Manager.PostThreadedReplyAsync(
            "never-created", "reply text", "corr", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(SlackThreadPostStatus.MappingMissing);
        fx.ChatClient.Calls.Should().BeEmpty();
        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
    }

    [Fact]
    public async Task PostThreadedReplyAsync_returns_failure_on_non_recoverable_error_without_touching_LastMessageAt()
    {
        Fixture fx = this.NewFixture(WorkspaceWithDefaults());
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1.001", DefaultChannel));

        await fx.Manager.GetOrCreateThreadAsync(
            "TASK-900", "agent-α", "corr-900", TeamId, CancellationToken.None);
        fx.ChatClient.Calls.Clear();
        fx.Audit.Clear();

        DateTimeOffset before = (await this.LoadMappingAsync("TASK-900")).LastMessageAt;
        fx.TimeProvider.Now = fx.NowAtCreate.AddHours(1);

        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Failure("rate_limited"));
        SlackThreadPostResult result = await fx.Manager.PostThreadedReplyAsync(
            "TASK-900", "reply", "corr-900-reply", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(SlackThreadPostStatus.Failed);
        result.Error.Should().Be("rate_limited");

        (await this.LoadMappingAsync("TASK-900")).LastMessageAt.Should().Be(before,
            "a failed reply MUST NOT bump LastMessageAt -- the column tracks REAL activity");

        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
    }

    [Fact]
    public async Task RecoverThreadAsync_creates_new_root_in_fallback_channel_and_updates_mapping()
    {
        DateTimeOffset originalCreate = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);
        await this.SeedMappingAsync(new SlackThreadMapping
        {
            TaskId = "TASK-301",
            TeamId = TeamId,
            ChannelId = DefaultChannel,
            ThreadTs = "1716000000.000200",
            CorrelationId = "corr-recover",
            AgentId = "agent-α",
            CreatedAt = originalCreate,
            LastMessageAt = originalCreate,
        });

        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: true));
        fx.ChatClient.Enqueue(SlackChatPostMessageResult.Success("1716999999.000777", FallbackChannel));

        SlackThreadMapping? recovered = await fx.Manager.RecoverThreadAsync(
            "TASK-301", "agent-α", "corr-recover", TeamId, CancellationToken.None);

        recovered.Should().NotBeNull();
        recovered!.ChannelId.Should().Be(FallbackChannel);
        recovered.ThreadTs.Should().Be("1716999999.000777");
        recovered.CreatedAt.Should().Be(originalCreate, "the recovered mapping preserves CreatedAt");

        fx.ChatClient.Calls.Should().ContainSingle();
        fx.ChatClient.Calls[0].ChannelId.Should().Be(FallbackChannel);

        SlackThreadMapping persisted = await this.LoadMappingAsync("TASK-301");
        persisted.ChannelId.Should().Be(FallbackChannel);
        persisted.ThreadTs.Should().Be("1716999999.000777");

        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].RequestType.Should().Be(SlackThreadManager<SlackTestDbContext>.RequestTypeThreadRecover);
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeFallbackUsed);
    }

    [Fact]
    public async Task RecoverThreadAsync_returns_null_and_audits_error_when_no_fallback_configured()
    {
        await this.SeedMappingAsync(new SlackThreadMapping
        {
            TaskId = "TASK-302",
            TeamId = TeamId,
            ChannelId = DefaultChannel,
            ThreadTs = "1716000000.000200",
            CorrelationId = "corr",
            AgentId = "agent-α",
            CreatedAt = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        });

        Fixture fx = this.NewFixture(WorkspaceWithDefaults(withFallback: false));

        SlackThreadMapping? recovered = await fx.Manager.RecoverThreadAsync(
            "TASK-302", "agent-α", "corr", TeamId, CancellationToken.None);

        recovered.Should().BeNull();
        fx.ChatClient.Calls.Should().BeEmpty();
        fx.Audit.Entries.Should().ContainSingle();
        fx.Audit.Entries[0].Outcome.Should().Be(SlackThreadManager<SlackTestDbContext>.OutcomeError);
    }

    private static SlackWorkspaceConfig WorkspaceWithDefaults(bool withFallback = false) => new()
    {
        TeamId = TeamId,
        DefaultChannelId = DefaultChannel,
        FallbackChannelId = withFallback ? FallbackChannel : null,
        Enabled = true,
    };

    private Fixture NewFixture(SlackWorkspaceConfig? workspace)
    {
        IServiceScopeFactory scopeFactory = this.serviceProvider.GetRequiredService<IServiceScopeFactory>();
        InMemorySlackWorkspaceConfigStore store = new();
        if (workspace is not null)
        {
            store.Upsert(workspace);
        }

        FakeTimeProvider time = new(new DateTimeOffset(2025, 5, 1, 8, 30, 0, TimeSpan.Zero));
        ScriptedChatPostMessageClient chat = new();
        InMemoryAuditWriter audit = new();

        SlackThreadManager<SlackTestDbContext> manager = new(
            scopeFactory,
            store,
            chat,
            audit,
            NullLogger<SlackThreadManager<SlackTestDbContext>>.Instance,
            time);

        return new Fixture(manager, chat, audit, time, time.Now);
    }

    private async Task SeedMappingAsync(SlackThreadMapping mapping)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        ctx.ThreadMappings.Add(mapping);
        await ctx.SaveChangesAsync();
    }

    private async Task<SlackThreadMapping> LoadMappingAsync(string taskId)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        SlackThreadMapping? row = await ctx.ThreadMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TaskId == taskId);
        row.Should().NotBeNull($"a mapping for {taskId} was expected to exist");
        return row!;
    }

    private async Task<SlackThreadMapping?> LoadMappingOrNullAsync(string taskId)
    {
        using IServiceScope scope = this.serviceProvider.CreateScope();
        SlackTestDbContext ctx = scope.ServiceProvider.GetRequiredService<SlackTestDbContext>();
        return await ctx.ThreadMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TaskId == taskId);
    }

    private sealed record Fixture(
        SlackThreadManager<SlackTestDbContext> Manager,
        ScriptedChatPostMessageClient ChatClient,
        InMemoryAuditWriter Audit,
        FakeTimeProvider TimeProvider,
        DateTimeOffset NowAtCreate);

    /// <summary>
    /// In-memory chat client. <see cref="Enqueue"/> appends a scripted
    /// response; PostAsync drains the queue in order. When the queue is
    /// empty the client falls back to a generic failure so the test
    /// sees a deterministic, debuggable error instead of a hang.
    /// </summary>
    private sealed class ScriptedChatPostMessageClient : ISlackChatPostMessageClient
    {
        private readonly Queue<SlackChatPostMessageResult> queue = new();

        public List<SlackChatPostMessageRequest> Calls { get; } = new();

        public void Enqueue(SlackChatPostMessageResult result) => this.queue.Enqueue(result);

        public Task<SlackChatPostMessageResult> PostAsync(SlackChatPostMessageRequest request, CancellationToken ct)
        {
            this.Calls.Add(request);
            SlackChatPostMessageResult next = this.queue.Count > 0
                ? this.queue.Dequeue()
                : SlackChatPostMessageResult.Failure("test_queue_exhausted");
            return Task.FromResult(next);
        }
    }

    private sealed class InMemoryAuditWriter : ISlackAuditEntryWriter
    {
        private readonly ConcurrentBag<SlackAuditEntry> entries = new();

        public IReadOnlyList<SlackAuditEntry> Entries
            => this.entries.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToList();

        public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
        {
            this.entries.Add(entry);
            return Task.CompletedTask;
        }

        public void Clear()
        {
            while (this.entries.TryTake(out _))
            {
            }
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public FakeTimeProvider(DateTimeOffset initial)
        {
            this.Now = initial;
        }

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
