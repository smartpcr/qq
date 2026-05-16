// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceSeedBinder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Shared helpers that bind <see cref="SlackWorkspaceSeedOptions"/> from
/// the <see cref="SlackWorkspaceSeedOptions.SectionName"/> configuration
/// section and project the seed entries onto
/// <see cref="SlackWorkspaceConfig"/> instances. Used by both the
/// in-memory seed extension
/// (<see cref="SlackConnectorServiceCollectionExtensions.AddSlackWorkspaceConfigStoreFromConfiguration"/>)
/// and the EF-backed startup seeder (Stage 3.1 evaluator iter-3 item 1)
/// so the binding + validation contract is enforced consistently.
/// </summary>
public static class SlackWorkspaceSeedBinder
{
    /// <summary>
    /// Binds <see cref="SlackWorkspaceSeedOptions.Entries"/> on
    /// <paramref name="opts"/> from <paramref name="configuration"/>'s
    /// <see cref="SlackWorkspaceSeedOptions.SectionName"/> section.
    /// </summary>
    /// <remarks>
    /// Supports BOTH the flat array shape (<c>Slack:Workspaces: [ ... ]</c>)
    /// and the wrapped shape (<c>Slack:Workspaces: { Entries: [ ... ] }</c>)
    /// so operators are not surprised by either layout.
    /// </remarks>
    public static void BindEntries(IConfiguration configuration, SlackWorkspaceSeedOptions opts)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(opts);

        IConfigurationSection section = configuration.GetSection(SlackWorkspaceSeedOptions.SectionName);
        if (!section.Exists())
        {
            return;
        }

        IConfigurationSection entriesSection = section.GetSection("Entries");
        if (entriesSection.Exists())
        {
            List<SlackWorkspaceSeedEntry> wrapped = new();
            entriesSection.Bind(wrapped);
            opts.Entries = wrapped;
            return;
        }

        List<SlackWorkspaceSeedEntry> flat = new();
        section.Bind(flat);
        opts.Entries = flat;
    }

    /// <summary>
    /// Projects the validated seed entries onto a fresh
    /// <see cref="SlackWorkspaceConfig"/> sequence. Throws
    /// <see cref="InvalidOperationException"/> when any entry has a
    /// blank <see cref="SlackWorkspaceSeedEntry.TeamId"/> or
    /// <see cref="SlackWorkspaceSeedEntry.SigningSecretRef"/> (Stage 3.1
    /// evaluator iter-2 item 3: security-critical misconfiguration must
    /// fail loudly at startup, not degrade to UnknownWorkspace at
    /// request time).
    /// </summary>
    /// <param name="opts">Seed options whose entries are projected.</param>
    /// <returns>
    /// One <see cref="SlackWorkspaceConfig"/> per validated seed entry;
    /// an empty sequence when <paramref name="opts"/> is <c>null</c>,
    /// has no entries, or every slot is <c>null</c>.
    /// </returns>
    public static IEnumerable<SlackWorkspaceConfig> Materialize(SlackWorkspaceSeedOptions opts)
    {
        if (opts is null || opts.Entries is null || opts.Entries.Count == 0)
        {
            yield break;
        }

        DateTimeOffset seededAt = DateTimeOffset.UtcNow;
        for (int index = 0; index < opts.Entries.Count; index++)
        {
            SlackWorkspaceSeedEntry? entry = opts.Entries[index];
            if (entry is null)
            {
                // A null slot is treated as appsettings noise (trailing
                // comma, etc.); silently skipped.
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.TeamId))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Invalid {SlackWorkspaceSeedOptions.SectionName} configuration: entry at index {index} has a blank TeamId. Every seeded Slack workspace must declare a non-empty team_id; remove the entry or set Slack:Workspaces:{index}:TeamId to the workspace's Slack team identifier."));
            }

            if (string.IsNullOrWhiteSpace(entry.SigningSecretRef))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Invalid {SlackWorkspaceSeedOptions.SectionName} configuration: entry at index {index} (TeamId='{entry.TeamId}') has a blank SigningSecretRef. The signature validator cannot verify Slack requests for this workspace without a secret reference; set Slack:Workspaces:{index}:SigningSecretRef to a secret-provider URI (e.g. env://SLACK_SIGNING_{entry.TeamId})."));
            }

            yield return new SlackWorkspaceConfig
            {
                TeamId = entry.TeamId,
                WorkspaceName = entry.WorkspaceName ?? string.Empty,
                BotTokenSecretRef = entry.BotTokenSecretRef ?? string.Empty,
                SigningSecretRef = entry.SigningSecretRef,
                AppLevelTokenRef = entry.AppLevelTokenRef,
                DefaultChannelId = entry.DefaultChannelId ?? string.Empty,
                FallbackChannelId = entry.FallbackChannelId,
                AllowedChannelIds = entry.AllowedChannelIds ?? Array.Empty<string>(),
                AllowedUserGroupIds = entry.AllowedUserGroupIds ?? Array.Empty<string>(),
                Enabled = entry.Enabled,
                CreatedAt = seededAt,
                UpdatedAt = seededAt,
            };
        }
    }
}
