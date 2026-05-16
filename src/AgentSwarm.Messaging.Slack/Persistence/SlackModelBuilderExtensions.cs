// -----------------------------------------------------------------------
// <copyright file="SlackModelBuilderExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Public registration hook -- the Stage 2.3 "migration contribution"
/// surface -- for adding every Slack entity type configuration to an
/// arbitrary <see cref="ModelBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// <para>
/// The hook scans the Slack assembly for every
/// <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{TEntity}"/>
/// implementation via
/// <see cref="ModelBuilderExtensions.ApplyConfigurationsFromAssembly(ModelBuilder, System.Reflection.Assembly, System.Func{System.Type, bool}?)"/>.
/// As of Stage 2.3 those implementations are:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="SlackWorkspaceConfigConfiguration"/></description></item>
///   <item><description><see cref="SlackThreadMappingConfiguration"/></description></item>
///   <item><description><see cref="SlackInboundRequestRecordConfiguration"/></description></item>
///   <item><description><see cref="SlackAuditEntryConfiguration"/></description></item>
/// </list>
/// <para>
/// New Slack entity configurations added in later stages are picked up
/// automatically -- callers do not need to re-wire the hook.
/// </para>
/// <para><b>Integration with upstream <c>MessagingDbContext</c>.</b> The
/// Persistence project cannot reference the Slack project (the dependency
/// direction is Slack -> Persistence). Consequently the upstream
/// <c>MessagingDbContext</c> consumes this hook via the composition root
/// (typically the Worker), either through a contributor abstraction
/// registered in DI or by being constructed with an explicit
/// <c>Action&lt;ModelBuilder&gt;</c> that calls
/// <c>builder.AddSlackEntities()</c>. The Slack test harness invokes the
/// hook directly from
/// <c>AgentSwarm.Messaging.Slack.Tests.Persistence.SlackTestDbContext.OnModelCreating</c>.</para>
/// </remarks>
public static class SlackModelBuilderExtensions
{
    /// <summary>
    /// Registers every Slack entity type configuration with the supplied
    /// <see cref="ModelBuilder"/>.
    /// </summary>
    /// <param name="modelBuilder">
    /// The model builder to register configurations on. Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The same <paramref name="modelBuilder"/> instance so calls can be
    /// chained inside <c>OnModelCreating</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="modelBuilder"/> is <see langword="null"/>.
    /// </exception>
    public static ModelBuilder AddSlackEntities(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(SlackWorkspaceConfigConfiguration).Assembly);

        return modelBuilder;
    }
}
