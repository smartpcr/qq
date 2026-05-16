// -----------------------------------------------------------------------
// <copyright file="SlackAppMentionHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="ISlackAppMentionHandler"/>: processes
/// <c>app_mention</c> events from the Slack Events API and dispatches
/// them through the same sub-command switch used by slash commands.
/// Replaces the Stage 4.3 <see cref="NoOpSlackAppMentionHandler"/>
/// silent-completion default.
/// </summary>
/// <remarks>
/// <para>
/// Stage 5.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Implements every brief step:
/// </para>
/// <list type="number">
///   <item>
///     <description>Step 1 -- Processes <c>app_mention</c> events
///     forwarded by the Stage 4.3
///     <see cref="SlackInboundProcessingPipeline"/>.</description>
///   </item>
///   <item>
///     <description>Step 2 -- Strips the leading
///     <c>&lt;@BOT_USER_ID&gt;</c> token via
///     <see cref="SlackInboundPayloadParser.StripBotMentionPrefix"/>
///     before parsing.</description>
///   </item>
///   <item>
///     <description>Step 3 -- Parses the stripped text with the same
///     <see cref="SlackInboundPayloadParser.ParseSubCommand"/> helper
///     the slash-command path uses, so the supported sub-commands are
///     literally identical (<c>ask</c>, <c>status</c>, <c>approve</c>,
///     <c>reject</c>, <c>review</c>, <c>escalate</c>).</description>
///   </item>
///   <item>
///     <description>Step 4 -- Delegates to
///     <see cref="SlackCommandHandler.DispatchAsync"/> so the dispatch
///     logic (orchestrator calls, error rendering, modal fall-back) is
///     unified between the two entry points.</description>
///   </item>
///   <item>
///     <description>Step 5 -- Substitutes a
///     <see cref="ThreadedReplyResponder"/> for the default
///     <see cref="ISlackEphemeralResponder"/> so every reply lands as a
///     threaded <c>chat.postMessage</c> in the channel where the
///     mention occurred (using <c>event.thread_ts</c> when the mention
///     was already inside a thread, or the mention's own <c>event.ts</c>
///     so Slack promotes a top-level mention into a new thread).</description>
///   </item>
/// </list>
/// <para>
/// Failure semantics. Any exception thrown by the underlying
/// <see cref="SlackCommandHandler.DispatchAsync"/> propagates out so the
/// <see cref="SlackInboundProcessingPipeline"/> can retry the envelope
/// (and dead-letter it after the retry budget). Failures inside the
/// threaded reply poster are swallowed by the poster itself --
/// a missed reply is recoverable from logs, but turning it into a retry
/// would replay the orchestrator side-effect.
/// </para>
/// </remarks>
internal sealed class SlackAppMentionHandler : ISlackAppMentionHandler
{
    private readonly SlackCommandHandler commandHandler;
    private readonly ISlackThreadedReplyPoster threadedReplyPoster;
    private readonly ILogger<SlackAppMentionHandler> logger;

    public SlackAppMentionHandler(
        SlackCommandHandler commandHandler,
        ISlackThreadedReplyPoster threadedReplyPoster,
        ILogger<SlackAppMentionHandler> logger)
    {
        this.commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        this.threadedReplyPoster = threadedReplyPoster ?? throw new ArgumentNullException(nameof(threadedReplyPoster));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task HandleAsync(SlackInboundEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        SlackAppMentionPayload mention = SlackInboundPayloadParser.ParseEventAppMention(envelope.RawPayload);
        string strippedText = SlackInboundPayloadParser.StripBotMentionPrefix(mention.Text);

        // The channel id is required to anchor the reply; if it is
        // missing (malformed payload or non-channel mention) we have
        // no way to post a threaded response. Surface a warning and
        // ack so the dedup row still claims the envelope -- a Slack
        // retry of the same event_id MUST NOT re-dispatch.
        string? channelId = envelope.ChannelId ?? mention.ChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            this.logger.LogWarning(
                "SlackAppMentionHandler dropped app_mention envelope idempotency_key={IdempotencyKey} team={TeamId} user={UserId} because no channel id was supplied (envelope.ChannelId AND event.channel are both empty).",
                envelope.IdempotencyKey,
                envelope.TeamId,
                envelope.UserId);
            return;
        }

        // Iter-2 evaluator item 3 fix: when envelope.ChannelId is null
        // but the inner event.channel carried one (a malformed envelope
        // factory path, or a synthetic test envelope), the resolved
        // channelId MUST be propagated into the envelope handed to
        // SlackCommandHandler.DispatchAsync so downstream consumers
        // (notably AgentTaskCreationRequest.ChannelId for the ask path
        // -- SlackCommandHandler.HandleAskAsync reads envelope.ChannelId
        // directly) see the channel needed to anchor the Slack thread.
        // Using `with` keeps the rest of the envelope identical so the
        // idempotency key, source type, team/user/raw payload, and
        // trigger id continue to match the originally-buffered envelope.
        SlackInboundEnvelope dispatchEnvelope = string.Equals(envelope.ChannelId, channelId, StringComparison.Ordinal)
            ? envelope
            : envelope with { ChannelId = channelId };

        // Slack documents app_mention payloads as always carrying an
        // event.ts; thread_ts is optional and only present when the
        // mention was posted inside an existing thread. The handler
        // routes its reply into the existing thread when present, and
        // otherwise anchors a new thread on the mention's own ts --
        // both produce a threaded chat.postMessage (per the brief's
        // step 5: "using the message's thread_ts if already in a
        // thread, or creating a new thread if in the main channel").
        string? replyThreadTs = string.IsNullOrEmpty(mention.ThreadTs)
            ? mention.Ts
            : mention.ThreadTs;

        string correlationId = string.IsNullOrEmpty(envelope.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : envelope.IdempotencyKey;

        ThreadedReplyResponder responder = new(
            poster: this.threadedReplyPoster,
            teamId: dispatchEnvelope.TeamId,
            channelId: channelId!,
            threadTs: replyThreadTs,
            correlationId: correlationId,
            logger: this.logger);

        // Build a synthetic SlackCommandPayload from the stripped
        // mention text so the SlackCommandHandler.DispatchAsync switch
        // sees the same shape it would see from a real /agent slash
        // command. Notably, response_url is intentionally null (Events
        // API does not issue one) and trigger_id is null (so review /
        // escalate routes through the trigger-id-missing fallback,
        // matching e2e-scenarios.md Feature 7 sub-scenarios).
        SlackCommandPayload syntheticPayload = new(
            TeamId: dispatchEnvelope.TeamId,
            ChannelId: channelId,
            UserId: dispatchEnvelope.UserId,
            Command: null,
            Text: strippedText,
            TriggerId: null,
            ResponseUrl: null,
            SubCommand: SlackInboundPayloadParser.ParseSubCommand(strippedText));

        this.logger.LogInformation(
            "SlackAppMentionHandler dispatching app_mention idempotency_key={IdempotencyKey} team={TeamId} channel={ChannelId} user={UserId} thread_ts={ThreadTs} sub_command={SubCommand}.",
            dispatchEnvelope.IdempotencyKey,
            dispatchEnvelope.TeamId,
            channelId,
            dispatchEnvelope.UserId,
            replyThreadTs,
            syntheticPayload.SubCommand ?? "(none)");

        await this.commandHandler
            .DispatchAsync(dispatchEnvelope, syntheticPayload, responder, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Per-envelope <see cref="ISlackEphemeralResponder"/> shim that
    /// converts the slash-command-shaped <c>SendEphemeralAsync</c>
    /// call (ignoring the meaningless <c>response_url</c> argument)
    /// into a <see cref="ISlackThreadedReplyPoster"/> threaded
    /// <c>chat.postMessage</c>. Captured at <see cref="HandleAsync"/>
    /// scope so every reply this envelope produces routes to the same
    /// destination thread.
    /// </summary>
    private sealed class ThreadedReplyResponder : ISlackEphemeralResponder
    {
        private readonly ISlackThreadedReplyPoster poster;
        private readonly string teamId;
        private readonly string channelId;
        private readonly string? threadTs;
        private readonly string correlationId;
        private readonly ILogger logger;

        public ThreadedReplyResponder(
            ISlackThreadedReplyPoster poster,
            string teamId,
            string channelId,
            string? threadTs,
            string correlationId,
            ILogger logger)
        {
            this.poster = poster;
            this.teamId = teamId;
            this.channelId = channelId;
            this.threadTs = threadTs;
            this.correlationId = correlationId;
            this.logger = logger;
        }

        /// <inheritdoc />
        public async Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            // The responseUrl argument is intentionally ignored: Events
            // API app_mention payloads do not issue a per-invocation
            // response_url, so the dispatcher path always passes null
            // here. Logging the argument at debug level keeps the
            // suppression observable in test traces.
            if (!string.IsNullOrEmpty(responseUrl))
            {
                this.logger.LogDebug(
                    "SlackAppMentionHandler.ThreadedReplyResponder ignoring response_url='{ResponseUrl}' supplied by synthetic command payload; app_mention replies always route via chat.postMessage.",
                    responseUrl);
            }

            SlackThreadedReplyRequest request = new(
                TeamId: this.teamId,
                ChannelId: this.channelId,
                ThreadTs: this.threadTs,
                Text: message,
                CorrelationId: this.correlationId);

            // Iter-2 evaluator item 2 fix: failures inside the threaded
            // reply poster MUST NOT propagate out of the responder.
            // SlackCommandHandler.DispatchAsync calls the responder
            // AFTER orchestrator side-effects (CreateTaskAsync /
            // PublishDecisionAsync) have already completed. Letting a
            // poster exception escape would tear down the dispatch
            // method and the inbound pipeline would retry the whole
            // envelope, duplicating the orchestrator call once the
            // retry succeeds. The ISlackThreadedReplyPoster contract
            // documents that implementations should already swallow
            // non-fatal HTTP errors; this catch is defence-in-depth
            // against an implementation that does not honour that
            // contract. Caller cancellation is still propagated so the
            // ingestor's shutdown loop honours cancellation.
            try
            {
                await this.poster.PostAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "SlackAppMentionHandler.ThreadedReplyResponder swallowed an exception from the threaded reply poster (correlation_id={CorrelationId} team={TeamId} channel={ChannelId} thread_ts={ThreadTs}) so the inbound pipeline does NOT retry and duplicate orchestrator side-effects.",
                    this.correlationId,
                    this.teamId,
                    this.channelId,
                    this.threadTs);
            }
        }
    }
}
