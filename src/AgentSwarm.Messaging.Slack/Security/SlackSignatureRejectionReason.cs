// -----------------------------------------------------------------------
// <copyright file="SlackSignatureRejectionReason.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

/// <summary>
/// Enumerated reason for a rejection by <see cref="SlackSignatureValidator"/>.
/// The middleware always returns HTTP 401 on rejection regardless of value;
/// the discriminator exists so the audit sink and structured logger can
/// distinguish "well-formed but stale" from "unknown workspace" without
/// parsing free-form text.
/// </summary>
/// <remarks>
/// Mapped onto the brief's <c>outcome = rejected_signature</c> audit
/// value: every entry written through <see cref="ISlackSignatureAuditSink"/>
/// stores this enum together with the outcome string so an operator
/// triaging signature failures can quickly bucket them.
/// </remarks>
public enum SlackSignatureRejectionReason
{
    /// <summary>
    /// Default value; should never be persisted.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// <c>X-Slack-Signature</c> header is absent on the inbound request.
    /// </summary>
    MissingSignatureHeader = 1,

    /// <summary>
    /// <c>X-Slack-Request-Timestamp</c> header is absent or not a valid
    /// integer.
    /// </summary>
    MissingOrInvalidTimestampHeader = 2,

    /// <summary>
    /// Timestamp is outside the configured clock-skew tolerance (default
    /// 5 minutes per architecture.md §7.1) -- replay protection.
    /// </summary>
    StaleTimestamp = 3,

    /// <summary>
    /// Body could not be read or parsed to extract a Slack
    /// <c>team_id</c> for the signing-secret lookup.
    /// </summary>
    MalformedBody = 4,

    /// <summary>
    /// Body parsed but the supplied <c>team_id</c> is not registered with
    /// the connector.
    /// </summary>
    UnknownWorkspace = 5,

    /// <summary>
    /// Workspace registered but signing-secret reference did not resolve
    /// to a stored secret -- typically a misconfigured deployment.
    /// </summary>
    SigningSecretUnresolved = 6,

    /// <summary>
    /// Signature header format is invalid (not <c>v0=&lt;hex&gt;</c>) or
    /// the hex payload is unparseable.
    /// </summary>
    MalformedSignature = 7,

    /// <summary>
    /// Header signature did not match the HMAC computed for the
    /// observed body and timestamp.
    /// </summary>
    SignatureMismatch = 8,

    /// <summary>
    /// Request body exceeded
    /// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackSignatureOptions.MaxBufferedBodyBytes"/>
    /// before the HMAC could be computed. Defends the gateway from
    /// memory exhaustion by adversarial Slack-shaped payloads.
    /// </summary>
    BodyTooLarge = 9,

    /// <summary>
    /// The resolved <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig.SigningSecretRef"/>
    /// for the request's workspace was null, empty, whitespace, or
    /// otherwise rejected by the secret provider as malformed (e.g.,
    /// <c>env://</c> with no variable name). Distinct from
    /// <see cref="SigningSecretUnresolved"/>: that reason means the
    /// reference looked syntactically valid but the backing store did
    /// not have a value; this reason means the reference itself is
    /// broken (operator misconfiguration of
    /// <c>slack_workspace_config.signing_secret_ref</c>).
    /// </summary>
    MalformedSigningSecretRef = 10,
}
