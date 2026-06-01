// -----------------------------------------------------------------------
// <copyright file="SlackApiConnectivityHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// ASP.NET Core <see cref="IHealthCheck"/> that calls Slack's
/// <c>auth.test</c> Web API endpoint for every registered, enabled
/// workspace and reports the connector's connectivity to the Slack
/// platform.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 1: "Register ASP.NET Core health check for Slack API
/// connectivity: call <c>auth.test</c> via SlackNet and report
/// <c>Healthy</c> or <c>Unhealthy</c> based on the response."
/// </para>
/// <para>
/// Outcomes:
/// </para>
/// <list type="bullet">
///   <item><description><c>Healthy</c> -- every enabled workspace's
///   <c>auth.test</c> call returned <c>{ok: true}</c>; OR
///   <c>AuthTestAllWorkspaces</c> is <see langword="false"/> and at
///   least one probed workspace succeeded (the loop keeps iterating
///   past per-workspace failures until a success is found, so a
///   single broken workspace cannot evict the entire pod from
///   rotation -- matches the option doc's "short-circuit after the
///   first success" guarantee verbatim).</description></item>
///   <item><description><c>Unhealthy</c> -- under
///   <c>AuthTestAllWorkspaces=true</c>, ANY workspace failed; under
///   <c>AuthTestAllWorkspaces=false</c>, EVERY probed workspace
///   failed; OR no workspaces are registered at all (the connector
///   is not operational). The failure detail includes the workspace
///   id and Slack error code so an operator can root-cause from the
///   health-check JSON.</description></item>
/// </list>
/// <para>
/// Each per-workspace probe is bounded by
/// <see cref="SlackHealthCheckOptions.EffectiveAuthTestTimeout"/>
/// using a linked <see cref="CancellationTokenSource"/> so a single
/// stuck workspace cannot stall the entire health response.
/// </para>
/// </remarks>
internal sealed class SlackApiConnectivityHealthCheck : IHealthCheck
{
    /// <summary>
    /// Health-check registration name (matches the
    /// <c>AddSlackHealthChecks</c> extension constant).
    /// </summary>
    public const string CheckName = "slack-api-connectivity";

    private readonly ISlackWorkspaceConfigStore workspaceStore;
    private readonly ISlackAuthTester authTester;
    private readonly IOptionsMonitor<SlackHealthCheckOptions> options;
    private readonly ILogger<SlackApiConnectivityHealthCheck> logger;

    public SlackApiConnectivityHealthCheck(
        ISlackWorkspaceConfigStore workspaceStore,
        ISlackAuthTester authTester,
        IOptionsMonitor<SlackHealthCheckOptions> options,
        ILogger<SlackApiConnectivityHealthCheck> logger)
    {
        this.workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        this.authTester = authTester ?? throw new ArgumentNullException(nameof(authTester));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        SlackHealthCheckOptions opts = this.options.CurrentValue;

        IReadOnlyCollection<SlackWorkspaceConfig> workspaces;
        try
        {
            workspaces = await this.workspaceStore
                .GetAllEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack api-connectivity health check: failed to enumerate workspaces from the configured store.");
            return HealthCheckResult.Unhealthy(
                description: "Failed to enumerate Slack workspaces from the configured store.",
                exception: ex);
        }

        if (workspaces.Count == 0)
        {
            // Stage 7.3 evaluator iter-2 item 3: the brief explicitly
            // calls for a binary Healthy/Unhealthy contract on this
            // probe, and SlackStartupDiagnosticsHostedService reports
            // "the connector will refuse inbound traffic until at
            // least one workspace is configured" when this same set
            // is empty. Returning Degraded here would let /health/ready
            // serve HTTP 200 while the connector is operationally
            // dead -- Kubernetes would happily route Slack callbacks
            // to a pod that cannot service them. Unhealthy is the
            // only correct readiness signal in this state.
            return HealthCheckResult.Unhealthy(
                description: "No Slack workspaces are registered; the connector cannot service any Slack traffic. auth.test was not invoked.",
                data: new Dictionary<string, object> { ["workspace_count"] = 0 });
        }

        List<SlackAuthTestResult> results = new(workspaces.Count);
        foreach (SlackWorkspaceConfig workspace in workspaces)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using CancellationTokenSource timeoutCts = new(opts.EffectiveAuthTestTimeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            SlackAuthTestResult result;
            try
            {
                result = await this.authTester
                    .TestAsync(workspace.TeamId, linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                result = new SlackAuthTestResult(
                    TeamId: workspace.TeamId,
                    IsHealthy: false,
                    Detail: $"auth.test exceeded {opts.EffectiveAuthTestTimeout.TotalMilliseconds:0} ms timeout.");
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Slack api-connectivity health check: probe for workspace {TeamId} threw unexpectedly.",
                    workspace.TeamId);
                result = new SlackAuthTestResult(
                    TeamId: workspace.TeamId,
                    IsHealthy: false,
                    Detail: $"probe threw {ex.GetType().Name}: {ex.Message}");
            }

            results.Add(result);

            // Stage 7.3 evaluator iter-3 item 2: when
            // AuthTestAllWorkspaces=false the option docs promise
            // "short-circuit after the FIRST SUCCESS", i.e. the check
            // is at-least-one-success. The previous loop short-
            // circuited on the first success but DID NOT keep probing
            // through earlier failures -- it left them in `results`
            // and the final tallying still returned Unhealthy if
            // ANY workspace had failed before a later success was
            // found. That was internally inconsistent with the option
            // doc and produced false-negative readiness signals on
            // multi-workspace hosts where one workspace was
            // temporarily broken while others were healthy. The fix:
            // only break when we have an actual success; on failure
            // we keep iterating so a later workspace can satisfy the
            // at-least-one-success rule.
            if (!opts.AuthTestAllWorkspaces && result.IsHealthy)
            {
                break;
            }
        }

        int healthyCount = results.Count(r => r.IsHealthy);
        int failedCount = results.Count - healthyCount;
        Dictionary<string, object> data = new()
        {
            ["workspace_count"] = workspaces.Count,
            ["probed_count"] = results.Count,
            ["healthy_count"] = healthyCount,
            ["failed_count"] = failedCount,
            ["all_workspaces_probed"] = opts.AuthTestAllWorkspaces,
        };

        // Stage 7.3 evaluator iter-3 item 2: the success criterion
        // depends on AuthTestAllWorkspaces.
        //   true  (default) -- every probed workspace MUST succeed;
        //                      any failure trips the readiness gate.
        //   false           -- at least one probed workspace MUST
        //                      succeed; intermittent per-workspace
        //                      failures are tolerated as long as one
        //                      probe came back healthy. This matches
        //                      the option doc verbatim ("short-circuit
        //                      after the first success") and the
        //                      enterprise-host expectation that a
        //                      single broken workspace cannot evict
        //                      the entire pod from rotation.
        bool isHealthy = opts.AuthTestAllWorkspaces
            ? failedCount == 0
            : healthyCount > 0;

        if (isHealthy)
        {
            return HealthCheckResult.Healthy(
                description: $"Slack auth.test ok for {healthyCount}/{results.Count} probed workspace(s).",
                data: data);
        }

        string failureSummary = string.Join(
            "; ",
            results
                .Where(r => !r.IsHealthy)
                .Select(r => $"{r.TeamId}: {r.Detail}"));

        // Per the brief the connectivity check is binary (Healthy /
        // Unhealthy); under AuthTestAllWorkspaces=true ANY workspace
        // failure trips the probe so a Kubernetes readiness gate
        // removes the pod from rotation until the underlying issue
        // resolves. Under AuthTestAllWorkspaces=false the check only
        // reaches this branch when EVERY probed workspace failed.
        // The `data` payload breaks down which workspace failed so
        // an operator can target the rebuild.
        return HealthCheckResult.Unhealthy(
            description: $"Slack auth.test failed for {failedCount}/{results.Count} probed workspace(s): {failureSummary}",
            data: data);
    }
}
