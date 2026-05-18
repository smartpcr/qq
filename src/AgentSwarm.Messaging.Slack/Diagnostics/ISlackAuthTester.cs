// -----------------------------------------------------------------------
// <copyright file="ISlackAuthTester.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Calls Slack's <c>auth.test</c> Web API for a single workspace,
/// hiding SlackNet's typed client and exception surface behind a
/// narrow result record so the
/// <see cref="SlackApiConnectivityHealthCheck"/> can be unit-tested
/// without round-tripping to Slack.
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
/// The default implementation
/// (<see cref="SlackNetAuthTester"/>) resolves each workspace's bot
/// OAuth token via the registered
/// <see cref="Security.ISlackWorkspaceConfigStore"/> and
/// <see cref="Core.Secrets.ISecretProvider"/> chain, then dispatches
/// the call through SlackNet's <c>ISlackApiClient.Auth.Test</c>
/// surface. SlackNet's <c>SlackException</c> /
/// <c>SlackRateLimitException</c> are caught and converted into the
/// <see cref="SlackAuthTestResult"/> failure variants so the health
/// check can describe the outcome without depending on
/// transport-specific exception types.
/// </para>
/// </remarks>
public interface ISlackAuthTester
{
    /// <summary>
    /// Issues an <c>auth.test</c> Slack Web API call for the workspace
    /// keyed by <paramref name="teamId"/>.
    /// </summary>
    /// <param name="teamId">Slack <c>team_id</c> (case-sensitive).</param>
    /// <param name="ct">Cancellation token honoured by the underlying client.</param>
    /// <returns>
    /// A <see cref="SlackAuthTestResult"/> describing the outcome.
    /// Implementations MUST NOT throw on a Slack-side failure --
    /// surface those through <see cref="SlackAuthTestResult.IsHealthy"/>
    /// instead so the health check can format a stable report.
    /// </returns>
    Task<SlackAuthTestResult> TestAsync(string teamId, CancellationToken ct);
}

/// <summary>
/// Result of a single <c>auth.test</c> probe issued by
/// <see cref="ISlackAuthTester"/>.
/// </summary>
/// <param name="TeamId">Workspace the call was issued against.</param>
/// <param name="IsHealthy">
/// <see langword="true"/> when Slack returned <c>{ok: true}</c>;
/// <see langword="false"/> on any failure (missing workspace,
/// missing bot token, transport error, Slack error, timeout).
/// </param>
/// <param name="Detail">
/// Human-readable description suitable for inclusion in the
/// health-check status payload. Never <see langword="null"/> --
/// the default implementation supplies a non-empty diagnostic for
/// every outcome (success or failure).
/// </param>
/// <param name="ErrorCode">
/// Optional Slack error code (e.g., <c>invalid_auth</c>,
/// <c>token_revoked</c>) when the failure originated as a Slack
/// API <c>{ok: false}</c> response. Absent for transport errors,
/// timeouts, and configuration failures.
/// </param>
public sealed record SlackAuthTestResult(
    string TeamId,
    bool IsHealthy,
    string Detail,
    string? ErrorCode = null);
