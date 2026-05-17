// -----------------------------------------------------------------------
// <copyright file="SlackThreadLifecycleServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 6.2 DI smoke tests for
/// <see cref="SlackThreadLifecycleServiceCollectionExtensions"/>:
/// asserts the right service-type bindings land on the container and
/// that host-supplied overrides (registered before the extension)
/// still win where the contract specifies <c>TryAdd*</c>.
/// </summary>
public sealed class SlackThreadLifecycleServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSlackThreadLifecycleManagement_registers_thread_manager_and_default_chat_client()
    {
        ServiceCollection services = BuildBaseServices();

        services.AddSlackThreadLifecycleManagement<SlackTestDbContext>();

        using ServiceProvider sp = services.BuildServiceProvider();

        ISlackThreadManager manager = sp.GetRequiredService<ISlackThreadManager>();
        manager.Should().BeOfType<SlackThreadManager<SlackTestDbContext>>(
            "the extension MUST replace any earlier ISlackThreadManager registration with the EF-backed manager");

        ISlackChatPostMessageClient chat = sp.GetRequiredService<ISlackChatPostMessageClient>();
        chat.Should().BeOfType<HttpClientSlackChatPostMessageClient>(
            "the extension MUST wire the HTTP-backed chat.postMessage client as the default");
    }

    [Fact]
    public void AddSlackThreadLifecycleManagement_replaces_earlier_thread_manager_registrations()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<ISlackThreadManager, StubThreadManager>();

        services.AddSlackThreadLifecycleManagement<SlackTestDbContext>();

        using ServiceProvider sp = services.BuildServiceProvider();
        ISlackThreadManager manager = sp.GetRequiredService<ISlackThreadManager>();
        manager.Should().NotBeOfType<StubThreadManager>(
            "the extension uses RemoveAll + AddSingleton because the whole purpose is to swap in the real manager");
        manager.Should().BeOfType<SlackThreadManager<SlackTestDbContext>>();
    }

    [Fact]
    public void AddSlackThreadLifecycleManagement_honours_pre_registered_chat_post_message_client()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<ISlackChatPostMessageClient, StubChatPostMessageClient>();

        services.AddSlackThreadLifecycleManagement<SlackTestDbContext>();

        using ServiceProvider sp = services.BuildServiceProvider();
        ISlackChatPostMessageClient chat = sp.GetRequiredService<ISlackChatPostMessageClient>();
        chat.Should().BeOfType<StubChatPostMessageClient>(
            "the chat client registration uses TryAdd so host-supplied production clients (Stage 6.4 SlackDirectApiClient) win");
    }

    [Fact]
    public void AddSlackChatPostMessageOptions_binds_section_from_configuration()
    {
        ServiceCollection services = BuildBaseServices();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string?>(
                    "Slack:ChatPostMessage:RequestTimeout", "00:00:07"),
            })
            .Build();

        services.AddSlackThreadLifecycleManagement<SlackTestDbContext>();
        services.AddSlackChatPostMessageOptions(config);

        using ServiceProvider sp = services.BuildServiceProvider();
        SlackChatPostMessageClientOptions opts = sp
            .GetRequiredService<IOptions<SlackChatPostMessageClientOptions>>().Value;
        opts.RequestTimeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Resolved_manager_uses_configured_chat_client_and_persists_mapping()
    {
        // End-to-end DI test: prove that the extension's wiring is
        // sufficient to actually run a thread-create round trip
        // against the test DbContext.
        using SqliteConnection connection = new("DataSource=:memory:");
        connection.Open();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ISecretProvider>(new NoopSecretProvider());
        services.AddSingleton<ISlackWorkspaceConfigStore>(new SeededWorkspaceStore("T-1"));
        services.AddDbContext<SlackTestDbContext>(opts => opts.UseSqlite(connection));

        StubChatPostMessageClient stubClient = new();
        services.AddSingleton<ISlackChatPostMessageClient>(stubClient);
        services.AddSingleton<ISlackAuditEntryWriter, NoopAuditWriter>();

        services.AddSlackThreadLifecycleManagement<SlackTestDbContext>();

        using ServiceProvider sp = services.BuildServiceProvider();
        using (IServiceScope bootstrap = sp.CreateScope())
        {
            await bootstrap.ServiceProvider
                .GetRequiredService<SlackTestDbContext>()
                .Database.EnsureCreatedAsync();
        }

        stubClient.NextResult = SlackChatPostMessageResult.Success("1.001", "C-1");

        ISlackThreadManager manager = sp.GetRequiredService<ISlackThreadManager>();
        SlackThreadMapping mapping = await manager.GetOrCreateThreadAsync(
            taskId: "TASK-DI",
            agentId: "agent",
            correlationId: "corr",
            teamId: "T-1",
            CancellationToken.None);

        mapping.ThreadTs.Should().Be("1.001");
        mapping.ChannelId.Should().Be("C-1",
            "the manager MUST post into the workspace's DefaultChannelId rather than a caller-supplied channel");
        stubClient.CallCount.Should().Be(1);
    }

    private static ServiceCollection BuildBaseServices()
    {
        ServiceCollection services = new();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<ISecretProvider>(new NoopSecretProvider());
        services.AddSingleton<ISlackWorkspaceConfigStore>(new SeededWorkspaceStore("T-DI"));
        services.AddSingleton<ISlackAuditEntryWriter, NoopAuditWriter>();
        services.AddDbContext<SlackTestDbContext>(opts =>
            opts.UseSqlite("DataSource=file::memory:?cache=private"));
        return services;
    }

    private sealed class StubThreadManager : ISlackThreadManager
    {
        public Task<SlackThreadMapping> GetOrCreateThreadAsync(
            string taskId, string agentId, string correlationId,
            string teamId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SlackThreadMapping?> GetThreadAsync(string taskId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<bool> TouchAsync(string taskId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SlackThreadMapping?> RecoverThreadAsync(
            string taskId, string agentId, string correlationId,
            string teamId, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<SlackThreadPostResult> PostThreadedReplyAsync(
            string taskId, string text, string? correlationId, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubChatPostMessageClient : ISlackChatPostMessageClient
    {
        public int CallCount { get; private set; }

        public SlackChatPostMessageResult NextResult { get; set; }
            = SlackChatPostMessageResult.Failure("stub");

        public Task<SlackChatPostMessageResult> PostAsync(
            SlackChatPostMessageRequest request, CancellationToken ct)
        {
            this.CallCount++;
            return Task.FromResult(this.NextResult);
        }
    }

    private sealed class NoopSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
            => Task.FromResult("noop");
    }

    private sealed class SeededWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly string teamId;

        public SeededWorkspaceStore(string teamId) => this.teamId = teamId;

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            if (string.Equals(teamId, this.teamId, StringComparison.Ordinal))
            {
                return Task.FromResult<SlackWorkspaceConfig?>(new SlackWorkspaceConfig
                {
                    TeamId = this.teamId,
                    BotTokenSecretRef = "noop",
                    DefaultChannelId = "C-1",
                    Enabled = true,
                });
            }

            return Task.FromResult<SlackWorkspaceConfig?>(null);
        }

        public Task<System.Collections.Generic.IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
        {
            return Task.FromResult<System.Collections.Generic.IReadOnlyCollection<SlackWorkspaceConfig>>(
                System.Array.Empty<SlackWorkspaceConfig>());
        }
    }

    private sealed class NoopAuditWriter : ISlackAuditEntryWriter
    {
        public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public static readonly NullLoggerProvider Instance = new();

        public ILogger CreateLogger(string categoryName)
            => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }
    }
}
