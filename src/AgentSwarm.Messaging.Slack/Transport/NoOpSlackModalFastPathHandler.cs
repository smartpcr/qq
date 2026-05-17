// -----------------------------------------------------------------------
// <copyright file="NoOpSlackModalFastPathHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Test / opt-in <see cref="ISlackModalFastPathHandler"/> that does
/// nothing and returns
/// <see cref="SlackModalFastPathResult.AsyncFallback"/>. Used by tests
/// that want to drive the controller's fallback branch without
/// constructing the full real fast-path collaborator graph.
/// </summary>
/// <remarks>
/// As of Stage 4.1 iter-2, the default registration is
/// <see cref="DefaultSlackModalFastPathHandler"/> (which runs the
/// real <c>auth + idempotency + views.open</c> pipeline against a
/// stub / production Slack endpoint). This no-op variant is kept so
/// the previously-shipped failure mode remains observable to tests
/// and so a host can explicitly opt out of the real fast-path by
/// registering <c>ISlackModalFastPathHandler</c> against this type
/// BEFORE calling
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>.
/// </remarks>
internal sealed class NoOpSlackModalFastPathHandler : ISlackModalFastPathHandler
{
    private readonly ILogger<NoOpSlackModalFastPathHandler> logger;

    public NoOpSlackModalFastPathHandler(ILogger<NoOpSlackModalFastPathHandler> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<SlackModalFastPathResult> HandleAsync(
        SlackInboundEnvelope envelope,
        HttpContext httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(httpContext);

        this.logger.LogWarning(
            "NoOpSlackModalFastPathHandler invoked for team={TeamId} user={UserId} trigger_id={TriggerId}. This handler always returns AsyncFallback; the controller will surface an ephemeral error to the invoking user because modal commands cannot be processed async.",
            envelope.TeamId,
            envelope.UserId,
            envelope.TriggerId);

        return Task.FromResult(SlackModalFastPathResult.AsyncFallback);
    }
}
