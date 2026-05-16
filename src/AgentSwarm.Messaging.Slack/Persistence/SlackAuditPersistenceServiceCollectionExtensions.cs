// -----------------------------------------------------------------------
// <copyright file="SlackAuditPersistenceServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// DI extensions that register the EF Core-backed
/// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/> as the
/// canonical <see cref="ISlackAuditEntryWriter"/>. Designed to be called
/// BEFORE <c>AddSlackSignatureValidation</c> so the validator's
/// <c>TryAddSingleton&lt;ISlackAuditEntryWriter, InMemorySlackAuditEntryWriter&gt;</c>
/// fallback is skipped and rejection audit rows are durably persisted
/// instead of being lost on process restart.
/// </summary>
/// <remarks>
/// Stage 3.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The composition root (typically the Worker) supplies the EF Core
/// <c>DbContext</c> + provider; this extension only wires the writer.
/// Keeping the provider choice outside the Slack project means a single
/// production deployment can swap SQLite for SQL Server without
/// recompiling the connector library.
/// </remarks>
public static class SlackAuditPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers
    /// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/> as the
    /// canonical <see cref="ISlackAuditEntryWriter"/>.
    /// </summary>
    /// <typeparam name="TContext">
    /// The EF Core context that implements
    /// <see cref="ISlackAuditEntryDbContext"/>. Must already be registered
    /// in <paramref name="services"/> as <c>Scoped</c> via
    /// <c>AddDbContext&lt;TContext&gt;</c> by the caller (or its derived
    /// context).
    /// </typeparam>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same collection for fluent chaining.</returns>
    public static IServiceCollection AddSlackEntityFrameworkAuditWriter<TContext>(
        this IServiceCollection services)
        where TContext : class, ISlackAuditEntryDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        // The EF writer is safe as a singleton because it resolves the
        // (scoped) DbContext from a fresh IServiceScope per AppendAsync
        // call. Registering as singleton means it wins the
        // TryAddSingleton<ISlackAuditEntryWriter, InMemorySlackAuditEntryWriter>
        // call inside AddSlackSignatureValidation regardless of order:
        // singleton TryAdd checks the service type, not the lifetime.
        services.TryAddSingleton<EntityFrameworkSlackAuditEntryWriter<TContext>>();
        services.RemoveAll<ISlackAuditEntryWriter>();
        services.AddSingleton<ISlackAuditEntryWriter>(sp =>
            sp.GetRequiredService<EntityFrameworkSlackAuditEntryWriter<TContext>>());

        return services;
    }
}
