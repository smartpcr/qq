// -----------------------------------------------------------------------
// <copyright file="ISlackViewsOpenClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stage 4.1 contract for the synchronous <c>views.open</c> call made
/// by the modal fast-path. Stage 6.4's
/// <c>SlackDirectApiClient</c> will provide the production
/// SlackNet-backed implementation; Stage 4.1 ships
/// <see cref="HttpClientSlackViewsOpenClient"/> as the default so the
/// fast-path can actually open modals today.
/// </summary>
/// <remarks>
/// Kept narrow (one method, simple result struct) so Stage 6.4 can
/// drop in a richer client without rewriting the
/// <see cref="DefaultSlackModalFastPathHandler"/>.
/// </remarks>
internal interface ISlackViewsOpenClient
{
    /// <summary>
    /// POSTs <paramref name="request"/> to Slack's <c>views.open</c>
    /// Web API endpoint synchronously. Implementations MUST complete
    /// well within Slack's <c>trigger_id</c> expiry window
    /// (~3 seconds) or return
    /// <see cref="SlackViewsOpenResultKind.NetworkFailure"/>.
    /// </summary>
    Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct);
}

/// <summary>
/// Input bundle for <see cref="ISlackViewsOpenClient.OpenAsync"/>.
/// </summary>
/// <param name="TeamId">Slack workspace identifier (used by the
/// implementation to resolve the bot OAuth token via
/// <see cref="Core.Secrets.ISecretProvider"/>).</param>
/// <param name="TriggerId">Slack-issued <c>trigger_id</c> from the
/// originating slash command. Expires within approximately three
/// seconds of issuance.</param>
/// <param name="ViewPayload">Raw Slack view JSON
/// (object node). Implementations serialize it as the <c>view</c>
/// property of the request body.</param>
internal readonly record struct SlackViewsOpenRequest(
    string TeamId,
    string TriggerId,
    object ViewPayload);

/// <summary>
/// Result of the Slack <c>views.open</c> Web API call. Distinguishes
/// the three classes of failure the modal fast-path needs to react
/// to (network, Slack-side error, missing configuration).
/// </summary>
internal readonly record struct SlackViewsOpenResult(
    SlackViewsOpenResultKind Kind,
    string? Error)
{
    public bool IsSuccess => this.Kind == SlackViewsOpenResultKind.Ok;

    public static SlackViewsOpenResult Success() => new(SlackViewsOpenResultKind.Ok, null);

    public static SlackViewsOpenResult Failure(string error)
        => new(SlackViewsOpenResultKind.SlackError, error);

    public static SlackViewsOpenResult NetworkFailure(string error)
        => new(SlackViewsOpenResultKind.NetworkFailure, error);

    public static SlackViewsOpenResult MissingConfiguration(string error)
        => new(SlackViewsOpenResultKind.MissingConfiguration, error);
}

/// <summary>
/// Discriminator on <see cref="SlackViewsOpenResult"/>.
/// </summary>
internal enum SlackViewsOpenResultKind
{
    Ok = 0,

    /// <summary>Slack accepted the HTTP request but returned <c>{"ok":false}</c>.</summary>
    SlackError = 1,

    /// <summary>Transport error (timeout, DNS, TLS, 5xx).</summary>
    NetworkFailure = 2,

    /// <summary>The workspace lacks a bot-token secret reference or the secret resolves to empty.</summary>
    MissingConfiguration = 3,
}
