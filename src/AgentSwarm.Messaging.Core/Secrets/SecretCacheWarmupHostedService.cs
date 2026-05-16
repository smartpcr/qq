// -----------------------------------------------------------------------
// <copyright file="SecretCacheWarmupHostedService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Secrets;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// <see cref="IHostedService"/> that pre-loads every secret reference
/// surfaced by the registered <see cref="ISecretRefSource"/> set into
/// the <see cref="ISecretProvider"/> at host start-up. Implements the
/// "loaded into memory at connector startup" half of
/// architecture.md §7.3.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.3 iter-3 evaluator item 2: the previous iteration's
/// warmup swallowed <see cref="SecretNotFoundException"/> and every
/// generic <see cref="Exception"/>, which weakened the
/// "loaded into memory at connector startup" contract -- a missing
/// Slack signing secret would no-op at boot and only surface on the
/// first inbound request (often hours later, with no operator
/// nearby). This iteration fails closed: references annotated
/// <see cref="SecretRefRequirement.Required"/> that cannot be
/// resolved cause <see cref="StartAsync"/> to throw
/// <see cref="SecretCacheWarmupException"/> and the host does not
/// start. References annotated
/// <see cref="SecretRefRequirement.Optional"/> still log a warning
/// and allow the host to boot -- the per-request code path raises
/// <see cref="SecretNotFoundException"/> when the missing reference
/// is later requested.
/// </para>
/// <para>
/// The warmup runs sequentially per source so a slow backend cannot
/// fan out unbounded concurrent calls; sources are processed in
/// registration order. Every failure is collected before
/// <see cref="StartAsync"/> throws so the operator sees ALL missing
/// references in a single restart, not one at a time across many.
/// </para>
/// </remarks>
public sealed class SecretCacheWarmupHostedService : IHostedService
{
    private readonly ISecretProvider secretProvider;
    private readonly IEnumerable<ISecretRefSource> sources;
    private readonly ILogger<SecretCacheWarmupHostedService> logger;

    /// <summary>Initializes a new instance.</summary>
    public SecretCacheWarmupHostedService(
        ISecretProvider secretProvider,
        IEnumerable<ISecretRefSource> sources,
        ILogger<SecretCacheWarmupHostedService>? logger = null)
    {
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.logger = logger ?? NullLogger<SecretCacheWarmupHostedService>.Instance;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int loaded = 0;
        int optionalSkipped = 0;
        List<SecretCacheWarmupFailure> requiredFailures = new();

        foreach (ISecretRefSource source in this.sources)
        {
            await foreach (SecretRefDescriptor descriptor in source.GetSecretRefsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(descriptor.SecretRef))
                {
                    continue;
                }

                try
                {
                    _ = await this.secretProvider
                        .GetSecretAsync(descriptor.SecretRef, cancellationToken)
                        .ConfigureAwait(false);
                    loaded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (descriptor.Requirement == SecretRefRequirement.Required)
                    {
                        this.logger.LogError(
                            ex,
                            "Required secret '{SecretRef}' could not be resolved at startup; host will fail closed.",
                            descriptor.SecretRef);
                        requiredFailures.Add(new SecretCacheWarmupFailure(descriptor.SecretRef, ex));
                    }
                    else
                    {
                        optionalSkipped++;
                        this.logger.LogWarning(
                            ex,
                            "Optional secret '{SecretRef}' could not be resolved at startup; host will continue and the on-demand call path will retry.",
                            descriptor.SecretRef);
                    }
                }
            }
        }

        if (requiredFailures.Count > 0)
        {
            // Architecture.md §7.3: "secrets are loaded into memory at
            // connector startup". Required secrets that cannot be
            // loaded mean the connector cannot serve its first request,
            // so we MUST fail closed. Surface every failure together
            // so a partial misconfiguration (e.g., 3 of 5 workspaces
            // missing) shows up in ONE restart, not five.
            throw new SecretCacheWarmupException(requiredFailures);
        }

        this.logger.LogInformation(
            "Secret cache warmup complete: {Loaded} loaded, {OptionalSkipped} optional skipped.",
            loaded,
            optionalSkipped);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

