// -----------------------------------------------------------------------
// <copyright file="ISlackModalPayloadBuilder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Builds the Slack <c>view</c> JSON payload that the modal fast-path
/// passes to <see cref="ISlackViewsOpenClient.OpenAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 5.2 introduces <c>SlackMessageRenderer</c>, the
/// presentation-layer component responsible for producing every Slack
/// view/blocks payload from typed view-models. Until that lands,
/// Stage 4.1 ships <see cref="DefaultSlackModalPayloadBuilder"/> with
/// minimal-but-real Block Kit payloads so the modal fast-path can
/// satisfy the brief's acceptance criterion 3 ("Human can answer via
/// button or modal").
/// </para>
/// <para>
/// The interface is internal: the brief pins the modal fast-path as a
/// private transport concern.
/// </para>
/// </remarks>
internal interface ISlackModalPayloadBuilder
{
    /// <summary>
    /// Builds the Slack view payload for a given modal-triggering
    /// command (currently <c>/agent review</c> and
    /// <c>/agent escalate</c>).
    /// </summary>
    /// <param name="subCommand">Lower-case sub-command name as
    /// returned by
    /// <see cref="SlackInboundPayloadParser.ParseSubCommand"/>.</param>
    /// <param name="envelope">Normalized inbound envelope. Used to
    /// stamp the workspace/user identifiers on the modal so the
    /// later submission can be correlated.</param>
    /// <returns>
    /// An anonymous-object tree (will be serialized by
    /// <see cref="HttpClientSlackViewsOpenClient"/> as JSON).
    /// </returns>
    object BuildView(string subCommand, SlackInboundEnvelope envelope);
}
