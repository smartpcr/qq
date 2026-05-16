// -----------------------------------------------------------------------
// <copyright file="SlackSignatureValidatorTests.cs" company="Microsoft Corp.">
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
/// Stage 3.1 acceptance tests for <see cref="SlackSignatureValidator"/>.
/// Each <c>[Fact]</c> implements one of the three scenarios called out by
/// the implementation-plan brief
/// (<c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// §Stage 3.1):
/// <list type="bullet">
///   <item><description>Valid signature passes -- request proceeds and the
///   resolved workspace is stamped on <see cref="HttpContext.Items"/>.</description></item>
///   <item><description>Invalid signature rejected -- HTTP 401, audit
///   record with reason <see cref="SlackSignatureRejectionReason.SignatureMismatch"/>.</description></item>
///   <item><description>Stale timestamp rejected -- HTTP 401, audit record
///   with reason <see cref="SlackSignatureRejectionReason.StaleTimestamp"/>.</description></item>
/// </list>
/// Supplementary facts pin the supporting failure modes (missing headers,
/// unknown workspace, unresolved signing secret, malformed signature, body
/// rewinding, path-prefix gating, and disabled-master-switch behaviour) so
/// regressions surface here rather than in a later, harder-to-debug stage.
/// </summary>
public sealed class SlackSignatureValidatorTests
{
    private const string TeamId = "T0123ABCD";
    private const string SigningSecretRef = "env://SLACK_SIGNING_SECRET";
    private const string SigningSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    private const string DefaultPath = "/api/slack/events";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task Valid_signature_passes_and_calls_next_middleware()
    {
        // Brief scenario: Given a request with a correctly computed HMAC
        // SHA-256 signature and timestamp within 5 minutes, When the
        // validator processes the request, Then it proceeds to the next
        // middleware.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = @"{""team_id"":""T0123ABCD"",""type"":""event_callback"",""event"":{}}";
        long timestamp = now.ToUnixTimeSeconds();
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue("a valid signature must propagate to downstream middleware");
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().BeEmpty("no audit entry is recorded on the happy path");
        ctx.Items[SlackSignatureValidator.WorkspaceItemKey].Should().BeOfType<SlackWorkspaceConfig>()
            .Which.TeamId.Should().Be(TeamId,
                "the resolved workspace must be stamped onto HttpContext.Items so downstream filters reuse the lookup");
    }

    [Fact]
    public async Task Valid_signature_leaves_body_readable_for_downstream_middleware()
    {
        // Regression guard: the middleware buffers the request body to
        // compute the HMAC; it must rewind it so action methods can read
        // the original payload again.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_005);
        string body = @"{""team_id"":""T0123ABCD"",""type"":""event_callback""}";
        long timestamp = now.ToUnixTimeSeconds();
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));
        string? capturedBody = null;

        await harness.Validator.InvokeAsync(ctx, async http =>
        {
            using StreamReader reader = new(http.Request.Body, Utf8NoBom, leaveOpen: true);
            capturedBody = await reader.ReadToEndAsync();
        });

        capturedBody.Should().Be(body, "downstream middleware must be able to re-read the original request body");
    }

    [Fact]
    public async Task Tampered_signature_is_rejected_with_401_and_audit_entry()
    {
        // Brief scenario: Given a request with a tampered signature, When
        // the validator processes it, Then HTTP 401 is returned.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = @"{""team_id"":""T0123ABCD"",""type"":""event_callback""}";
        long timestamp = now.ToUnixTimeSeconds();
        string correctSignature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        // Flip the last hex nibble; FixedTimeEquals must reject this even
        // though the prefix, length, and parsing all succeed.
        string tamperedSignature = correctSignature[..^1] + (correctSignature[^1] == '0' ? '1' : '0');
        tamperedSignature.Should().NotBe(correctSignature);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, tamperedSignature, timestamp.ToString(CultureInfo.InvariantCulture));
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeFalse("a rejected request must short-circuit the pipeline");
        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);

        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should()
            .ContainSingle("the rejection must produce exactly one audit entry").Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.SignatureMismatch);
        record.Outcome.Should().Be(SlackSignatureAuditRecord.RejectedSignatureOutcome);
        record.TeamId.Should().Be(TeamId,
            "team_id is known once the body is parsed, even when the signature mismatch is the rejection cause");
        record.RequestPath.Should().Be(DefaultPath);
    }

    [Fact]
    public async Task Stale_timestamp_is_rejected_with_401_and_audit_entry()
    {
        // Brief scenario: Given a request with a timestamp older than 5
        // minutes, When the validator processes it, Then HTTP 401 is
        // returned.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long staleTimestamp = now.AddMinutes(-6).ToUnixTimeSeconds();
        string body = @"{""team_id"":""T0123ABCD"",""type"":""event_callback""}";
        string signature = ComputeSignatureHeader(SigningSecret, staleTimestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, staleTimestamp.ToString(CultureInfo.InvariantCulture));
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);

        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.StaleTimestamp,
            "replay protection bucket is StaleTimestamp per architecture.md §7.1");
        record.Outcome.Should().Be(SlackSignatureAuditRecord.RejectedSignatureOutcome);
        record.TimestampHeader.Should().Be(staleTimestamp.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Future_timestamp_outside_skew_window_is_also_rejected()
    {
        // Symmetric replay guard: the validator must reject timestamps in
        // the future by more than the skew window, not just stale ones.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long futureTimestamp = now.AddMinutes(6).ToUnixTimeSeconds();
        string body = @"{""team_id"":""T0123ABCD""}";
        string signature = ComputeSignatureHeader(SigningSecret, futureTimestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, futureTimestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.StaleTimestamp);
    }

    [Fact]
    public async Task Missing_signature_header_is_rejected()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""team_id"":""T0123ABCD""}";

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signatureHeader: null, timestampHeader: timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.MissingSignatureHeader);
    }

    [Fact]
    public async Task Missing_timestamp_header_is_rejected()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = @"{""team_id"":""T0123ABCD""}";
        // Signature value can be arbitrary; the validator should bail on
        // the missing timestamp before computing the HMAC.
        string signature = "v0=" + new string('a', 64);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestampHeader: null);

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.MissingOrInvalidTimestampHeader);
    }

    [Theory]
    [InlineData("9223372036854775807")] // long.MaxValue -- well past DateTimeOffset.MaxValue.ToUnixTimeSeconds()
    [InlineData("-9223372036854775808")] // long.MinValue -- well before DateTimeOffset.MinValue.ToUnixTimeSeconds()
    [InlineData("253402300800")] // DateTimeOffset.MaxValue.ToUnixTimeSeconds() + 1
    [InlineData("-62135596801")] // DateTimeOffset.MinValue.ToUnixTimeSeconds() - 1
    public async Task Out_of_range_timestamp_is_rejected_as_controlled_401_not_500(string timestampHeader)
    {
        // Stage 3.1 evaluator iter-2 item 1: a timestamp that parses as
        // long but falls outside DateTimeOffset.FromUnixTimeSeconds's
        // accepted range used to bubble an ArgumentOutOfRangeException
        // out of the middleware, surfacing as a 500 Internal Server
        // Error -- breaking the security contract that EVERY signature
        // rejection produces a controlled 401 + audit row. The validator
        // must bucket out-of-range values alongside missing/non-numeric
        // headers under MissingOrInvalidTimestampHeader.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = @"{""team_id"":""T0123ABCD""}";
        string signature = "v0=" + new string('a', 64);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestampHeader: timestampHeader);

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be(
            (int)HttpStatusCode.Unauthorized,
            "an out-of-range Unix-time value MUST NOT escape the middleware as a 500");
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(
            SlackSignatureRejectionReason.MissingOrInvalidTimestampHeader,
            "out-of-range timestamps share the unusable-header bucket");
        record.TimestampHeader.Should().Be(timestampHeader);
    }

    [Fact]
    public async Task Unknown_team_id_is_rejected()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""team_id"":""T_UNREGISTERED""}";
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        SlackSignatureAuditRecord record = harness.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackSignatureRejectionReason.UnknownWorkspace);
        record.TeamId.Should().Be("T_UNREGISTERED");
    }

    [Fact]
    public async Task Unresolved_signing_secret_reference_is_rejected()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""team_id"":""T0123ABCD""}";
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        // Build a harness whose secret provider does NOT contain the
        // configured SigningSecretRef.
        Harness harness = Harness.Build(currentTime: now, includeSigningSecret: false);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.SigningSecretUnresolved);
    }

    [Fact]
    public async Task Malformed_signature_header_is_rejected()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = @"{""team_id"":""T0123ABCD""}";
        // Wrong version tag -- TryParseSignature must refuse to decode.
        string signature = "v1=" + new string('a', 64);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildJsonContext(body, signature, timestamp.ToString(CultureInfo.InvariantCulture));

        await harness.Validator.InvokeAsync(ctx, _ => Task.CompletedTask);

        ctx.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackSignatureRejectionReason.MalformedSignature);
    }

    [Fact]
    public async Task Form_encoded_slash_command_payload_is_validated()
    {
        // Slash commands arrive as application/x-www-form-urlencoded with
        // team_id as a top-level form field. The signature is computed
        // over the raw form body, exactly as for JSON payloads.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        long timestamp = now.ToUnixTimeSeconds();
        string body = "token=verif&team_id=T0123ABCD&command=%2Fagent&text=ask";
        string signature = ComputeSignatureHeader(SigningSecret, timestamp, body);

        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildContext(
            body,
            "application/x-www-form-urlencoded",
            signature,
            timestamp.ToString(CultureInfo.InvariantCulture),
            "/api/slack/commands");

        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue("slash-command form payloads with valid signatures must pass");
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Path_outside_prefix_passes_through_without_validation()
    {
        // The middleware mounts on /api/slack/* only; routes such as
        // /health/live must not be HMAC-checked.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        string body = "{}";
        Harness harness = Harness.Build(currentTime: now);
        HttpContext ctx = BuildContext(body, "application/json", signatureHeader: null, timestampHeader: null, path: "/health/live");
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue("non-Slack paths must not require a Slack signature");
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_master_switch_short_circuits_to_next_middleware()
    {
        // SlackSignatureOptions.Enabled = false: the validator is still in
        // the pipeline but every request flows through (diagnostic mode).
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        SlackSignatureOptions disabled = new() { Enabled = false };
        Harness harness = Harness.Build(currentTime: now, options: disabled);

        HttpContext ctx = BuildJsonContext(@"{""team_id"":""T0123ABCD""}", signatureHeader: null, timestampHeader: null);
        bool nextInvoked = false;

        await harness.Validator.InvokeAsync(ctx, _ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        nextInvoked.Should().BeTrue();
        ctx.Response.StatusCode.Should().NotBe((int)HttpStatusCode.Unauthorized);
        harness.AuditSink.Records.Should().BeEmpty();
    }

    private static HttpContext BuildJsonContext(string body, string? signatureHeader, string? timestampHeader)
    {
        return BuildContext(body, "application/json", signatureHeader, timestampHeader, DefaultPath);
    }

    private static HttpContext BuildContext(
        string body,
        string contentType,
        string? signatureHeader,
        string? timestampHeader,
        string path)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = path;
        ctx.Request.ContentType = contentType;

        byte[] bodyBytes = Utf8NoBom.GetBytes(body);
        MemoryStream stream = new(bodyBytes, writable: false);
        ctx.Request.Body = stream;
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
        private Harness(
            SlackSignatureValidator validator,
            InMemorySlackSignatureAuditSink auditSink,
            InMemorySlackWorkspaceConfigStore workspaceStore,
            InMemorySecretProvider secretProvider)
        {
            this.Validator = validator;
            this.AuditSink = auditSink;
            this.WorkspaceStore = workspaceStore;
            this.SecretProvider = secretProvider;
        }

        public SlackSignatureValidator Validator { get; }

        public InMemorySlackSignatureAuditSink AuditSink { get; }

        public InMemorySlackWorkspaceConfigStore WorkspaceStore { get; }

        public InMemorySecretProvider SecretProvider { get; }

        public static Harness Build(
            DateTimeOffset currentTime,
            bool includeSigningSecret = true,
            SlackSignatureOptions? options = null)
        {
            SlackWorkspaceConfig workspace = new()
            {
                TeamId = TeamId,
                WorkspaceName = "Acme",
                BotTokenSecretRef = "env://SLACK_BOT_TOKEN",
                SigningSecretRef = SigningSecretRef,
                DefaultChannelId = "C0123",
                Enabled = true,
                CreatedAt = currentTime,
                UpdatedAt = currentTime,
            };

            InMemorySlackWorkspaceConfigStore workspaceStore = new(seed: new[] { workspace });

            InMemorySecretProvider secretProvider = new();
            if (includeSigningSecret)
            {
                secretProvider.Set(SigningSecretRef, SigningSecret);
            }

            InMemorySlackSignatureAuditSink auditSink = new();
            FixedTimeProvider timeProvider = new(currentTime);
            IOptionsMonitor<SlackSignatureOptions> optionsMonitor =
                new StaticOptionsMonitor<SlackSignatureOptions>(options ?? new SlackSignatureOptions());

            SlackSignatureValidator validator = new(
                workspaceStore,
                secretProvider,
                auditSink,
                optionsMonitor,
                NullLogger<SlackSignatureValidator>.Instance,
                timeProvider);

            return new Harness(validator, auditSink, workspaceStore, secretProvider);
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
