// -----------------------------------------------------------------------
// <copyright file="InMemorySlackAuditEntryWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Thread-safe in-process <see cref="ISlackAuditEntryWriter"/> for tests
/// and developer-laptop setups that do not yet have a relational
/// <c>MessagingDbContext</c> wired in. Retains every appended
/// <see cref="SlackAuditEntry"/> in insertion order.
/// </summary>
/// <remarks>
/// Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </remarks>
public sealed class InMemorySlackAuditEntryWriter : ISlackAuditEntryWriter
{
    private readonly ConcurrentQueue<SlackAuditEntry> entries = new();

    /// <summary>Snapshot of every appended entry, in insertion order.</summary>
    public IReadOnlyList<SlackAuditEntry> Entries => this.entries.ToArray();

    /// <inheritdoc />
    public Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        this.entries.Enqueue(entry);
        return Task.CompletedTask;
    }

    /// <summary>Removes every captured entry.</summary>
    public void Clear()
    {
        while (this.entries.TryDequeue(out _))
        {
            // intentional drain.
        }
    }
}
