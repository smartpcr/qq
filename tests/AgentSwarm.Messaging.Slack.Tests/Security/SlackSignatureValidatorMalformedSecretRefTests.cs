// -----------------------------------------------------------------------
// <copyright file="SlackSignatureValidatorMalformedSecretRefTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.1 iter-2 evaluator-feedback regression tests for the
/// "malformed <c>SigningSecretRef</c>" code path. The validator must
/// turn null/empty/whitespace refs (and any
/// <see cref="ArgumentException"/> from the secret provider) into a
/// controlled 401 + <c>MalformedSigningSecretRef</c> audit record
/// instead of letting the provider's argument-validation exception
/// escape as an unhandled 500.
/// </summary>
public sealed class SlackSignatureValidatorMalformedSecretRefTests
{
    private const string TeamId = "T0123ABCD";
    private const string EventsPath = "/api/slack/events";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Malformed_workspace_signing_secret_ref_returns_controlled_401(string? refValue)
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = $@"{{""team_id"":""{TeamId}"",""type"":""event_callback""}}";
        string signature = ComputeSignatureHeader("any-secret", timestamp, body);

        SlackWorkspaceConfig workspace = new()
        {
            TeamId = TeamId,
            WorkspaceName = TeamId,
            BotTokenSecretRef = "env://BOT",
            // The misconfiguration under test:
            SigningSecretRef = refValue!,
            DefaultChannelId = "C-" + TeamId,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Harness harness = Harness.Build(now, workspace);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        Exception? caught = null;
        try
        {
            await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().BeNull(
            "a malformed signing_secret_ref must surface as a controlled 401, not an unhandled exception from the secret provider's argument validation");
        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.MalformedSigningSecretRef,
            "the rejection reason must distinguish 'admin misconfigured the workspace' from 'env var is unset' so operator triage can pivot on it");
        record.TeamId.Should().Be(TeamId,
            "team_id was successfully extracted from the body before the secret-resolution failure");
    }

    [Fact]
    public async Task Secret_provider_argument_exception_is_mapped_to_MalformedSigningSecretRef()
    {
        // A hypothetical secret provider that requires a specific scheme
        // and throws ArgumentException for refs it can't parse. The
        // validator must not bubble the exception.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = $@"{{""team_id"":""{TeamId}"",""type"":""event_callback""}}";
        string signature = ComputeSignatureHeader("any-secret", timestamp, body);

        SlackWorkspaceConfig workspace = new()
        {
            TeamId = TeamId,
            WorkspaceName = TeamId,
            BotTokenSecretRef = "env://BOT",
            SigningSecretRef = "totally-malformed::ref",
            DefaultChannelId = "C-" + TeamId,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Harness harness = Harness.BuildWith(
            now,
            workspace,
            new ThrowingArgumentExceptionSecretProvider());
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.MalformedSigningSecretRef);
    }

    [Fact]
    public async Task UrlVerification_with_only_malformed_workspaces_is_rejected_with_MalformedSigningSecretRef()
    {
        // url_verification carries no team_id, so the validator tries
        // every enabled workspace. If every workspace is misconfigured
        // (null SigningSecretRef), the rejection must surface as
        // MalformedSigningSecretRef rather than the more ambiguous
        // SigningSecretUnresolved.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""token"":""x"",""challenge"":""abc"",""type"":""url_verification""}";
        string signature = ComputeSignatureHeader("any-secret", timestamp, body);

        SlackWorkspaceConfig misconfigured = new()
        {
            TeamId = TeamId,
            WorkspaceName = TeamId,
            BotTokenSecretRef = "env://BOT",
            SigningSecretRef = " ",
            DefaultChannelId = "C-" + TeamId,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Harness harness = Harness.Build(now, misconfigured);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.MalformedSigningSecretRef);
        record.TeamId.Should().BeNull("url_verification has no team_id");
    }

    [Fact]
    public async Task Unexpected_provider_exception_is_swallowed_and_audited_not_propagated()
    {
        // A misbehaving provider (e.g., future KeyVault provider hitting
        // a transient network error) must NOT bubble an unhandled
        // exception to the caller. The validator must instead produce a
        // controlled 401 + SigningSecretUnresolved audit record so the
        // caller never sees a 500.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = $@"{{""team_id"":""{TeamId}"",""type"":""event_callback""}}";
        string signature = ComputeSignatureHeader("any-secret", timestamp, body);

        SlackWorkspaceConfig workspace = new()
        {
            TeamId = TeamId,
            WorkspaceName = TeamId,
            BotTokenSecretRef = "env://BOT",
            SigningSecretRef = "env://VALID-LOOKING-REF",
            DefaultChannelId = "C-" + TeamId,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Harness harness = Harness.BuildWith(
            now,
            workspace,
            new ThrowingInvalidOperationSecretProvider());
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        Exception? caught = null;
        try
        {
            await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().BeNull(
            "an unexpected provider exception must NEVER bubble out of the middleware; "
            + "the rejection must be controlled and audited so a transient KeyVault outage "
            + "does not leak a 500 to Slack");
        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.SigningSecretUnresolved,
            "unexpected provider failures (network errors, server-side errors) map to Unresolved "
            + "to differentiate them from MalformedSigningSecretRef (operator-misconfiguration)");
    }

    [Fact]
    public async Task Provider_returning_empty_string_is_treated_as_SigningSecretUnresolved()
    {
        // A provider that returns "" (instead of throwing
        // SecretNotFoundException) must not feed a zero-length HMAC key
        // to the signature computation -- that would falsely accept
        // requests signed with any zero-length key. Treat as unresolved.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = $@"{{""team_id"":""{TeamId}"",""type"":""event_callback""}}";
        string signature = ComputeSignatureHeader("any-secret", timestamp, body);

        SlackWorkspaceConfig workspace = new()
        {
            TeamId = TeamId,
            WorkspaceName = TeamId,
            BotTokenSecretRef = "env://BOT",
            SigningSecretRef = "env://EMPTY-VALUED-REF",
            DefaultChannelId = "C-" + TeamId,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Harness harness = Harness.BuildWith(
            now,
            workspace,
            new ReturnsEmptyStringSecretProvider());
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.SigningSecretUnresolved);
    }

    private static HttpContext BuildJsonContext(string body, string signature, string timestampHeader)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = EventsPath;
        ctx.Request.ContentType = "application/json";

        byte[] bodyBytes = Utf8NoBom.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bodyBytes, writable: false);
        ctx.Request.ContentLength = bodyBytes.Length;
        ctx.Request.Headers[SlackSignatureValidator.SignatureHeaderName] = signature;
        ctx.Request.Headers[SlackSignatureValidator.TimestampHeaderName] = timestampHeader;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ComputeSignatureHeader(string signingSecret, long timestamp, string body)
    {
        string baseString = FormattableString.Invariant($"v0:{timestamp}:{body}");
        byte[] hash = HMACSHA256.HashData(Utf8NoBom.GetBytes(signingSecret), Utf8NoBom.GetBytes(baseString));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class ThrowingArgumentExceptionSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            throw new ArgumentException(
                $"Provider rejected secret reference shape '{secretRef}'.",
                nameof(secretRef));
        }
    }

    private sealed class ThrowingInvalidOperationSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            throw new InvalidOperationException(
                $"Simulated transient provider failure resolving '{secretRef}' (e.g., KeyVault throttling).");
        }
    }

    private sealed class ReturnsEmptyStringSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class Harness
    {
        private Harness(SlackSignatureValidator validator, InMemorySlackSignatureAuditSink auditSink)
        {
            this.Validator = validator;
            this.AuditSink = auditSink;
        }

        public SlackSignatureValidator Validator { get; }

        public InMemorySlackSignatureAuditSink AuditSink { get; }

        public static Harness Build(DateTimeOffset now, SlackWorkspaceConfig workspace)
        {
            return BuildWith(now, workspace, new InMemorySecretProvider());
        }

        public static Harness BuildWith(
            DateTimeOffset now,
            SlackWorkspaceConfig workspace,
            ISecretProvider secretProvider)
        {
            InMemorySlackWorkspaceConfigStore store = new();
            store.Upsert(workspace);

            InMemorySlackSignatureAuditSink auditSink = new();
            FixedTimeProvider timeProvider = new(now);
            StaticOptionsMonitor<SlackSignatureOptions> options = new(new SlackSignatureOptions());

            SlackSignatureValidator validator = new(
                store,
                secretProvider,
                auditSink,
                options,
                NullLogger<SlackSignatureValidator>.Instance,
                timeProvider);

            return new Harness(validator, auditSink);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => this.utcNow;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
