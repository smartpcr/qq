// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceSeedOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using System.Collections.Generic;

/// <summary>
/// Strongly-typed configuration that lets a Worker host seed the
/// in-memory <c>ISlackWorkspaceConfigStore</c> from
/// <c>appsettings.json</c> (or any other configuration provider) at
/// startup. Bound from the <c>Slack:Workspaces</c> section by
/// <see cref="SlackConnectorServiceCollectionExtensions.AddSlackWorkspaceConfigStoreFromConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.1 evaluator iter-1 item 4: "the Worker host defaults to empty
/// in-memory workspace/secret stores with no configuration path to seed a
/// workspace signing secret, so even after pipeline wiring the shipped
/// host cannot validate a real Slack request". This options bag closes
/// the workspace half of the gap; <see cref="SlackWorkspaceSeedEntry.SigningSecretRef"/>
/// still resolves through the <c>ISecretProvider</c> chain so the actual
/// HMAC key never lands in plaintext configuration.
/// </para>
/// <para>
/// Production deployments that have a real <c>slack_workspace_config</c>
/// table (Stage 2.3 EF Core-backed store) register their own
/// <c>ISlackWorkspaceConfigStore</c> BEFORE this extension is invoked;
/// the seed extension uses
/// <c>services.TryAddSingleton&lt;ISlackWorkspaceConfigStore, ...&gt;</c>
/// so the database-backed store always wins.
/// </para>
/// </remarks>
public sealed class SlackWorkspaceSeedOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:Workspaces"</c>) the options
    /// are bound from. Exposed as a constant so the extension method and
    /// tests can agree on the key without duplicating the literal.
    /// </summary>
    public const string SectionName = "Slack:Workspaces";

    /// <summary>
    /// Workspace seed entries. The list shape mirrors
    /// <c>appsettings.json</c>'s natural array binding so operators can
    /// add a workspace by appending a single object literal to
    /// <c>Slack:Workspaces</c>.
    /// </summary>
    public IList<SlackWorkspaceSeedEntry> Entries { get; set; } = new List<SlackWorkspaceSeedEntry>();
}

/// <summary>
/// One workspace's seed configuration. Field names match the canonical
/// <c>SlackWorkspaceConfig</c> entity so operators can copy a row from
/// the future EF-backed store directly into appsettings.
/// </summary>
public sealed class SlackWorkspaceSeedEntry
{
    /// <summary>Slack <c>team_id</c> (primary key in the audit and idempotency tables).</summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>Human-readable workspace name.</summary>
    public string WorkspaceName { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider reference for the bot OAuth token. The token
    /// itself is never inlined; the reference is resolved through
    /// <c>ISecretProvider</c> at use time.
    /// </summary>
    public string BotTokenSecretRef { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider reference for the workspace's signing secret. The
    /// signature validator resolves this via <c>ISecretProvider</c>
    /// before computing the HMAC -- the actual signing secret is never
    /// stored on the entity or in configuration.
    /// </summary>
    public string SigningSecretRef { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider reference for the optional Socket Mode app-level
    /// token. <see langword="null"/> when the workspace runs in Events
    /// API mode.
    /// </summary>
    public string? AppLevelTokenRef { get; set; }

    /// <summary>Channel into which new agent task threads are posted by default.</summary>
    public string DefaultChannelId { get; set; } = string.Empty;

    /// <summary>Failover channel, or <see langword="null"/> when none is configured.</summary>
    public string? FallbackChannelId { get; set; }

    /// <summary>
    /// Channels from which slash commands and interactions are accepted.
    /// An empty array rejects every channel (the authorization filter
    /// enforces this from Stage 3.2 onward).
    /// </summary>
    public string[] AllowedChannelIds { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Slack user-group IDs whose members may issue commands.
    /// </summary>
    public string[] AllowedUserGroupIds { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Whether this workspace is active. Disabled rows are kept in the
    /// store so a future re-enable does not lose configuration, but the
    /// validator and authorization filter reject inbound traffic for
    /// them.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
