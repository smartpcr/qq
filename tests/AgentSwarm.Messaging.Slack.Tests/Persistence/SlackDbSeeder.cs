// -----------------------------------------------------------------------
// <copyright file="SlackDbSeeder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Test-only seed helper that inserts a deterministic sample
/// <see cref="SlackWorkspaceConfig"/> row into any <see cref="DbContext"/>
/// that maps the Slack entity set. Intended for use by Stage 2.3 and
/// later integration tests that need a baseline workspace before
/// exercising authorization, threading, idempotency, audit, or outbound
/// flows.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </para>
/// <para>
/// The helper accepts a <see cref="DbContext"/> rather than the
/// test-specific <see cref="SlackTestDbContext"/> so the same call site
/// can target the future upstream <c>MessagingDbContext</c> once it lands
/// in the Persistence project. Access is mediated through
/// <see cref="DbContext.Set{TEntity}()"/>, which resolves to whatever
/// <c>DbSet</c> the supplied context exposes for
/// <see cref="SlackWorkspaceConfig"/>.
/// </para>
/// <para>
/// Timestamps are deterministic (anchored at
/// <see cref="SampleCreatedAt"/> / <see cref="SampleUpdatedAt"/>) so
/// downstream assertions can compare the persisted row without inheriting
/// wall-clock nondeterminism.
/// </para>
/// </remarks>
public static class SlackDbSeeder
{
    /// <summary>
    /// Default <see cref="SlackWorkspaceConfig.TeamId"/> assigned when
    /// the caller does not supply an override.
    /// </summary>
    public const string SampleTeamId = "T0SAMPLE01";

    /// <summary>Default workspace display name.</summary>
    public const string SampleWorkspaceName = "Sample Test Workspace";

    /// <summary>Default secret-provider URI for the bot OAuth token.</summary>
    public const string SampleBotTokenSecretRef = "keyvault://slack/sample/bot-token";

    /// <summary>Default secret-provider URI for the signing secret.</summary>
    public const string SampleSigningSecretRef = "keyvault://slack/sample/signing-secret";

    /// <summary>Default channel that agent task threads are posted to.</summary>
    public const string SampleDefaultChannelId = "C-SAMPLE-DEFAULT";

    /// <summary>Deterministic <see cref="SlackWorkspaceConfig.CreatedAt"/>.</summary>
    public static readonly DateTimeOffset SampleCreatedAt =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Deterministic <see cref="SlackWorkspaceConfig.UpdatedAt"/>.</summary>
    public static readonly DateTimeOffset SampleUpdatedAt =
        new(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] SampleAllowedChannelIds =
        new[] { SampleDefaultChannelId, "C-SAMPLE-ENG" };

    private static readonly string[] SampleAllowedUserGroupIds =
        new[] { "S-SAMPLE-LEADS" };

    /// <summary>
    /// Builds an in-memory <see cref="SlackWorkspaceConfig"/> with the
    /// sample fixture values. The row is NOT persisted; callers that
    /// want persistence should use
    /// <see cref="SeedTestWorkspace(DbContext, string?)"/>.
    /// </summary>
    /// <param name="teamId">
    /// Override for <see cref="SlackWorkspaceConfig.TeamId"/>, or
    /// <see langword="null"/> to use <see cref="SampleTeamId"/>.
    /// </param>
    /// <returns>The constructed sample workspace.</returns>
    public static SlackWorkspaceConfig CreateSampleWorkspace(string? teamId = null) => new()
    {
        TeamId = teamId ?? SampleTeamId,
        WorkspaceName = SampleWorkspaceName,
        BotTokenSecretRef = SampleBotTokenSecretRef,
        SigningSecretRef = SampleSigningSecretRef,
        AppLevelTokenRef = null,
        DefaultChannelId = SampleDefaultChannelId,
        FallbackChannelId = null,
        AllowedChannelIds = (string[])SampleAllowedChannelIds.Clone(),
        AllowedUserGroupIds = (string[])SampleAllowedUserGroupIds.Clone(),
        Enabled = true,
        CreatedAt = SampleCreatedAt,
        UpdatedAt = SampleUpdatedAt,
    };

    /// <summary>
    /// Inserts a deterministic sample <see cref="SlackWorkspaceConfig"/>
    /// row into <paramref name="db"/> and saves changes synchronously.
    /// </summary>
    /// <param name="db">
    /// The target <see cref="DbContext"/>. Must map a
    /// <see cref="DbSet{TEntity}"/> of <see cref="SlackWorkspaceConfig"/>
    /// (the Slack entity configurations supply the mapping when
    /// <see cref="SlackModelBuilderExtensions.AddSlackEntities(ModelBuilder)"/>
    /// has been called on the context's model builder).
    /// </param>
    /// <param name="teamId">
    /// Override for <see cref="SlackWorkspaceConfig.TeamId"/>, or
    /// <see langword="null"/> to use <see cref="SampleTeamId"/>.
    /// </param>
    /// <returns>The persisted workspace row.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="db"/> is <see langword="null"/>.
    /// </exception>
    public static SlackWorkspaceConfig SeedTestWorkspace(
        DbContext db,
        string? teamId = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        SlackWorkspaceConfig workspace = CreateSampleWorkspace(teamId);
        db.Set<SlackWorkspaceConfig>().Add(workspace);
        db.SaveChanges();
        return workspace;
    }
}
