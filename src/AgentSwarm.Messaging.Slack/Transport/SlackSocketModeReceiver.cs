// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeReceiver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.Extensions.Logging;

/// <summary>
/// Per-workspace <see cref="ISlackInboundTransport"/> that maintains a
/// persistent Slack Socket Mode WebSocket connection, ACKs every
/// inbound envelope within Slack's five-second budget, normalises
/// payloads into <see cref="SlackInboundEnvelope"/>, and enqueues
/// them onto <see cref="ISlackInboundQueue"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The receiver runs one background task per workspace:
/// </para>
/// <list type="number">
///   <item><description>Resolve the workspace's app-level token via
///   <see cref="ISecretProvider"/>.</description></item>
///   <item><description>Open a fresh
///   <see cref="ISlackSocketModeConnection"/> via
///   <see cref="ISlackSocketModeConnectionFactory"/>. The factory
///   handles the <c>apps.connections.open</c> + WebSocket upgrade
///   handshake.</description></item>
///   <item><description>Loop receiving frames. For
///   <c>events_api</c>/<c>slash_commands</c>/<c>interactive</c> frames
///   ACK FIRST (so we satisfy the 5-second deadline even if downstream
///   enqueue is slow), then normalise via
///   <see cref="SlackSocketModePayloadNormalizer"/> and enqueue.</description></item>
///   <item><description>On any failure (WebSocket exception, graceful
///   close, Slack <c>disconnect</c> frame, secret resolution failure)
///   wait for the
///   <see cref="SlackSocketModeBackoffPolicy"/>-computed delay and
///   reconnect. The attempt counter resets to zero on a successful
///   <c>hello</c>.</description></item>
/// </list>
/// <para>
/// <see cref="StopAsync"/> cancels the loop, closes the current
/// connection, and waits for any in-flight enqueue to drain before
/// returning -- satisfying the brief's "drain pending envelopes"
/// requirement.
/// </para>
/// </remarks>
internal sealed class SlackSocketModeReceiver : ISlackInboundTransport
{
    private readonly SlackWorkspaceConfig workspace;
    private readonly ISecretProvider secretProvider;
    private readonly ISlackSocketModeConnectionFactory connectionFactory;
    private readonly ISlackInboundQueue inboundQueue;
    private readonly ISlackInboundEnqueueDeadLetterSink deadLetterSink;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackSocketModeReceiver> logger;
    private readonly SlackSocketModeOptions options;
    private readonly SlackSocketModeBackoffPolicy backoffPolicy;
    private readonly object syncRoot = new();

    private CancellationTokenSource? cts;
    private Task? loopTask;
    private ISlackSocketModeConnection? currentConnection;

    public SlackSocketModeReceiver(
        SlackWorkspaceConfig workspace,
        ISecretProvider secretProvider,
        ISlackSocketModeConnectionFactory connectionFactory,
        ISlackInboundQueue inboundQueue,
        ISlackInboundEnqueueDeadLetterSink deadLetterSink,
        TimeProvider timeProvider,
        ILogger<SlackSocketModeReceiver> logger,
        SlackSocketModeOptions? options = null)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.TeamId))
        {
            throw new ArgumentException(
                $"{nameof(SlackWorkspaceConfig.TeamId)} must be non-empty.",
                nameof(workspace));
        }

        if (string.IsNullOrWhiteSpace(workspace.AppLevelTokenRef))
        {
            throw new ArgumentException(
                $"{nameof(SlackSocketModeReceiver)} requires {nameof(SlackWorkspaceConfig.AppLevelTokenRef)} to be set. Use SlackEventsApiReceiver for Events-API-only workspaces.",
                nameof(workspace));
        }

        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.inboundQueue = inboundQueue ?? throw new ArgumentNullException(nameof(inboundQueue));
        this.deadLetterSink = deadLetterSink ?? throw new ArgumentNullException(nameof(deadLetterSink));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.options = options ?? new SlackSocketModeOptions();
        this.backoffPolicy = new SlackSocketModeBackoffPolicy(this.options);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct)
    {
        CancellationTokenSource localCts;
        lock (this.syncRoot)
        {
            if (this.loopTask is not null)
            {
                throw new InvalidOperationException(
                    $"{nameof(SlackSocketModeReceiver)} is already started for workspace {this.workspace.TeamId}.");
            }

            // The receiver owns its own cancellation token so the
            // caller's start-up cancellation cannot accidentally stop
            // the loop -- the contract on ISlackInboundTransport is
            // that StopAsync is the lifecycle exit.
            localCts = new CancellationTokenSource();
            this.cts = localCts;
        }

        // Stage 4.2 evaluator iter-1 item 4: surface initial setup
        // failures synchronously. Resolving the app-level token AND
        // opening the first connection here (rather than fire-and-
        // forget inside the background loop) means the host sees a
        // failed StartAsync if secret resolution fails, the
        // apps.connections.open call returns ok=false, or the WebSocket
        // upgrade is rejected. The background reconnect loop only
        // covers post-handshake faults.
        ISlackSocketModeConnection initialConnection;
        try
        {
            string initialToken = await this.secretProvider
                .GetSecretAsync(this.workspace.AppLevelTokenRef!, ct)
                .ConfigureAwait(false);

            initialConnection = await this.connectionFactory
                .ConnectAsync(initialToken, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Roll back the CTS so a retried StartAsync starts clean
            // and StopAsync remains a no-op.
            lock (this.syncRoot)
            {
                if (ReferenceEquals(this.cts, localCts))
                {
                    this.cts = null;
                }
            }

            try
            {
                localCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            this.logger.LogError(
                ex,
                "Slack Socket Mode receiver: initial connection failed for workspace {TeamId}; StartAsync rejected so the host can surface the fault.",
                this.workspace.TeamId);
            throw;
        }

        CancellationToken loopToken;
        lock (this.syncRoot)
        {
            this.currentConnection = initialConnection;
            loopToken = localCts.Token;
            this.loopTask = Task.Run(
                () => this.RunLoopAsync(initialConnection, loopToken),
                CancellationToken.None);
        }

        this.logger.LogInformation(
            "Slack Socket Mode receiver started for workspace {TeamId} (initial connection open; reconnect bounds {InitialMs}ms..{MaxMs}ms).",
            this.workspace.TeamId,
            this.options.InitialReconnectDelay.TotalMilliseconds,
            this.options.MaxReconnectDelay.TotalMilliseconds);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        CancellationTokenSource? localCts;
        Task? localLoop;
        ISlackSocketModeConnection? localConn;
        lock (this.syncRoot)
        {
            localCts = this.cts;
            localLoop = this.loopTask;
            localConn = this.currentConnection;
            this.cts = null;
            this.loopTask = null;
            this.currentConnection = null;
        }

        if (localCts is null)
        {
            return;
        }

        try
        {
            localCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (localConn is not null)
        {
            try
            {
                await localConn.CloseAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Slack Socket Mode receiver: error closing current connection for workspace {TeamId}; ignoring.",
                    this.workspace.TeamId);
            }
        }

        if (localLoop is not null)
        {
            try
            {
                await localLoop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller asked us to give up waiting; the loop will
                // observe the cancellation on its own and exit.
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Slack Socket Mode receiver: receive loop terminated with error during shutdown for workspace {TeamId}.",
                    this.workspace.TeamId);
            }
        }

        try
        {
            localCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        this.logger.LogInformation(
            "Slack Socket Mode receiver stopped for workspace {TeamId}.",
            this.workspace.TeamId);
    }

    private async Task RunLoopAsync(ISlackSocketModeConnection? initialConnection, CancellationToken ct)
    {
        ISlackSocketModeConnection? carryConnection = initialConnection;
        int reconnectAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ISlackSocketModeConnection conn;
                if (carryConnection is not null)
                {
                    // First iteration: re-use the connection that
                    // StartAsync already opened so the synchronous
                    // start-up handshake is not repeated.
                    conn = carryConnection;
                    carryConnection = null;
                }
                else
                {
                    string token = await this.secretProvider
                        .GetSecretAsync(this.workspace.AppLevelTokenRef!, ct)
                        .ConfigureAwait(false);

                    conn = await this.connectionFactory
                        .ConnectAsync(token, ct)
                        .ConfigureAwait(false);

                    lock (this.syncRoot)
                    {
                        this.currentConnection = conn;
                    }
                }

                try
                {
                    await this.PumpFramesAsync(conn, () => reconnectAttempt = 0, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (this.syncRoot)
                    {
                        if (ReferenceEquals(this.currentConnection, conn))
                        {
                            this.currentConnection = null;
                        }
                    }

                    try
                    {
                        await conn.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        this.logger.LogDebug(
                            disposeEx,
                            "Slack Socket Mode receiver: error disposing connection for workspace {TeamId}; ignoring.",
                            this.workspace.TeamId);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SlackSocketModeReconnectRequestedException)
            {
                // Slack-issued disconnect frame: reconnect immediately
                // without bumping the attempt counter (this is a
                // benign rollover, not a fault).
                this.logger.LogInformation(
                    "Slack Socket Mode receiver: Slack requested reconnect for workspace {TeamId}; reopening connection.",
                    this.workspace.TeamId);
                reconnectAttempt = 0;
                continue;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    ex,
                    "Slack Socket Mode receiver: connection or receive loop error for workspace {TeamId}; will reconnect with backoff.",
                    this.workspace.TeamId);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            reconnectAttempt++;
            TimeSpan delay = this.backoffPolicy.ComputeDelay(reconnectAttempt);
            this.logger.LogInformation(
                "Slack Socket Mode receiver: workspace {TeamId} reconnect attempt {Attempt} in {DelayMs}ms (ceiling {CeilingMs}ms).",
                this.workspace.TeamId,
                reconnectAttempt,
                delay.TotalMilliseconds,
                this.backoffPolicy.ComputeCeiling(reconnectAttempt).TotalMilliseconds);

            try
            {
                await Task.Delay(delay, this.timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PumpFramesAsync(
        ISlackSocketModeConnection conn,
        Action onHello,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SlackSocketModeFrame? frame = await conn.ReceiveFrameAsync(ct).ConfigureAwait(false);
            if (frame is null)
            {
                this.logger.LogInformation(
                    "Slack Socket Mode receiver: WebSocket closed by peer for workspace {TeamId}; will reconnect.",
                    this.workspace.TeamId);
                return;
            }

            switch (frame.Type)
            {
                case SlackSocketModeFrame.HelloType:
                    onHello();
                    this.logger.LogInformation(
                        "Slack Socket Mode receiver: hello received for workspace {TeamId}; ready to process envelopes.",
                        this.workspace.TeamId);
                    break;

                case SlackSocketModeFrame.DisconnectType:
                    throw new SlackSocketModeReconnectRequestedException();

                case SlackSocketModeFrame.EventsApiType:
                case SlackSocketModeFrame.SlashCommandsType:
                case SlackSocketModeFrame.InteractiveType:
                    await this.HandleEnvelopeFrameAsync(conn, frame, ct).ConfigureAwait(false);
                    break;

                default:
                    this.logger.LogDebug(
                        "Slack Socket Mode receiver: ignoring frame of type {FrameType} for workspace {TeamId}.",
                        frame.Type,
                        this.workspace.TeamId);
                    if (!string.IsNullOrEmpty(frame.EnvelopeId))
                    {
                        await this.SendAckSafeAsync(conn, frame.EnvelopeId, ct).ConfigureAwait(false);
                    }

                    break;
            }
        }
    }

    private async Task HandleEnvelopeFrameAsync(
        ISlackSocketModeConnection conn,
        SlackSocketModeFrame frame,
        CancellationToken ct)
    {
        // ACK FIRST so Slack's 5-second budget is not blocked on a
        // slow enqueue or downstream parser (tech-spec.md §5.2).
        if (!string.IsNullOrEmpty(frame.EnvelopeId))
        {
            await this.SendAckSafeAsync(conn, frame.EnvelopeId, ct).ConfigureAwait(false);
        }
        else
        {
            this.logger.LogWarning(
                "Slack Socket Mode receiver: frame of type {FrameType} arrived without envelope_id; cannot ACK.",
                frame.Type);
        }

        SlackInboundEnvelope? envelope = SlackSocketModePayloadNormalizer.Normalize(
            frame,
            this.timeProvider.GetUtcNow());
        if (envelope is null)
        {
            return;
        }

        // The receive loop is itself a background task -- it is safe
        // to AWAIT the enqueue retry here so StopAsync's loop-wait
        // naturally drains pending envelopes (brief: "drain pending
        // envelopes" on graceful shutdown).
        await SlackInboundEnqueueScheduler.EnqueueWithRetryAsync(
            this.inboundQueue,
            envelope,
            this.logger,
            this.deadLetterSink,
            auditContext: $"socket_mode_frame={frame.Type} team_id={this.workspace.TeamId}")
            .ConfigureAwait(false);
    }

    private async Task SendAckSafeAsync(
        ISlackSocketModeConnection conn,
        string envelopeId,
        CancellationToken ct)
    {
        using CancellationTokenSource ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackCts.CancelAfter(this.options.AckTimeout);

        try
        {
            await conn.SendAckAsync(envelopeId, ackCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            this.logger.LogWarning(
                "Slack Socket Mode receiver: ACK send timed out after {TimeoutMs}ms for envelope_id={EnvelopeId} workspace={TeamId}; treating as disconnection.",
                this.options.AckTimeout.TotalMilliseconds,
                envelopeId,
                this.workspace.TeamId);
            throw;
        }
    }
}

/// <summary>
/// Internal marker exception thrown when the receive loop wants to
/// trigger an immediate reconnection without bumping the reconnect
/// attempt counter -- specifically when Slack sends a <c>disconnect</c>
/// Socket Mode frame.
/// </summary>
internal sealed class SlackSocketModeReconnectRequestedException : Exception
{
    public SlackSocketModeReconnectRequestedException()
        : base("Slack issued a disconnect frame requesting client reconnect.")
    {
    }
}
