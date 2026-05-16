// -----------------------------------------------------------------------
// <copyright file="SlackSignatureValidator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

/// <summary>
/// ASP.NET Core middleware that verifies the Slack request signature on
/// every inbound POST to the Slack endpoint surface (defaults to
/// <c>/api/slack</c>). Implements Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The validator performs the four checks called out by the brief and by
/// architecture.md §7.1:
/// </para>
/// <list type="number">
///   <item><description>Read <c>X-Slack-Signature</c> and
///   <c>X-Slack-Request-Timestamp</c>. Reject 401 when either is missing
///   or malformed.</description></item>
///   <item><description>Reject 401 when the timestamp differs from the
///   validator's clock by more than
///   <see cref="SlackSignatureOptions.ClockSkewMinutes"/> minutes
///   (default 5; replay protection).</description></item>
///   <item><description>Buffer the request body, extract the workspace
///   identifier (<c>team_id</c>) from the JSON or form payload, and
///   resolve the signing secret via
///   <see cref="ISlackWorkspaceConfigStore"/> +
///   <see cref="ISecretProvider"/>. Reject 401 when either lookup
///   fails.</description></item>
///   <item><description>Compute
///   <c>HMAC_SHA256(signingSecret, "v0:" + timestamp + ":" + body)</c>
///   and compare with the header value in constant time. Mismatch =&gt;
///   reject 401.</description></item>
/// </list>
/// <para>
/// Every rejection is forwarded to <see cref="ISlackSignatureAuditSink"/>
/// with <see cref="SlackSignatureAuditRecord.RejectedSignatureOutcome"/>
/// so the audit pipeline can persist it to <c>slack_audit_entry</c>.
/// </para>
/// <para>
/// On success the validator rewinds the buffered body so downstream
/// controllers (added by Stage 4.1) read the original payload, and stamps
/// the resolved <see cref="SlackWorkspaceConfig"/> on
/// <see cref="HttpContext.Items"/> under <see cref="WorkspaceItemKey"/> so
/// later filters do not have to repeat the lookup.
/// </para>
/// </remarks>
public sealed class SlackSignatureValidator : IMiddleware
{
    /// <summary>
    /// Slack signature header. <c>X-Slack-Signature</c> per
    /// <see href="https://api.slack.com/authentication/verifying-requests-from-slack"/>.
    /// </summary>
    public const string SignatureHeaderName = "X-Slack-Signature";

    /// <summary>
    /// Slack timestamp header.
    /// </summary>
    public const string TimestampHeaderName = "X-Slack-Request-Timestamp";

    /// <summary>
    /// Signature header prefix and version tag used in the HMAC string.
    /// Slack's published spec is <c>v0=</c> / <c>v0:</c>.
    /// </summary>
    public const string VersionTag = "v0";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the resolved
    /// <see cref="SlackWorkspaceConfig"/> is stamped on success.
    /// </summary>
    public const string WorkspaceItemKey = "AgentSwarm.Slack.WorkspaceConfig";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key set to <see langword="true"/>
    /// when the request was accepted via the Events API
    /// <c>url_verification</c> handshake path. The receiver endpoint
    /// (Stage 4.1) reads this marker to respond with the challenge
    /// instead of enqueuing the event.
    /// </summary>
    public const string UrlVerificationItemKey = "AgentSwarm.Slack.UrlVerification";

    private const string UrlVerificationType = "url_verification";

    /// <summary>
    /// Inclusive lower bound for any <c>long</c> that
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds(long)"/> will accept
    /// without throwing <see cref="ArgumentOutOfRangeException"/>.
    /// Matches the framework constant
    /// <c>DateTimeOffset.MinValue.ToUnixTimeSeconds()</c>; hard-coded so
    /// the range check is a single integer comparison and visible at
    /// the call site.
    /// </summary>
    private const long MinUnixTimeSeconds = -62_135_596_800L;

    /// <summary>
    /// Inclusive upper bound for any <c>long</c> that
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds(long)"/> will accept.
    /// Matches <c>DateTimeOffset.MaxValue.ToUnixTimeSeconds()</c>.
    /// </summary>
    private const long MaxUnixTimeSeconds = 253_402_300_799L;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISecretProvider secretProvider;
    private readonly ISlackSignatureAuditSink auditSink;
    private readonly ILogger<SlackSignatureValidator> logger;
    private readonly IOptionsMonitor<SlackSignatureOptions> optionsMonitor;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new instance with the supplied dependencies.
    /// </summary>
    public SlackSignatureValidator(
        ISlackWorkspaceConfigStore workspaceStore,
        ISecretProvider secretProvider,
        ISlackSignatureAuditSink auditSink,
        IOptionsMonitor<SlackSignatureOptions> optionsMonitor,
        ILogger<SlackSignatureValidator> logger,
        TimeProvider? timeProvider = null)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        SlackSignatureOptions options = this.optionsMonitor.CurrentValue;

        if (!options.Enabled || !this.PathMatches(context, options))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        SlackSignatureValidationResult result = await this.ValidateAsync(context, options).ConfigureAwait(false);

        if (result.Accepted)
        {
            if (result.Workspace is not null)
            {
                context.Items[WorkspaceItemKey] = result.Workspace;
            }

            if (result.IsUrlVerification)
            {
                context.Items[UrlVerificationItemKey] = true;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        await this.RecordRejectionAsync(context, options, result).ConfigureAwait(false);
        await WriteRejectionResponseAsync(context, result).ConfigureAwait(false);
    }

    private static async Task<string> BufferBodyAsync(HttpContext context, int maxBytes)
    {
        // Enable buffering so downstream controllers can re-read the body
        // once we've finished computing the HMAC. EnableBuffering's own
        // limit short-circuits the read, but the CopyToAsync call below
        // is the only line that observes the result, so we still validate
        // the final length defensively.
        context.Request.EnableBuffering(bufferThreshold: 32 * 1024, bufferLimit: maxBytes);

        // Reject obviously oversized payloads up-front so the audit
        // pipeline can record a structured rejection BEFORE the buffering
        // stream starts throwing IOException("Request body too large").
        if (context.Request.ContentLength is long advertised && advertised > maxBytes)
        {
            throw new SlackBodyTooLargeException(advertised, maxBytes);
        }

        using MemoryStream buffer = new();
        try
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        }
        catch (IOException ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            // FileBufferingReadStream throws IOException once bufferLimit
            // is exceeded mid-read. Surface it as a structured rejection
            // so the audit sink records "body too large" instead of
            // bubbling an unhandled 500.
            throw new SlackBodyTooLargeException(buffer.Length, maxBytes, ex);
        }

        if (buffer.Length > maxBytes)
        {
            throw new SlackBodyTooLargeException(buffer.Length, maxBytes);
        }

        context.Request.Body.Position = 0;
        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static string? ExtractTeamId(string body, string? contentType)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        string trimmed = body.AsSpan().TrimStart().ToString();
        if (trimmed.StartsWith('{'))
        {
            return ExtractTeamIdFromJson(trimmed);
        }

        // Interactions and slash commands arrive as form-urlencoded data.
        // Interactions wrap the JSON inside a "payload" field; commands
        // carry team_id directly.
        FormFields fields = ParseFormFields(body);

        if (fields.TryGet("payload", out string? payloadJson) && !string.IsNullOrEmpty(payloadJson))
        {
            string? teamFromPayload = ExtractTeamIdFromJson(payloadJson);
            if (!string.IsNullOrEmpty(teamFromPayload))
            {
                return teamFromPayload;
            }
        }

        if (fields.TryGet("team_id", out string? teamId) && !string.IsNullOrEmpty(teamId))
        {
            return teamId;
        }

        _ = contentType;
        return null;
    }

    private static string? ExtractTeamIdFromJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Top-level (Events API, interactive payloads, url_verification).
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("team_id", out JsonElement teamId))
            {
                if (teamId.ValueKind == JsonValueKind.String)
                {
                    return teamId.GetString();
                }
            }

            // Events API event-callback envelope nests team_id at the top
            // level AND repeats it inside "event" / "team" sub-objects.
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("team", out JsonElement teamObj)
                    && teamObj.ValueKind == JsonValueKind.Object
                    && teamObj.TryGetProperty("id", out JsonElement nestedTeamId)
                    && nestedTeamId.ValueKind == JsonValueKind.String)
                {
                    return nestedTeamId.GetString();
                }

                if (root.TryGetProperty("event", out JsonElement evt)
                    && evt.ValueKind == JsonValueKind.Object
                    && evt.TryGetProperty("team", out JsonElement evtTeam)
                    && evtTeam.ValueKind == JsonValueKind.String)
                {
                    return evtTeam.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FormFields ParseFormFields(string body)
    {
        // We deliberately use QueryHelpers.ParseQuery (RFC 3986 percent
        // decoding identical to ASP.NET Core's form binder) so callers
        // get the same parsing semantics they will see in their action
        // method bodies. Slack form payloads are small (Slack caps
        // requests at 32 KiB) so the materialisation cost is trivial.
        return new FormFields(QueryHelpers.ParseQuery(body));
    }

    private static byte[] ComputeHmacSha256(string secret, string message)
    {
        using HMACSHA256 hmac = new(Utf8NoBom.GetBytes(secret));
        return hmac.ComputeHash(Utf8NoBom.GetBytes(message));
    }

    private static bool TryParseSignature(string headerValue, out byte[] signature)
    {
        signature = Array.Empty<byte>();
        if (string.IsNullOrEmpty(headerValue))
        {
            return false;
        }

        int separator = headerValue.IndexOf('=');
        if (separator <= 0 || separator == headerValue.Length - 1)
        {
            return false;
        }

        string prefix = headerValue[..separator];
        if (!string.Equals(prefix, VersionTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string hex = headerValue[(separator + 1)..];
        if ((hex.Length & 1) != 0)
        {
            return false;
        }

        byte[] decoded = new byte[hex.Length / 2];
        for (int i = 0; i < decoded.Length; i++)
        {
            if (!byte.TryParse(
                    hex.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out byte b))
            {
                return false;
            }

            decoded[i] = b;
        }

        signature = decoded;
        return true;
    }

    private static async Task WriteRejectionResponseAsync(HttpContext context, SlackSignatureValidationResult result)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "text/plain; charset=utf-8";

        string body = $"Slack signature rejected: {result.Reason}.";
        await context.Response.WriteAsync(body, Utf8NoBom, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsUrlVerificationPayload(string body, string? contentType)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        string trimmed = body.AsSpan().TrimStart().ToString();
        if (trimmed.StartsWith('{'))
        {
            return ExtractTypeFromJson(trimmed) == UrlVerificationType;
        }

        // Slash commands and interactions are never url_verification --
        // Slack always sends url_verification as application/json. Skip
        // the form-decoding branch entirely.
        _ = contentType;
        return false;
    }

    private static string? ExtractTypeFromJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<SlackSignatureValidationResult> ValidateUrlVerificationAsync(
        HttpContext context,
        string signatureHeader,
        string timestampHeaderValue,
        long timestampSeconds,
        string body)
    {
        IReadOnlyCollection<SlackWorkspaceConfig> registered =
            await this.workspaceStore.GetAllEnabledAsync(context.RequestAborted).ConfigureAwait(false);

        if (registered.Count == 0)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.UnknownWorkspace,
                "url_verification received but no workspace is registered to validate against.",
                signatureHeader: signatureHeader,
                timestampHeader: timestampHeaderValue,
                teamId: null);
        }

        if (!TryParseSignature(signatureHeader, out byte[] signatureBytes))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MalformedSignature,
                "Signature header is not in the v0=<hex> form.",
                signatureHeader: signatureHeader,
                timestampHeader: timestampHeaderValue,
                teamId: null);
        }

        string baseString = FormattableString.Invariant($"{VersionTag}:{timestampSeconds}:{body}");

        bool anySecretResolved = false;
        bool anyMalformedRef = false;
        foreach (SlackWorkspaceConfig workspace in registered)
        {
            SecretResolutionOutcome outcome = await this
                .TryResolveSigningSecretAsync(workspace, context.RequestAborted)
                .ConfigureAwait(false);

            if (outcome.Status == SecretResolutionStatus.Malformed)
            {
                anyMalformedRef = true;
                this.logger.LogWarning(
                    "Slack workspace {TeamId} has a malformed signing_secret_ref ({Detail}); skipping during url_verification handshake.",
                    workspace.TeamId,
                    outcome.ErrorDetail);
                continue;
            }

            if (outcome.Status == SecretResolutionStatus.Unresolved)
            {
                // Skip workspaces whose secret is currently unresolvable;
                // we'll still accept the request if a sibling workspace
                // produces a matching HMAC.
                continue;
            }

            anySecretResolved = true;
            byte[] computed = ComputeHmacSha256(outcome.Secret!, baseString);
            if (CryptographicOperations.FixedTimeEquals(computed, signatureBytes))
            {
                return SlackSignatureValidationResult.AcceptUrlVerification(workspace);
            }
        }

        if (!anySecretResolved)
        {
            // Distinguish "every workspace was misconfigured" (operator
            // error: 401 + MalformedSigningSecretRef) from "every secret
            // store lookup came back empty" (transient / unconfigured
            // env vars: 401 + SigningSecretUnresolved). Operators triage
            // these very differently.
            SlackSignatureRejectionReason reason = anyMalformedRef
                ? SlackSignatureRejectionReason.MalformedSigningSecretRef
                : SlackSignatureRejectionReason.SigningSecretUnresolved;
            string detail = anyMalformedRef
                ? "url_verification: every registered workspace has a malformed signing_secret_ref."
                : "url_verification: no registered workspace's signing secret could be resolved.";

            return SlackSignatureValidationResult.Reject(
                reason,
                detail,
                signatureHeader: signatureHeader,
                timestampHeader: timestampHeaderValue,
                teamId: null);
        }

        return SlackSignatureValidationResult.Reject(
            SlackSignatureRejectionReason.SignatureMismatch,
            "url_verification HMAC did not match any registered workspace signing secret.",
            signatureHeader: signatureHeader,
            timestampHeader: timestampHeaderValue,
            teamId: null);
    }

    private async Task<SecretResolutionOutcome> TryResolveSigningSecretAsync(
        SlackWorkspaceConfig workspace,
        CancellationToken ct)
    {
        // Pre-check the reference itself so the secret provider's
        // ArgumentException for null/empty/whitespace does NOT escape as
        // an unhandled 500. Misconfigured workspaces (operator forgot
        // to set signing_secret_ref) become a clean 401 +
        // MalformedSigningSecretRef audit row.
        string? signingSecretRef = workspace?.SigningSecretRef;
        if (string.IsNullOrWhiteSpace(signingSecretRef))
        {
            return SecretResolutionOutcome.MalformedRef(
                signingSecretRef,
                "SigningSecretRef is null, empty, or whitespace.");
        }

        try
        {
            string secret = await this.secretProvider
                .GetSecretAsync(signingSecretRef, ct)
                .ConfigureAwait(false);

            // Some providers (or test doubles) may return null/empty even
            // when they do not throw -- treat that the same as a missing
            // secret so the HMAC step is never fed a zero-length key.
            if (string.IsNullOrEmpty(secret))
            {
                return SecretResolutionOutcome.Unresolved(signingSecretRef);
            }

            return SecretResolutionOutcome.Ok(secret);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honour caller cancellation -- do NOT swallow.
            throw;
        }
        catch (SecretNotFoundException ex)
        {
            return SecretResolutionOutcome.Unresolved(ex.SecretRef);
        }
        catch (ArgumentException ex)
        {
            // Provider rejected the reference shape (e.g., a provider
            // that requires a specific scheme). Surface as
            // MalformedSigningSecretRef rather than an unhandled 500.
            return SecretResolutionOutcome.MalformedRef(signingSecretRef, ex.Message);
        }
        catch (FormatException ex)
        {
            // Reference parsed by provider but field-shape (e.g. an
            // unparseable Key Vault URI) failed. Same operator-error
            // class as ArgumentException -- malformed ref.
            return SecretResolutionOutcome.MalformedRef(signingSecretRef, ex.Message);
        }
        catch (Exception ex)
        {
            // Last-resort guard: ANY other provider failure (transient
            // network error from a remote secret store, KeyVault throttling,
            // K8s API outage, etc.) must produce a structured 401 +
            // SigningSecretUnresolved rejection so the validator never
            // bubbles a raw 500 to Slack. The exception is logged at
            // ERROR by the rejection-audit pipeline so operators can
            // diagnose the underlying provider failure.
            this.logger.LogError(
                ex,
                "Unexpected exception of type {ExceptionType} resolving signing-secret ref '{SecretRef}' for team {TeamId}.",
                ex.GetType().FullName,
                signingSecretRef,
                workspace?.TeamId);
            return SecretResolutionOutcome.Unresolved(signingSecretRef);
        }
    }

    private bool PathMatches(HttpContext context, SlackSignatureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PathPrefix))
        {
            return true;
        }

        return context.Request.Path.StartsWithSegments(
            new PathString(options.PathPrefix),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SlackSignatureValidationResult> ValidateAsync(HttpContext context, SlackSignatureOptions options)
    {
        if (!context.Request.Headers.TryGetValue(SignatureHeaderName, out StringValues sigValues)
            || StringValues.IsNullOrEmpty(sigValues))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MissingSignatureHeader,
                "X-Slack-Signature header is missing.",
                signatureHeader: null,
                timestampHeader: GetHeader(context, TimestampHeaderName),
                teamId: null);
        }

        string signatureHeader = sigValues.ToString();

        if (!context.Request.Headers.TryGetValue(TimestampHeaderName, out StringValues tsValues)
            || StringValues.IsNullOrEmpty(tsValues)
            || !long.TryParse(tsValues.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestampSeconds))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MissingOrInvalidTimestampHeader,
                "X-Slack-Request-Timestamp header is missing or not a Unix timestamp.",
                signatureHeader: signatureHeader,
                timestampHeader: GetHeader(context, TimestampHeaderName),
                teamId: null);
        }

        // Guard against `long` values that parse but fall outside the
        // domain of `DateTimeOffset.FromUnixTimeSeconds`
        // ([-62_135_596_800, 253_402_300_799]). Without this check a
        // crafted timestamp like `long.MaxValue` would throw
        // `ArgumentOutOfRangeException` from inside the framework, escape
        // the middleware, and surface as a 500 Internal Server Error --
        // breaking the security contract that EVERY rejection is a
        // controlled 401 + audit row. We bucket out-of-range timestamps
        // alongside non-numeric input under the same
        // `MissingOrInvalidTimestampHeader` reason because both fail at
        // the "the header is not a usable Unix timestamp" boundary.
        if (timestampSeconds < MinUnixTimeSeconds || timestampSeconds > MaxUnixTimeSeconds)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MissingOrInvalidTimestampHeader,
                FormattableString.Invariant(
                    $"X-Slack-Request-Timestamp value {timestampSeconds} is outside the representable Unix-time range."),
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }

        DateTimeOffset now = this.timeProvider.GetUtcNow();
        DateTimeOffset headerTime;
        try
        {
            headerTime = DateTimeOffset.FromUnixTimeSeconds(timestampSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Defense-in-depth -- the explicit range check above should
            // have caught this. Kept so that any future drift in the
            // framework's accepted range still degrades to a controlled
            // 401 instead of bubbling a 500.
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MissingOrInvalidTimestampHeader,
                FormattableString.Invariant(
                    $"X-Slack-Request-Timestamp value {timestampSeconds} is outside the representable Unix-time range."),
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }

        TimeSpan skew = now - headerTime;
        TimeSpan allowance = TimeSpan.FromMinutes(Math.Max(0, options.ClockSkewMinutes));
        if (skew > allowance || skew < -allowance)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.StaleTimestamp,
                FormattableString.Invariant(
                    $"Timestamp differs from validator clock by {(long)skew.TotalSeconds} s (limit {(long)allowance.TotalSeconds} s)."),
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }

        string body;
        try
        {
            body = await BufferBodyAsync(context, options.MaxBufferedBodyBytes).ConfigureAwait(false);
        }
        catch (SlackBodyTooLargeException ex)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.BodyTooLarge,
                ex.Message,
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }
        catch (InvalidDataException ex)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MalformedBody,
                ex.Message,
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }

        // Slack's Events API URL-verification handshake POSTs a payload
        // with type=url_verification and NO team_id. Architecture.md
        // §2.2.2 requires us to honour this handshake even though our
        // workspace-keyed signing-secret lookup cannot help. We instead
        // try each enabled workspace's signing secret in constant time
        // and accept if any matches. The endpoint at /api/slack/events
        // (Stage 4.1) reads UrlVerificationItemKey from HttpContext.Items
        // to respond with the challenge.
        bool isUrlVerification = IsUrlVerificationPayload(body, context.Request.ContentType);

        string? teamId = ExtractTeamId(body, context.Request.ContentType);

        if (isUrlVerification && string.IsNullOrWhiteSpace(teamId))
        {
            return await this
                .ValidateUrlVerificationAsync(context, signatureHeader, tsValues.ToString(), timestampSeconds, body)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(teamId))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MalformedBody,
                "team_id could not be extracted from the request body.",
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: null);
        }

        SlackWorkspaceConfig? workspace =
            await this.workspaceStore.GetByTeamIdAsync(teamId, context.RequestAborted).ConfigureAwait(false);
        if (workspace is null || !workspace.Enabled)
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.UnknownWorkspace,
                $"team_id '{teamId}' is not registered or is disabled.",
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: teamId);
        }

        SecretResolutionOutcome resolution = await this
            .TryResolveSigningSecretAsync(workspace, context.RequestAborted)
            .ConfigureAwait(false);

        switch (resolution.Status)
        {
            case SecretResolutionStatus.Malformed:
                this.logger.LogError(
                    "Slack workspace {TeamId} has a malformed signing_secret_ref: {Detail}.",
                    teamId,
                    resolution.ErrorDetail);
                return SlackSignatureValidationResult.Reject(
                    SlackSignatureRejectionReason.MalformedSigningSecretRef,
                    $"Signing secret reference is malformed: {resolution.ErrorDetail}",
                    signatureHeader: signatureHeader,
                    timestampHeader: tsValues.ToString(),
                    teamId: teamId);

            case SecretResolutionStatus.Unresolved:
                this.logger.LogError(
                    "Slack signing secret reference {SecretRef} did not resolve for team {TeamId}.",
                    resolution.SecretRef,
                    teamId);
                return SlackSignatureValidationResult.Reject(
                    SlackSignatureRejectionReason.SigningSecretUnresolved,
                    $"Signing secret reference '{resolution.SecretRef}' did not resolve.",
                    signatureHeader: signatureHeader,
                    timestampHeader: tsValues.ToString(),
                    teamId: teamId);
        }

        string secret = resolution.Secret!;

        if (!TryParseSignature(signatureHeader, out byte[] signatureBytes))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.MalformedSignature,
                "Signature header is not in the v0=<hex> form.",
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: teamId);
        }

        string baseString = FormattableString.Invariant($"{VersionTag}:{timestampSeconds}:{body}");
        byte[] computed = ComputeHmacSha256(secret, baseString);

        if (!CryptographicOperations.FixedTimeEquals(computed, signatureBytes))
        {
            return SlackSignatureValidationResult.Reject(
                SlackSignatureRejectionReason.SignatureMismatch,
                "Computed HMAC does not match the X-Slack-Signature header.",
                signatureHeader: signatureHeader,
                timestampHeader: tsValues.ToString(),
                teamId: teamId);
        }

        return SlackSignatureValidationResult.Accept(workspace);
    }

    private async Task RecordRejectionAsync(
        HttpContext context,
        SlackSignatureOptions options,
        SlackSignatureValidationResult result)
    {
        _ = options;

        SlackSignatureAuditRecord record = new(
            ReceivedAt: this.timeProvider.GetUtcNow(),
            Reason: result.Reason,
            Outcome: SlackSignatureAuditRecord.RejectedSignatureOutcome,
            RequestPath: context.Request.Path.Value ?? string.Empty,
            TeamId: result.TeamId,
            SignatureHeader: TruncateHeaderForAudit(result.SignatureHeader),
            TimestampHeader: result.TimestampHeader,
            ErrorDetail: result.ErrorDetail);

        try
        {
            await this.auditSink.WriteAsync(record, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Audit write failure must not change the response status; the
            // request has already been classified as a rejection.
            this.logger.LogError(
                ex,
                "Failed to write Slack signature rejection audit entry for path {Path}.",
                context.Request.Path.Value);
        }

        this.logger.LogWarning(
            "Slack signature rejected: reason={Reason}, path={Path}, team_id={TeamId}, detail={Detail}.",
            result.Reason,
            context.Request.Path.Value,
            result.TeamId,
            result.ErrorDetail);
    }

    private static string? TruncateHeaderForAudit(string? value)
    {
        if (value is null)
        {
            return null;
        }

        const int Max = 128;
        return value.Length <= Max ? value : value[..Max];
    }

    private static string? GetHeader(HttpContext context, string name)
    {
        return context.Request.Headers.TryGetValue(name, out StringValues values) && !StringValues.IsNullOrEmpty(values)
            ? values.ToString()
            : null;
    }

    private readonly struct FormFields
    {
        private readonly System.Collections.Generic.IDictionary<string, StringValues> source;

        public FormFields(System.Collections.Generic.IDictionary<string, StringValues> source)
        {
            this.source = source;
        }

        public bool TryGet(string name, out string? value)
        {
            if (this.source.TryGetValue(name, out StringValues values) && !StringValues.IsNullOrEmpty(values))
            {
                value = values.ToString();
                return true;
            }

            value = null;
            return false;
        }
    }

    private enum SecretResolutionStatus
    {
        Ok = 0,
        Unresolved = 1,
        Malformed = 2,
    }

    private readonly struct SecretResolutionOutcome
    {
        private SecretResolutionOutcome(
            SecretResolutionStatus status,
            string? secret,
            string? secretRef,
            string? errorDetail)
        {
            this.Status = status;
            this.Secret = secret;
            this.SecretRef = secretRef;
            this.ErrorDetail = errorDetail;
        }

        public SecretResolutionStatus Status { get; }

        public string? Secret { get; }

        public string? SecretRef { get; }

        public string? ErrorDetail { get; }

        public static SecretResolutionOutcome Ok(string secret)
            => new(SecretResolutionStatus.Ok, secret, secretRef: null, errorDetail: null);

        public static SecretResolutionOutcome Unresolved(string? secretRef)
            => new(SecretResolutionStatus.Unresolved, secret: null, secretRef: secretRef, errorDetail: null);

        public static SecretResolutionOutcome MalformedRef(string? secretRef, string detail)
            => new(SecretResolutionStatus.Malformed, secret: null, secretRef: secretRef, errorDetail: detail);
    }

    private readonly struct SlackSignatureValidationResult
    {
        private SlackSignatureValidationResult(
            bool accepted,
            SlackSignatureRejectionReason reason,
            string? errorDetail,
            string? signatureHeader,
            string? timestampHeader,
            string? teamId,
            SlackWorkspaceConfig? workspace,
            bool isUrlVerification)
        {
            this.Accepted = accepted;
            this.Reason = reason;
            this.ErrorDetail = errorDetail;
            this.SignatureHeader = signatureHeader;
            this.TimestampHeader = timestampHeader;
            this.TeamId = teamId;
            this.Workspace = workspace;
            this.IsUrlVerification = isUrlVerification;
        }

        public bool Accepted { get; }

        public SlackSignatureRejectionReason Reason { get; }

        public string? ErrorDetail { get; }

        public string? SignatureHeader { get; }

        public string? TimestampHeader { get; }

        public string? TeamId { get; }

        public SlackWorkspaceConfig? Workspace { get; }

        public bool IsUrlVerification { get; }

        public static SlackSignatureValidationResult Accept(SlackWorkspaceConfig workspace)
            => new(
                accepted: true,
                reason: SlackSignatureRejectionReason.Unspecified,
                errorDetail: null,
                signatureHeader: null,
                timestampHeader: null,
                teamId: workspace?.TeamId,
                workspace: workspace,
                isUrlVerification: false);

        public static SlackSignatureValidationResult AcceptUrlVerification(SlackWorkspaceConfig? workspace)
            => new(
                accepted: true,
                reason: SlackSignatureRejectionReason.Unspecified,
                errorDetail: null,
                signatureHeader: null,
                timestampHeader: null,
                teamId: workspace?.TeamId,
                workspace: workspace,
                isUrlVerification: true);

        public static SlackSignatureValidationResult Reject(
            SlackSignatureRejectionReason reason,
            string errorDetail,
            string? signatureHeader,
            string? timestampHeader,
            string? teamId)
            => new(
                accepted: false,
                reason: reason,
                errorDetail: errorDetail,
                signatureHeader: signatureHeader,
                timestampHeader: timestampHeader,
                teamId: teamId,
                workspace: null,
                isUrlVerification: false);
    }
}

/// <summary>
/// Internal exception raised by <see cref="SlackSignatureValidator"/>
/// when buffered body bytes exceed
/// <see cref="SlackSignatureOptions.MaxBufferedBodyBytes"/>. Caught and
/// turned into a structured <see cref="SlackSignatureRejectionReason.BodyTooLarge"/>
/// rejection so the audit pipeline records it instead of bubbling an
/// unhandled 500.
/// </summary>
internal sealed class SlackBodyTooLargeException : Exception
{
    public SlackBodyTooLargeException(long observedBytes, long maxBytes)
        : base(BuildMessage(observedBytes, maxBytes))
    {
        this.ObservedBytes = observedBytes;
        this.MaxBytes = maxBytes;
    }

    public SlackBodyTooLargeException(long observedBytes, long maxBytes, Exception inner)
        : base(BuildMessage(observedBytes, maxBytes), inner)
    {
        this.ObservedBytes = observedBytes;
        this.MaxBytes = maxBytes;
    }

    public long ObservedBytes { get; }

    public long MaxBytes { get; }

    private static string BuildMessage(long observedBytes, long maxBytes)
        => FormattableString.Invariant(
            $"Request body of {observedBytes} bytes exceeds the configured {maxBytes}-byte Slack signature buffer.");
}
