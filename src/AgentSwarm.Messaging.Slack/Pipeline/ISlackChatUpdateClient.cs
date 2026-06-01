// -----------------------------------------------------------------------
// <copyright file="ISlackChatUpdateClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Best-effort Stage 5.3 contract for the Slack <c>chat.update</c>
/// Web API call the
/// <see cref="SlackInteractionHandler"/> issues after publishing a
/// <see cref="AgentSwarm.Messaging.Abstractions.HumanDecisionEvent"/>.
/// Implementations MUST swallow non-fatal network / Slack errors; the
/// handler's primary contract is the decision publish, not the visual
/// update.
/// </summary>
/// <remarks>
/// <para>
/// Architecture.md §5.2 step 8 says "the original message is updated
/// (<c>chat.update</c>) to disable the buttons and show the decision".
/// Stage 5.3's implementation step 6 mirrors that requirement. The
/// production binding will be Stage 6.4's <c>SlackDirectApiClient</c>;
/// in the meantime the Stage 5.3 DI extension wires
/// <see cref="HttpClientSlackChatUpdateClient"/> as the default so the
/// brief-mandated Web API call actually happens in production.
/// </para>
/// </remarks>
internal interface ISlackChatUpdateClient
{
    /// <summary>
    /// POSTs <paramref name="request"/> to Slack's <c>chat.update</c>
    /// endpoint. Returns the outcome so the handler can write a single
    /// audit line; throws ONLY for caller cancellation.
    /// </summary>
    Task<SlackChatUpdateResult> UpdateAsync(SlackChatUpdateRequest request, CancellationToken ct);
}

/// <summary>
/// Input bundle for <see cref="ISlackChatUpdateClient.UpdateAsync"/>.
/// </summary>
/// <param name="TeamId">Slack workspace id (used by the implementation
/// to resolve the bot OAuth token via the per-workspace secret).</param>
/// <param name="ChannelId">Slack channel id of the message to update.</param>
/// <param name="MessageTs">Slack <c>message.ts</c> identifying the
/// message inside the channel.</param>
/// <param name="Text">Plain-text fallback (Slack renders this when the
/// client cannot render the blocks).</param>
/// <param name="Blocks">Replacement Block Kit blocks (object node).
/// The Stage 5.3 handler supplies a section block that visually
/// disables the original buttons -- e.g.,
/// "*Approved* by &lt;@U1&gt;".</param>
internal readonly record struct SlackChatUpdateRequest(
    string TeamId,
    string ChannelId,
    string MessageTs,
    string Text,
    object Blocks);

/// <summary>
/// Result of an <see cref="ISlackChatUpdateClient.UpdateAsync"/> call.
/// </summary>
internal readonly record struct SlackChatUpdateResult(
    SlackChatUpdateResultKind Kind,
    string? Error)
{
    public bool IsSuccess => this.Kind == SlackChatUpdateResultKind.Ok;

    public static SlackChatUpdateResult Success() => new(SlackChatUpdateResultKind.Ok, null);

    public static SlackChatUpdateResult Failure(string error)
        => new(SlackChatUpdateResultKind.SlackError, error);

    public static SlackChatUpdateResult NetworkFailure(string error)
        => new(SlackChatUpdateResultKind.NetworkFailure, error);

    public static SlackChatUpdateResult MissingConfiguration(string error)
        => new(SlackChatUpdateResultKind.MissingConfiguration, error);

    public static SlackChatUpdateResult Skipped(string reason)
        => new(SlackChatUpdateResultKind.Skipped, reason);
}

/// <summary>Discriminator on <see cref="SlackChatUpdateResult"/>.</summary>
internal enum SlackChatUpdateResultKind
{
    /// <summary>Slack accepted the update.</summary>
    Ok = 0,

    /// <summary>Slack accepted the HTTP request but returned <c>{"ok":false}</c>.</summary>
    SlackError = 1,

    /// <summary>Transport error (timeout, DNS, TLS, 5xx).</summary>
    NetworkFailure = 2,

    /// <summary>The workspace lacks a bot-token secret reference or the secret resolves to empty.</summary>
    MissingConfiguration = 3,

    /// <summary>The handler chose not to call <c>chat.update</c> (e.g., no <c>message.ts</c> was carried on the interaction).</summary>
    Skipped = 4,
}
