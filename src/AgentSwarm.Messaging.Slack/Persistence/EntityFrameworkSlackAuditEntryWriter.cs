// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackAuditEntryWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// EF Core-backed <see cref="ISlackAuditEntryWriter"/>. Appends a single
/// <see cref="SlackAuditEntry"/> via a freshly-scoped
/// <typeparamref name="TContext"/> resolved from
/// <see cref="IServiceScopeFactory"/>.
/// </summary>
/// <typeparam name="TContext">
/// The upstream <c>MessagingDbContext</c> (or any application context)
/// that implements <see cref="ISlackAuditEntryDbContext"/> and is
/// registered in DI as <c>Scoped</c> via
/// <c>AddDbContext&lt;TContext&gt;(...)</c>.
/// </typeparam>
/// <remarks>
/// <para>
/// The writer itself is safe to register as a <em>singleton</em>: it
/// creates a fresh DI scope per <see cref="AppendAsync"/> call and resolves
/// <typeparamref name="TContext"/> from that scope, so the context's
/// per-request lifetime (and EF Core's threading model) are respected
/// even though the consuming <see cref="SlackAuditEntrySignatureSink"/>
/// is also a singleton. This avoids the "captive dependency" trap where a
/// singleton sink would otherwise hold a stale scoped DbContext for the
/// process lifetime.
/// </para>
/// <para>
/// Stage 3.1 (per
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>)
/// registers this writer as the canonical <see cref="ISlackAuditEntryWriter"/>
/// in the Worker host so signature rejections are durably persisted to
/// <c>slack_audit_entry</c> instead of the in-memory diagnostic store.
/// </para>
/// </remarks>
public sealed class EntityFrameworkSlackAuditEntryWriter<TContext> : ISlackAuditEntryWriter
    where TContext : class, ISlackAuditEntryDbContext
{
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>
    /// Creates a writer that resolves <typeparamref name="TContext"/>
    /// from a fresh DI scope per <see cref="AppendAsync"/> call.
    /// </summary>
    public EntityFrameworkSlackAuditEntryWriter(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task AppendAsync(SlackAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();
        context.SlackAuditEntries.Add(entry);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
