// -----------------------------------------------------------------------
// <copyright file="WorkerSlackSignatureCompositionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.IO;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 3.1 iter-2 regression tests for the worker host's composition
/// root. The iter-2 evaluator flagged that
/// "<c>Program.BuildApp</c> only calls <c>AddSlackSignatureValidation</c>",
/// causing the audit pipeline to fall back to the in-memory writer and
/// drop rejection rows on restart. These tests pin the production
/// wiring so a future regression in <see cref="Program.BuildApp"/>
/// surfaces here, not in a missing audit row on a live deployment.
/// </summary>
/// <remarks>
/// Each test passes an isolated SQLite path via command-line args so the
/// shared <c>EnsureCreated()</c> call inside <see cref="Program.BuildApp"/>
/// never races sibling test classes that also boot the host. The
/// previous CWD-mutation isolation was not parallel-safe under xUnit's
/// default cross-class parallelization.
/// </remarks>
public sealed class WorkerSlackSignatureCompositionTests : IDisposable
{
    private readonly string testRoot;
    private readonly string sqlitePath;

    public WorkerSlackSignatureCompositionTests()
    {
        this.testRoot = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-3.1-worker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testRoot);
        this.sqlitePath = Path.Combine(this.testRoot, "slack-audit.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.testRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup -- a held SQLite file on Windows must
            // not fail the test run.
        }
    }

    [Fact]
    public void BuildApp_registers_entity_framework_audit_writer_not_in_memory_fallback()
    {
        // Arrange / Act
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // Assert -- the canonical ISlackAuditEntryWriter MUST be the EF
        // writer, NOT the InMemorySlackAuditEntryWriter fallback. If a
        // future refactor reorders the DI extensions in Program.BuildApp,
        // this assertion fires before the host can ship.
        ISlackAuditEntryWriter writer = app.Services.GetRequiredService<ISlackAuditEntryWriter>();
        writer.Should()
            .BeOfType<EntityFrameworkSlackAuditEntryWriter<SlackPersistenceDbContext>>(
                "Stage 3.1 requires signature rejection audit rows to be durably persisted; "
                + "falling back to InMemorySlackAuditEntryWriter would lose them on process restart");
    }

    [Fact]
    public void BuildApp_registers_signature_validator_and_sink_bridge()
    {
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // The signature middleware itself is registered as singleton.
        SlackSignatureValidator validator = app.Services.GetRequiredService<SlackSignatureValidator>();
        validator.Should().NotBeNull();

        // The signature audit sink must be the bridge that maps records
        // onto slack_audit_entry (not the in-memory diagnostic sink).
        ISlackSignatureAuditSink sink = app.Services.GetRequiredService<ISlackSignatureAuditSink>();
        sink.Should().BeOfType<SlackAuditEntrySignatureSink>(
            "the canonical sink bridges SlackSignatureAuditRecord -> SlackAuditEntry -> ISlackAuditEntryWriter");
    }

    [Fact]
    public void BuildApp_registers_environment_backed_secret_provider_from_appsettings()
    {
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // appsettings.json sets SecretProvider:ProviderType = Environment.
        // The composite provider must win over the validator's
        // TryAddSingleton<ISecretProvider, InMemorySecretProvider> fallback.
        ISecretProvider provider = app.Services.GetRequiredService<ISecretProvider>();
        provider.Should().BeOfType<CompositeSecretProvider>(
            "AddSecretProvider must be called before AddSlackSignatureValidation so the "
            + "appsettings SecretProvider:ProviderType selector is honoured in production");
    }

    [Fact]
    public void BuildApp_registers_slack_persistence_db_context_with_sqlite_provider()
    {
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        using IServiceScope scope = app.Services.CreateScope();
        SlackPersistenceDbContext ctx =
            scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();

        // The Database.ProviderName property exposes the active EF provider;
        // SQLite is the default for the Worker host.
        ctx.Database.ProviderName.Should().Be(
            "Microsoft.EntityFrameworkCore.Sqlite",
            "Stage 3.1 ships SQLite as the default audit backing store");
    }

    private string[] BuildIsolatedArgs()
    {
        // WebApplication.CreateBuilder(args) reads "--Key=Value" CLI
        // overrides into IConfiguration, so this is the cleanest
        // parallel-safe alternative to mutating process-global state
        // (env vars or CurrentDirectory) per test.
        return new[]
        {
            $"--ConnectionStrings:{Program.SlackAuditConnectionStringKey}=Data Source={this.sqlitePath}",
        };
    }
}
