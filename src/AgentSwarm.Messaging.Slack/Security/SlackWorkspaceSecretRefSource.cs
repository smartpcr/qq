// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceSecretRefSource.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// <see cref="ISecretRefSource"/> that surfaces every secret reference
/// held by the registered Slack workspace configurations
/// (<see cref="SlackWorkspaceConfig.BotTokenSecretRef"/>,
/// <see cref="SlackWorkspaceConfig.SigningSecretRef"/>, and
/// <see cref="SlackWorkspaceConfig.AppLevelTokenRef"/>) so the Stage 3.3
/// <c>SecretCacheWarmupHostedService</c> can resolve them at host
/// start-up and the Slack signature validator never sees a cold cache
/// on its first request.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// (architecture.md §7.3). The source enumerates the store via
/// <see cref="ISlackWorkspaceConfigStore.GetAllEnabledAsync"/> so only
/// secrets for currently-enabled workspaces are warmed -- disabled
/// rows would never have their secrets resolved at runtime and
/// warming them would generate spurious
/// <see cref="SecretNotFoundException"/> log noise for secrets that
/// are intentionally absent.
/// </para>
/// <para>
/// Stage 3.3 iter-3 evaluator item 2: each reference is tagged with
/// its <see cref="SecretRefRequirement"/> so the warmup hosted
/// service can fail closed on missing critical secrets without
/// crashing on optional Socket Mode tokens.
/// <see cref="SlackWorkspaceConfig.SigningSecretRef"/> +
/// <see cref="SlackWorkspaceConfig.BotTokenSecretRef"/> are
/// <see cref="SecretRefRequirement.Required"/> -- every Slack request
/// needs the signing secret for HMAC verification and every reply
/// needs the bot token for the Web API call.
/// <see cref="SlackWorkspaceConfig.AppLevelTokenRef"/> is
/// <see cref="SecretRefRequirement.Optional"/> because it is only
/// consumed by Socket Mode workspaces; HTTP Events API workspaces
/// leave it null and must still boot.
/// </para>
/// <para>
/// References that are <see langword="null"/> or whitespace are
/// skipped silently.
/// </para>
/// </remarks>
public sealed class SlackWorkspaceSecretRefSource : ISecretRefSource
{
    private readonly ISlackWorkspaceConfigStore store;

    /// <summary>Initializes a new instance bound to the supplied store.</summary>
    public SlackWorkspaceSecretRefSource(ISlackWorkspaceConfigStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SecretRefDescriptor> GetSecretRefsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        IReadOnlyCollection<SlackWorkspaceConfig> workspaces = await this.store
            .GetAllEnabledAsync(ct)
            .ConfigureAwait(false);

        foreach (SlackWorkspaceConfig workspace in workspaces)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(workspace.SigningSecretRef))
            {
                yield return SecretRefDescriptor.Required(workspace.SigningSecretRef);
            }

            if (!string.IsNullOrWhiteSpace(workspace.BotTokenSecretRef))
            {
                yield return SecretRefDescriptor.Required(workspace.BotTokenSecretRef);
            }

            if (!string.IsNullOrWhiteSpace(workspace.AppLevelTokenRef))
            {
                yield return SecretRefDescriptor.Optional(workspace.AppLevelTokenRef!);
            }
        }
    }
}

