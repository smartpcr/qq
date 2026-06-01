// -----------------------------------------------------------------------
// <copyright file="ISlackRateLimiter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Shared per-tier token-bucket rate limiter that gates every Slack
/// Web API call routed through the durable outbound queue
/// (<see cref="SlackOutboundDispatcher"/>) AND the synchronous modal
/// fast-path (<c>SlackDirectApiClient</c> -- Stage 6.4). Honouring a
/// single shared limiter is what lets the two callers stay within
/// Slack's published tier ceilings without each having to model the
/// other's call rate (architecture.md §2.12, §2.15).
/// </summary>
/// <remarks>
/// <para>
/// Stage 6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 5. Implementations bind one token bucket per
/// (<see cref="SlackApiTier"/>, <c>scopeKey</c>) pair -- the scope
/// key is "team_id:channel_id" for Tier 2 (per-channel) calls and
/// "team_id" for the workspace-scoped tiers.
/// </para>
/// <para>
/// <see cref="NotifyRetryAfter"/> is the dispatcher's mechanism for
/// surfacing an HTTP 429 response into the limiter: the bucket for
/// the affected scope is held closed for the supplied delay so the
/// next <see cref="AcquireAsync"/> on the same scope blocks until
/// Slack's published <c>Retry-After</c> window elapses. The pause is
/// idempotent and monotonic -- a later, longer pause replaces an
/// earlier shorter one; a shorter pause never shortens an existing
/// longer one.
/// </para>
/// </remarks>
internal interface ISlackRateLimiter
{
    /// <summary>
    /// Blocks (asynchronously) until a token is available for the
    /// supplied <paramref name="tier"/> + <paramref name="scopeKey"/>
    /// bucket. Throws <see cref="System.OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled while waiting.
    /// </summary>
    /// <param name="tier">Slack rate-limit tier the call falls under.</param>
    /// <param name="scopeKey">
    /// Stable identifier of the bucket scope. Callers MUST follow the
    /// convention "<c>team_id:channel_id</c>" for per-channel tiers
    /// (Tier 2) and "<c>team_id</c>" for per-workspace tiers; the
    /// limiter is opaque to the format and treats the value as a
    /// case-sensitive ordinal key.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask AcquireAsync(SlackApiTier tier, string scopeKey, CancellationToken ct);

    /// <summary>
    /// Surfaces an HTTP 429 response into the limiter: the bucket for
    /// (<paramref name="tier"/>, <paramref name="scopeKey"/>) is held
    /// closed until <c>now + <paramref name="delay"/></c>. Subsequent
    /// <see cref="AcquireAsync"/> calls on the same bucket block
    /// until that deadline passes.
    /// </summary>
    /// <param name="tier">Tier the rate-limit response was observed on.</param>
    /// <param name="scopeKey">Scope key the rate-limit response was observed on.</param>
    /// <param name="delay">
    /// Duration to pause the bucket. Typically derived from the
    /// <c>Retry-After</c> header on the 429 response; the caller is
    /// responsible for any upper-bound clamping it wishes to apply
    /// before invoking this method.
    /// </param>
    void NotifyRetryAfter(SlackApiTier tier, string scopeKey, TimeSpan delay);
}
