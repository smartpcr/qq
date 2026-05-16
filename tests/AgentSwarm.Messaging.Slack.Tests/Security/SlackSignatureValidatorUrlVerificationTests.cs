// -----------------------------------------------------------------------
// <copyright file="SlackSignatureValidatorUrlVerificationTests.cs" company="Microsoft Corp.">
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
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator-feedback regression tests covering the
/// <see cref="SlackSignatureValidator"/> code paths the iter-1 review
/// flagged as production-blocking:
/// <list type="bullet">
///   <item><description>Events API <c>url_verification</c> handshake (no
///   <c>team_id</c>) must validate against any registered workspace
///   signing secret and forward to the next middleware with the
///   <c>UrlVerificationItemKey</c> marker set.</description></item>
///   <item><description>Oversized buffered bodies must surface a
///   structured <see cref="SlackSignatureRejectionReason.BodyTooLarge"/>
///   rejection (HTTP 401 + audit record) instead of bubbling an
///   <see cref="IOException"/> from
///   <c>FileBufferingReadStream</c>.</description></item>
/// </list>
/// </summary>
public sealed class SlackSignatureValidatorUrlVerificationTests
{
    private const string TeamId = "T0123ABCD";
    private const string SigningSecretRef = "env://SLACK_SIGNING_SECRET";
    private const string SigningSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    private const string EventsPath = "/api/slack/events";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task UrlVerification_passes_when_signature_matches_a_registered_workspace_secret()
    {
        // Slack's url_verification handshake omits team_id; the validator
        // must try each enabled workspace's signing secret and forward
        // the request to the next middleware on the first match.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""token"":""verif"",""challenge"":""3e1d-abc"",""type"":""url_verification""}";
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue(
            "url_verification with a signature matching a registered workspace's secret must reach the endpoint that responds with the challenge");
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().BeEmpty();
        ctx.Items[SlackSignatureValidator.UrlVerificationItemKey].Should().Be(true,
            "the receiver endpoint reads UrlVerificationItemKey to decide whether to respond with the challenge string");
    }

    [Fact]
    public async Task UrlVerification_is_rejected_when_no_workspace_secret_matches()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""token"":""verif"",""challenge"":""3e1d-abc"",""type"":""url_verification""}";
        // Signature computed with the WRONG secret -- no registered
        // workspace can produce a matching HMAC.
        string signature = ComputeSignatureHeader("wrong-secret", timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.SignatureMismatch);
        record.TeamId.Should().BeNull(
            "url_verification carries no team_id, so the audit record must not invent one");
    }

    [Fact]
    public async Task UrlVerification_is_rejected_when_no_workspace_is_registered()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""token"":""verif"",""challenge"":""3e1d-abc"",""type"":""url_verification""}";
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now, registerWorkspace: false);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.UnknownWorkspace);
    }

    [Fact]
    public async Task UrlVerification_picks_the_workspace_whose_secret_matches()
    {
        // Two workspaces registered; the request signs with secret B.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""token"":""verif"",""challenge"":""xyz"",""type"":""url_verification""}";
        const string SecretA = "team-A-secret";
        const string SecretB = "team-B-secret-matches";
        string signature = ComputeSignatureHeader(SecretB, timestamp, body);

        SlackWorkspaceConfig teamA = MakeWorkspace("TA001", "env://A", now);
        SlackWorkspaceConfig teamB = MakeWorkspace("TB002", "env://B", now);

        InMemorySlackWorkspaceConfigStore store = new(seed: new[] { teamA, teamB });
        InMemorySecretProvider secrets = new();
        secrets.Set("env://A", SecretA);
        secrets.Set("env://B", SecretB);

        Harness harness = Harness.BuildWith(now, store, secrets);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        ctx.Items[SlackSignatureValidator.UrlVerificationItemKey].Should().Be(true);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_with_BodyTooLarge_reason_not_unhandled_exception()
    {
        // A body larger than MaxBufferedBodyBytes must surface as a
        // structured 401 + BodyTooLarge audit record rather than an
        // unhandled IOException from FileBufferingReadStream.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = new string('a', 4096); // > 1024-byte cap below
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        SlackSignatureOptions cap = new() { MaxBufferedBodyBytes = 1024 };
        Harness harness = Harness.Build(currentTime: now, options: cap);
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

        caught.Should().BeNull("oversized bodies must produce a controlled 401, not an unhandled exception");
        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.BodyTooLarge,
            "the body cap must produce SlackSignatureRejectionReason.BodyTooLarge so triage can distinguish it from a malformed body");
        record.Outcome.Should().Be(SlackSignatureAuditRecord.RejectedSignatureOutcome);
    }

    [Fact]
    public async Task Body_with_advertised_ContentLength_above_cap_is_rejected_before_reading()
    {
        // ContentLength header advertises an oversized body. The
        // validator MUST reject before EnableBuffering attempts the read.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        SlackSignatureOptions cap = new() { MaxBufferedBodyBytes = 64 };

        Harness harness = Harness.Build(currentTime: now, options: cap);
        HttpContext ctx = BuildJsonContext("{}", "v0=" + new string('a', 64), timestamp.ToString(CultureInfo.InvariantCulture));
        ctx.Request.ContentLength = 5000;

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.BodyTooLarge);
    }

    private static SlackWorkspaceConfig MakeWorkspace(string teamId, string signingSecretRef, DateTimeOffset stamp)
    {
        return new SlackWorkspaceConfig
        {
            TeamId = teamId,
            WorkspaceName = teamId,
            BotTokenSecretRef = "env://BOT_" + teamId,
            SigningSecretRef = signingSecretRef,
            DefaultChannelId = "C-" + teamId,
            Enabled = true,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
    }

    private static HttpContext BuildJsonContext(string body, string? signatureHeader, string? timestampHeader)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = EventsPath;
        ctx.Request.ContentType = "application/json";

        byte[] bodyBytes = Utf8NoBom.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bodyBytes, writable: false);
        ctx.Request.ContentLength = bodyBytes.Length;

        if (signatureHeader is not null)
        {
            ctx.Request.Headers[SlackSignatureValidator.SignatureHeaderName] = signatureHeader;
        }

        if (timestampHeader is not null)
        {
            ctx.Request.Headers[SlackSignatureValidator.TimestampHeaderName] = timestampHeader;
        }

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ComputeSignatureHeader(string signingSecret, long timestamp, string body)
    {
        string baseString = FormattableString.Invariant($"v0:{timestamp}:{body}");
        byte[] hash = HMACSHA256.HashData(Utf8NoBom.GetBytes(signingSecret), Utf8NoBom.GetBytes(baseString));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
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

        public static Harness Build(
            DateTimeOffset currentTime,
            bool registerWorkspace = true,
            SlackSignatureOptions? options = null)
        {
            InMemorySlackWorkspaceConfigStore workspaceStore = new();
            if (registerWorkspace)
            {
                workspaceStore.Upsert(MakeWorkspace(TeamId, SigningSecretRef, currentTime));
            }

            InMemorySecretProvider secrets = new();
            secrets.Set(SigningSecretRef, SigningSecret);
            return BuildWith(currentTime, workspaceStore, secrets, options);
        }

        public static Harness BuildWith(
            DateTimeOffset currentTime,
            ISlackWorkspaceConfigStore workspaceStore,
            ISecretProvider secretProvider,
            SlackSignatureOptions? options = null)
        {
            InMemorySlackSignatureAuditSink auditSink = new();
            FixedTimeProvider timeProvider = new(currentTime);
            StaticOptionsMonitor<SlackSignatureOptions> optionsMonitor = new(options ?? new SlackSignatureOptions());

            SlackSignatureValidator validator = new(
                workspaceStore,
                secretProvider,
                auditSink,
                optionsMonitor,
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
