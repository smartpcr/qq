// -----------------------------------------------------------------------
// <copyright file="InMemorySlackSignatureAuditSink.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Thread-safe in-process <see cref="ISlackSignatureAuditSink"/> that
/// retains every rejection in an in-memory queue. Used by Stage 3.1 tests
/// to assert the brief's "audit entry with outcome = rejected_signature"
/// requirement without dragging in the EF Core schema.
/// </summary>
/// <remarks>
/// Production deployments register the database-backed sink introduced by
/// the audit-pipeline stage; this implementation exists so the signature
/// validator can be exercised in isolation.
/// </remarks>
public sealed class InMemorySlackSignatureAuditSink : ISlackSignatureAuditSink
{
    private readonly ConcurrentQueue<SlackSignatureAuditRecord> records = new();

    /// <summary>
    /// Snapshot of every record written so far, in insertion order.
    /// </summary>
    public IReadOnlyList<SlackSignatureAuditRecord> Records => this.records.ToArray();

    /// <inheritdoc />
    public Task WriteAsync(SlackSignatureAuditRecord record, CancellationToken ct)
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
/// No-op implementation registered when no audit sink is wired in. Keeps
/// the signature validator runnable without forcing every consumer to
/// configure persistence.
/// </summary>
internal sealed class NullSlackSignatureAuditSink : ISlackSignatureAuditSink
{
    public static NullSlackSignatureAuditSink Instance { get; } = new();

    public Task WriteAsync(SlackSignatureAuditRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
