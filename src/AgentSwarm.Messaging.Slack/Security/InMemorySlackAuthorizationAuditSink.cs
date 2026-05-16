// -----------------------------------------------------------------------
// <copyright file="InMemorySlackAuthorizationAuditSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe in-process <see cref="ISlackAuthorizationAuditSink"/> that
/// retains every rejection in an in-memory queue. Used by Stage 3.2
/// tests to assert the brief's "audit entry with outcome = rejected_auth"
/// requirement without dragging in the EF Core schema.
/// </summary>
/// <remarks>
/// Production deployments register <see cref="SlackAuditEntryAuthorizationSink"/>
/// (bridges into <c>ISlackAuditEntryWriter</c>); this implementation
/// exists so the authorization filter can be exercised in isolation.
/// </remarks>
public sealed class InMemorySlackAuthorizationAuditSink : ISlackAuthorizationAuditSink
{
    private readonly ConcurrentQueue<SlackAuthorizationAuditRecord> records = new();

    /// <summary>
    /// Snapshot of every record written so far, in insertion order.
    /// </summary>
    public IReadOnlyList<SlackAuthorizationAuditRecord> Records => this.records.ToArray();

    /// <inheritdoc />
    public Task WriteAsync(SlackAuthorizationAuditRecord record, CancellationToken ct)
    {
        if (record is null)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();
        this.records.Enqueue(record);
        return Task.CompletedTask;
    }

    /// <summary>Removes every captured record.</summary>
    public void Clear()
    {
        while (this.records.TryDequeue(out _))
        {
            // intentional drain.
        }
    }
}

/// <summary>
/// No-op implementation registered when no authorization audit sink is
/// wired in. Keeps the authorization filter runnable without forcing
/// every consumer to configure persistence.
/// </summary>
internal sealed class NullSlackAuthorizationAuditSink : ISlackAuthorizationAuditSink
{
    public static NullSlackAuthorizationAuditSink Instance { get; } = new();

    public Task WriteAsync(SlackAuthorizationAuditRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
