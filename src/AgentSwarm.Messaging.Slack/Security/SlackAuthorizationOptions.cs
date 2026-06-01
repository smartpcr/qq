// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

/// <summary>
/// Tunables for the Stage 3.2 <see cref="SlackAuthorizationFilter"/>.
/// Bound from <c>Slack:Authorization</c> in configuration via
/// <see cref="SlackAuthorizationServiceCollectionExtensions.AddSlackAuthorization"/>.
/// </summary>
/// <remarks>
/// <para>
/// Defaults follow the implementation-plan brief: every Slack endpoint
/// must return HTTP 200 even on rejection, with the rejection
/// communicated to the human caller via an ephemeral Slack message body.
/// The default text is intentionally generic so reject responses never
/// leak which layer of the three-layer ACL failed (an attacker fishing
/// for a valid <c>channel_id</c> or workspace gets the same reply as
/// an in-workspace user typing the wrong sub-command).
/// </para>
/// <para>
/// <strong>Path scope is intentionally NOT configured here.</strong> The
/// filter derives its URL scope from
/// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackSignatureOptions.PathPrefix"/>
/// so the HMAC middleware and the authorization gate cover exactly the
/// same surface area by construction -- there is no separate
/// <c>Slack:Authorization:PathPrefix</c> that an operator can leave
/// pointing at the old default while moving the signature middleware to
/// a new mount point. That class of misconfiguration was a real
/// authorization-bypass footgun in earlier drafts and is now impossible.
/// </para>
/// </remarks>
public sealed class SlackAuthorizationOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:Authorization"</c>) the
    /// options are bound from.
    /// </summary>
    public const string SectionName = "Slack:Authorization";

    /// <summary>
    /// Default text returned in the ephemeral rejection message. Public
    /// so test fixtures can assert against the canonical value without
    /// duplicating the literal.
    /// </summary>
    public const string DefaultRejectionMessage =
        "Sorry, you are not authorized to use this AgentSwarm command in this channel.";

    /// <summary>
    /// Master switch. When <see langword="false"/> the filter is still
    /// registered but every request short-circuits to the next step
    /// without enforcement. Intended for non-production diagnostic
    /// flows; defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Text rendered in the ephemeral Slack message returned on every
    /// rejection. The brief mandates an ephemeral message body
    /// (<c>response_type=ephemeral</c>) on HTTP 200; the wording itself
    /// is operator-tunable for localisation and brand voice. Empty or
    /// whitespace strings fall back to <see cref="DefaultRejectionMessage"/>
    /// at runtime so a misconfigured section never produces an empty
    /// Slack reply.
    /// </summary>
    public string RejectionMessage { get; set; } = DefaultRejectionMessage;
}
