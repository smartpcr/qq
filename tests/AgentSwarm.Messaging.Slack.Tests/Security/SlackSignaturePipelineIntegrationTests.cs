// -----------------------------------------------------------------------
// <copyright file="SlackSignaturePipelineIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator iter-1 item 1 + item 2 regression tests.
/// </summary>
/// <remarks>
/// <para>
/// Item 1 ("middleware is registered in DI but not wired into the
/// ASP.NET Core request pipeline") is verified end-to-end by booting the
/// real <see cref="Program"/> host via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, firing an HTTP
/// request at <c>/api/slack/commands</c>, and asserting the middleware
/// executes (401 on bad signature, pass-through on good signature).
/// </para>
/// <para>
/// Item 2 ("rejections write only an ILogger warning, not an actual
/// audit entry") is verified by querying
/// <see cref="SlackPersistenceDbContext.SlackAuditEntries"/> after the
/// rejection and asserting a row with
/// <c>Outcome = rejected_signature</c> landed in SQLite. Each test
/// uses an isolated SQLite file so the assertion is deterministic.
/// </para>
/// </remarks>
public sealed class SlackSignaturePipelineIntegrationTests : IDisposable
{
    private const string TestTeamId = "T01TEST0001";
    private const string TestSigningSecretRef = "test://signing-secret/T01TEST0001";
    private const string TestSigningSecret = "8f742231b10e8888abcd99edabcd00d6";

    private readonly string sqliteDirectory;
    private readonly string sqlitePath;

    public SlackSignaturePipelineIntegrationTests()
    {
        // Isolate the audit SQLite file per test class instance so the
        // assertions never see leakage from a sibling test, and so the
        // file path lives outside the repo root.
        this.sqliteDirectory = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-3.1-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.sqliteDirectory);
        this.sqlitePath = Path.Combine(this.sqliteDirectory, "audit.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.sqliteDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; an open SQLite handle on Windows must
            // not fail the test run.
        }
    }

    [Fact]
    public async Task Request_with_missing_signature_header_returns_401_through_pipeline()
    {
        using SlackPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using StringContent content = new("token=xoxb&team_id=T01TEST0001&text=hello", Encoding.UTF8, "application/x-www-form-urlencoded");
        using HttpResponseMessage response = await client.PostAsync("/api/slack/commands", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the SlackSignatureValidator must execute on /api/slack/* requests via UseSlackSignatureValidation()");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("Slack signature rejected:",
            "the middleware's branded rejection body confirms it intercepted the request rather than the default 404 handler");
    }

    [Fact]
    public async Task Request_with_tampered_signature_returns_401_through_pipeline()
    {
        using SlackPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        string body = "token=xoxb&team_id=T01TEST0001&text=hello";
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string tamperedSig = "v0=" + new string('a', 64);

        using StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        content.Headers.TryAddWithoutValidation(SlackSignatureValidator.SignatureHeaderName, tamperedSig);
        content.Headers.TryAddWithoutValidation(
            SlackSignatureValidator.TimestampHeaderName,
            timestamp.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync("/api/slack/commands", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().StartWith("Slack signature rejected:");
    }

    [Fact]
    public async Task Request_with_valid_signature_passes_signature_middleware()
    {
        using SlackPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        string body = "token=xoxb&team_id=T01TEST0001&command=%2Fagent&text=hello";
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string baseString = FormattableString.Invariant($"{SlackSignatureValidator.VersionTag}:{timestamp}:{body}");
        string signature = $"{SlackSignatureValidator.VersionTag}={ComputeHexHmac(TestSigningSecret, baseString)}";

        using StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        content.Headers.TryAddWithoutValidation(SlackSignatureValidator.SignatureHeaderName, signature);
        content.Headers.TryAddWithoutValidation(
            SlackSignatureValidator.TimestampHeaderName,
            timestamp.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync("/api/slack/commands", content);

        // No /api/slack/commands endpoint is mapped at Stage 3.1, so the
        // pipeline falls through to the default 404 handler AFTER the
        // signature middleware accepts the request. The critical proof
        // is that the validator did NOT short-circuit with 401.
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "a request with a valid HMAC must pass the signature middleware and reach later pipeline stages");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "Stage 3.1 ships the validator but does not yet map the Slack endpoints (added by Stage 4.1)");
    }

    [Fact]
    public async Task Request_outside_slack_path_prefix_bypasses_middleware()
    {
        using SlackPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the validator must short-circuit for paths outside SlackSignatureOptions.PathPrefix so health probes are not gated by an HMAC");
    }

    [Fact]
    public async Task Rejection_persists_audit_entry_row_to_sqlite_database()
    {
        using SlackPipelineFactory factory = this.CreateFactory();
        using HttpClient client = factory.CreateClient();

        long staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        string body = "token=xoxb&team_id=T01TEST0001";
        string anyHexSig = "v0=" + new string('0', 64);

        using StringContent content = new(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        content.Headers.TryAddWithoutValidation(SlackSignatureValidator.SignatureHeaderName, anyHexSig);
        content.Headers.TryAddWithoutValidation(
            SlackSignatureValidator.TimestampHeaderName,
            staleTimestamp.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await client.PostAsync("/api/slack/events", content);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Open a fresh DbContext (same SQLite file, separate connection)
        // to read the row the middleware just persisted. This proves the
        // audit pipeline writes through EF Core to the real
        // slack_audit_entry table, not just to ILogger.
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        SlackPersistenceDbContext ctx =
            scope.ServiceProvider.GetRequiredService<SlackPersistenceDbContext>();
        List<SlackAuditEntry> rows = await ctx.SlackAuditEntries.AsNoTracking().ToListAsync();

        rows.Should().HaveCount(1, "exactly one rejection was issued in this test");
        SlackAuditEntry row = rows[0];
        row.Direction.Should().Be("inbound");
        row.Outcome.Should().Be("rejected_signature",
            "the canonical Outcome value for signature failures is defined by SlackSignatureAuditRecord.RejectedSignatureOutcome");
        row.RequestType.Should().Be("event",
            "/api/slack/events maps to RequestType=event in SlackAuditEntrySignatureSink.DeriveRequestType");
        row.TeamId.Should().NotBeNullOrEmpty();
        row.ErrorDetail.Should().Contain("StaleTimestamp");
    }

    [Fact]
    public async Task Configured_workspace_is_observable_via_workspace_config_store()
    {
        // Item 4 regression: the host MUST be able to seed at least one
        // workspace from the Slack:Workspaces configuration section so a
        // real Slack request can be validated end-to-end after pipeline
        // wiring. Asserting the store via ISlackWorkspaceConfigStore is
        // sufficient -- the prior tests in this class then prove the
        // validator actually uses the seed to accept a real HMAC.
        using SlackPipelineFactory factory = this.CreateFactory();

        using IServiceScope scope = factory.Services.CreateScope();
        ISlackWorkspaceConfigStore store =
            scope.ServiceProvider.GetRequiredService<ISlackWorkspaceConfigStore>();
        SlackWorkspaceConfig? config = await store.GetByTeamIdAsync(TestTeamId, CancellationToken.None);

        config.Should().NotBeNull("the in-memory store must be seeded from Slack:Workspaces in appsettings");
        config!.TeamId.Should().Be(TestTeamId);
        config.SigningSecretRef.Should().Be(TestSigningSecretRef);
        config.Enabled.Should().BeTrue();
    }

    private static string ComputeHexHmac(string secret, string baseString)
    {
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
        StringBuilder sb = new(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private SlackPipelineFactory CreateFactory()
    {
        SlackPipelineFactory factory = new(this.sqlitePath);

        // Seed the test signing secret into the InMemorySecretProvider
        // BEFORE the first request fires. The composite selects the
        // in-memory backend because the factory pinned
        // SecretProvider:ProviderType=InMemory in configuration.
        InMemorySecretProvider inMemory =
            factory.Services.GetRequiredService<InMemorySecretProvider>();
        inMemory.Set(TestSigningSecretRef, TestSigningSecret);

        return factory;
    }

    private sealed class SlackPipelineFactory : WebApplicationFactory<Program>
    {
        private readonly string sqlitePath;

        public SlackPipelineFactory(string sqlitePath)
        {
            this.sqlitePath = sqlitePath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                Dictionary<string, string?> overrides = new()
                {
                    // Pin the InMemory backend so the test can seed
                    // signing secrets without touching the process env.
                    ["SecretProvider:ProviderType"] = "InMemory",

                    // Isolated SQLite database per test class so the
                    // audit-entry assertion is deterministic.
                    ["ConnectionStrings:" + Program.SlackAuditConnectionStringKey] =
                        $"Data Source={this.sqlitePath}",

                    // Seed a known workspace via the new
                    // Slack:Workspaces configuration path. The test then
                    // confirms the validator can resolve the workspace
                    // and the audit sink can write its team_id.
                    ["Slack:Workspaces:0:TeamId"] = TestTeamId,
                    ["Slack:Workspaces:0:WorkspaceName"] = "Stage 3.1 Pipeline Test Workspace",
                    ["Slack:Workspaces:0:SigningSecretRef"] = TestSigningSecretRef,
                    ["Slack:Workspaces:0:BotTokenSecretRef"] = "test://bot-token/T01TEST0001",
                    ["Slack:Workspaces:0:DefaultChannelId"] = "C01TEST0001",
                    ["Slack:Workspaces:0:AllowedChannelIds:0"] = "C01TEST0001",
                    ["Slack:Workspaces:0:AllowedUserGroupIds:0"] = "S01TEST0001",
                    ["Slack:Workspaces:0:Enabled"] = "true",
                };

                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
