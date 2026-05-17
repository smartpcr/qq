// -----------------------------------------------------------------------
// <copyright file="SlackAuditLoggerServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Stage 7.1 DI extensions that register the EF Core-backed
/// <see cref="SlackAuditLogger{TContext}"/> as the canonical
/// <see cref="ISlackAuditLogger"/> AND
/// <see cref="ISlackAuditEntryWriter"/>, plus the
/// <see cref="SlackRetentionCleanupService{TContext}"/> background
/// purge job. Designed to be called instead of (or after) the Stage
/// 3.1 <c>AddSlackEntityFrameworkAuditWriter</c> so the logger wins
/// every audit write seam in the connector pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// </remarks>
public static class SlackAuditLoggerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SlackAuditLogger{TContext}"/> as the
    /// canonical <see cref="ISlackAuditLogger"/> AND
    /// <see cref="ISlackAuditEntryWriter"/> (single instance, dual
    /// interface) plus the cleanup background service.
    /// </summary>
    /// <typeparam name="TContext">
    /// EF Core context implementing both
    /// <see cref="ISlackAuditEntryDbContext"/> and
    /// <see cref="ISlackInboundRequestRecordDbContext"/>.
    /// Must already be registered as <c>Scoped</c> via
    /// <c>AddDbContext&lt;TContext&gt;</c>.
    /// </typeparam>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">
    /// Configuration root used to bind
    /// <see cref="SlackRetentionOptions"/> from
    /// <see cref="SlackRetentionOptions.SectionName"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSlackAuditLogger<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : Microsoft.EntityFrameworkCore.DbContext, ISlackAuditEntryDbContext, ISlackInboundRequestRecordDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind options eagerly so misconfiguration (e.g. zero or
        // negative RetentionDays) fails at host start, not at the
        // first sweep tick.
        services
            .AddOptions<SlackRetentionOptions>()
            .Bind(configuration.GetSection(SlackRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                opts => opts.SweepInterval > TimeSpan.Zero,
                $"{nameof(SlackRetentionOptions)}.{nameof(SlackRetentionOptions.SweepInterval)} must be greater than zero.");

        // The logger is safe as a singleton because it resolves the
        // (scoped) DbContext from a fresh IServiceScope per call. The
        // SAME instance is registered against both ISlackAuditLogger
        // and ISlackAuditEntryWriter so existing pipeline call sites
        // (signature, authorization, idempotency, command dispatch,
        // interaction handling, modal open, outbound dispatch, thread
        // manager, DirectApiClient) automatically route through
        // SlackAuditLogger.LogAsync via the explicit AppendAsync
        // implementation on SlackAuditLogger<TContext>.
        services.TryAddSingleton<SlackAuditLogger<TContext>>();

        services.RemoveAll<ISlackAuditLogger>();
        services.AddSingleton<ISlackAuditLogger>(sp =>
            sp.GetRequiredService<SlackAuditLogger<TContext>>());

        services.RemoveAll<ISlackAuditEntryWriter>();
        services.AddSingleton<ISlackAuditEntryWriter>(sp =>
            sp.GetRequiredService<SlackAuditLogger<TContext>>());

        services.AddHostedService<SlackRetentionCleanupService<TContext>>();

        return services;
    }
}
