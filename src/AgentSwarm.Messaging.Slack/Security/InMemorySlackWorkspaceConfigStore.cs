// -----------------------------------------------------------------------
// <copyright file="InMemorySlackWorkspaceConfigStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// In-memory <see cref="ISlackWorkspaceConfigStore"/> for tests and
/// development environments. The store keeps a thread-safe dictionary of
/// <c>team_id -&gt; SlackWorkspaceConfig</c> entries seeded at construction
/// time and updatable via <see cref="Upsert(SlackWorkspaceConfig)"/>.
/// </summary>
/// <remarks>
/// Stage 3.1 of <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The production implementation backed by EF Core is introduced together
/// with the upstream <c>MessagingDbContext</c>; the in-memory store keeps
/// the signature validator runnable in unit tests and in single-process
/// developer setups that do not yet have a database wired in.
/// </remarks>
public sealed class InMemorySlackWorkspaceConfigStore : ISlackWorkspaceConfigStore
{
    private readonly ConcurrentDictionary<string, SlackWorkspaceConfig> byTeamId;

    /// <summary>
    /// Creates an empty store.
    /// </summary>
    public InMemorySlackWorkspaceConfigStore()
        : this(seed: null)
    {
    }

    /// <summary>
    /// Creates a store seeded with the supplied workspace configurations.
    /// Subsequent <see cref="Upsert(SlackWorkspaceConfig)"/> calls replace
    /// matching entries by <see cref="SlackWorkspaceConfig.TeamId"/>.
    /// </summary>
    /// <param name="seed">
    /// Initial set of workspace configurations. <see langword="null"/> or
    /// empty leaves the store empty. Entries with a null, empty, or
    /// whitespace <see cref="SlackWorkspaceConfig.TeamId"/> are rejected.
    /// </param>
    public InMemorySlackWorkspaceConfigStore(IEnumerable<SlackWorkspaceConfig>? seed)
    {
        this.byTeamId = new ConcurrentDictionary<string, SlackWorkspaceConfig>(StringComparer.Ordinal);

        if (seed is null)
        {
            return;
        }

        foreach (SlackWorkspaceConfig entry in seed)
        {
            this.Upsert(entry);
        }
    }

    /// <summary>
    /// Adds or replaces a workspace configuration, keyed by
    /// <see cref="SlackWorkspaceConfig.TeamId"/>.
    /// </summary>
    public void Upsert(SlackWorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.TeamId))
        {
            throw new ArgumentException(
                $"{nameof(SlackWorkspaceConfig)}.{nameof(SlackWorkspaceConfig.TeamId)} must be non-empty.",
                nameof(config));
        }

        this.byTeamId[config.TeamId] = config;
    }

    /// <summary>
    /// Removes the configuration for <paramref name="teamId"/>, if present.
    /// </summary>
    public bool Remove(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return false;
        }

        return this.byTeamId.TryRemove(teamId, out _);
    }

    /// <inheritdoc />
    public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return Task.FromResult<SlackWorkspaceConfig?>(null);
        }

        ct.ThrowIfCancellationRequested();

        SlackWorkspaceConfig? found = this.byTeamId.TryGetValue(teamId, out SlackWorkspaceConfig? value)
            ? value
            : null;

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        List<SlackWorkspaceConfig> enabled = new();
        foreach (SlackWorkspaceConfig entry in this.byTeamId.Values)
        {
            if (entry.Enabled)
            {
                enabled.Add(entry);
            }
        }

        IReadOnlyCollection<SlackWorkspaceConfig> result = enabled;
        return Task.FromResult(result);
    }
}
