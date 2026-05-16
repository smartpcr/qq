// -----------------------------------------------------------------------
// <copyright file="SlackSignatureOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

/// <summary>
/// Tunables for the Stage 3.1 <c>SlackSignatureValidator</c> middleware.
/// Bound from <c>Slack:Signature</c> in configuration via
/// <see cref="SlackConnectorServiceCollectionExtensions.AddSlackSignatureValidation"/>.
/// </summary>
/// <remarks>
/// Defaults follow Slack's published guidance and architecture.md §7.1:
/// reject requests whose <c>X-Slack-Request-Timestamp</c> is more than
/// five minutes older than the validator's clock to defeat replay attacks.
/// </remarks>
public sealed class SlackSignatureOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:Signature"</c>) the options
    /// are bound from.
    /// </summary>
    public const string SectionName = "Slack:Signature";

    /// <summary>
    /// Master switch. When <see langword="false"/> the middleware is
    /// still registered but every request short-circuits to the next
    /// handler. Intended for non-production diagnostic flows; defaults
    /// to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Clock-skew tolerance, in minutes. Requests whose
    /// <c>X-Slack-Request-Timestamp</c> is older than this window are
    /// rejected with HTTP 401 to defeat replay attacks. Defaults to
    /// <c>5</c> minutes (the Slack-published value).
    /// </summary>
    public int ClockSkewMinutes { get; set; } = 5;

    /// <summary>
    /// URL path prefix the middleware is mounted on. When set, the
    /// validator only runs for requests whose path starts with this
    /// segment (case-insensitive). Defaults to <c>"/api/slack"</c> so
    /// the connector's Events API, slash command, and interactions
    /// endpoints are covered automatically.
    /// </summary>
    public string PathPrefix { get; set; } = "/api/slack";

    /// <summary>
    /// Maximum body size (in bytes) the middleware will buffer when
    /// computing the HMAC. Requests with a larger body are rejected.
    /// Defaults to <c>1 MiB</c>, which comfortably exceeds Slack's
    /// 32&nbsp;KiB request limit while bounding memory pressure on the
    /// gateway.
    /// </summary>
    public int MaxBufferedBodyBytes { get; set; } = 1 * 1024 * 1024;
}
