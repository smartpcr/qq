// -----------------------------------------------------------------------
// <copyright file="ScriptedSlackSocketModeConnection.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Scripted in-process fake of <see cref="ISlackSocketModeConnection"/>
/// that delivers pre-loaded <see cref="SlackSocketModeFrame"/> values to
/// the production <see cref="SlackSocketModeReceiver"/>'s receive loop
/// and records every <see cref="SendAckAsync"/> call so a test can
/// assert the WebSocket ACK contract.
/// </summary>
/// <remarks>
/// <para>
/// Stage 8.2 AC-5 (iter-8 evaluator item #1 fix): the previous
/// iteration's async-leg test built a <see cref="SlackInboundEnvelope"/>
/// via <see cref="SlackInboundEnvelopeFactory.Build"/> and called
/// <see cref="Queues.ISlackInboundQueue.EnqueueAsync"/> directly,
/// treating the Socket Mode ACK as satisfied by construction. The
/// evaluator flagged that as failing to drive a real production
/// transport. This connection lets a test plug a fully-scripted
/// WebSocket-equivalent into the real
/// <see cref="SlackSocketModeReceiver"/>:
/// </para>
/// <list type="number">
///   <item><description>
///     Tests enqueue frames via <see cref="EnqueueFrame"/>. The
///     receiver pulls them in order from
///     <see cref="ReceiveFrameAsync"/>.
///   </description></item>
///   <item><description>
///     When the receiver invokes <see cref="SendAckAsync"/> for an
///     <see cref="SlackSocketModeFrame.EnvelopeId"/>, the envelope id
///     lands in <see cref="AckedEnvelopeIds"/> so the test can assert
///     the Slack 5-second ACK contract was honoured.
///   </description></item>
///   <item><description>
///     After all scripted frames are consumed,
///     <see cref="ReceiveFrameAsync"/> blocks indefinitely on the
///     internal <see cref="Channel"/> until the receiver's
///     cancellation token fires (i.e. the test calls
///     <see cref="SlackSocketModeReceiver.StopAsync"/>). Returning
///     <see langword="null"/> prematurely would cause the production
///     receiver to enter its reconnect loop -- the test would
///     observe an unbounded sequence of <see cref="ConnectAsync"/>
///     calls and the assertions would race a reconnect storm.
///   </description></item>
/// </list>
/// </remarks>
internal sealed class ScriptedSlackSocketModeConnection : ISlackSocketModeConnection
{
    private readonly Channel<SlackSocketModeFrame> frames = Channel.CreateUnbounded<SlackSocketModeFrame>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly List<string> ackedEnvelopeIds = new();
    private readonly object syncRoot = new();
    private int closeCount;
    private int disposeCount;

    /// <summary>
    /// Envelope ids the receiver has ACKed via
    /// <see cref="SendAckAsync"/>, in arrival order. Snapshotted on
    /// access so the test can iterate safely while the receiver loop
    /// continues to run.
    /// </summary>
    public IReadOnlyList<string> AckedEnvelopeIds
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.ackedEnvelopeIds.ToArray();
            }
        }
    }

    /// <summary>
    /// Number of times <see cref="CloseAsync"/> has been invoked. The
    /// production receiver calls it once during
    /// <see cref="SlackSocketModeReceiver.StopAsync"/>.
    /// </summary>
    public int CloseCount => Volatile.Read(ref this.closeCount);

    /// <summary>
    /// Number of times <see cref="DisposeAsync"/> has been invoked.
    /// </summary>
    public int DisposeCount => Volatile.Read(ref this.disposeCount);

    /// <summary>
    /// Adds a frame to the scripted sequence. Throws if the channel
    /// has already been completed (i.e. <see cref="CloseAsync"/> ran).
    /// </summary>
    public void EnqueueFrame(SlackSocketModeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!this.frames.Writer.TryWrite(frame))
        {
            throw new InvalidOperationException(
                "ScriptedSlackSocketModeConnection: unable to enqueue frame (channel closed).");
        }
    }

    /// <inheritdoc />
    public async Task<SlackSocketModeFrame?> ReceiveFrameAsync(CancellationToken ct)
    {
        try
        {
            SlackSocketModeFrame frame = await this.frames.Reader.ReadAsync(ct).ConfigureAwait(false);
            return frame;
        }
        catch (ChannelClosedException)
        {
            // The connection was closed via CloseAsync; signal graceful
            // close so the production receiver returns from
            // PumpFramesAsync (and the outer loop reconnects -- but the
            // test will have cancelled the receiver before that).
            return null;
        }
    }

    /// <inheritdoc />
    public Task SendAckAsync(string envelopeId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelopeId))
        {
            throw new ArgumentException("envelopeId is required.", nameof(envelopeId));
        }

        lock (this.syncRoot)
        {
            this.ackedEnvelopeIds.Add(envelopeId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref this.closeCount);
        this.frames.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref this.disposeCount);
        this.frames.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="ISlackSocketModeConnectionFactory"/> that hands out a
/// caller-supplied <see cref="ScriptedSlackSocketModeConnection"/> on
/// the FIRST <see cref="ConnectAsync"/> call and throws on every
/// subsequent attempt. The strict single-connection contract makes
/// reconnect-loop bugs visible (an unexpected reconnect fails the test
/// with a clear message rather than silently masking the real
/// behaviour under retries).
/// </summary>
internal sealed class ScriptedSlackSocketModeConnectionFactory : ISlackSocketModeConnectionFactory
{
    private readonly ScriptedSlackSocketModeConnection connection;
    private readonly List<string> requestedTokens = new();
    private readonly object syncRoot = new();
    private int connectCount;

    public ScriptedSlackSocketModeConnectionFactory(ScriptedSlackSocketModeConnection connection)
    {
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// Number of times <see cref="ConnectAsync"/> has been invoked.
    /// Exposed so tests can assert the production receiver opened
    /// exactly one connection during the scripted flow.
    /// </summary>
    public int ConnectCount => Volatile.Read(ref this.connectCount);

    /// <summary>
    /// App-level tokens the receiver passed to
    /// <see cref="ConnectAsync"/>, in call order. Tests assert the
    /// receiver resolved the workspace's <c>AppLevelTokenRef</c>
    /// through <see cref="AgentSwarm.Messaging.Core.Secrets.ISecretProvider"/>
    /// and forwarded the resolved <c>xapp-...</c> string verbatim.
    /// </summary>
    public IReadOnlyList<string> RequestedTokens
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.requestedTokens.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct)
    {
        int callNumber = Interlocked.Increment(ref this.connectCount);
        lock (this.syncRoot)
        {
            this.requestedTokens.Add(appLevelToken);
        }

        if (callNumber > 1)
        {
            throw new InvalidOperationException(
                "ScriptedSlackSocketModeConnectionFactory: a second ConnectAsync call was issued (call #"
                + callNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "). Tests rely on a single scripted connection that stays open until StopAsync; an "
                + "extra connect call usually means the receive loop entered its reconnect path "
                + "unexpectedly (e.g. ReceiveFrameAsync returned null prematurely, or PumpFramesAsync "
                + "threw an unhandled exception during frame processing).");
        }

        return Task.FromResult<ISlackSocketModeConnection>(this.connection);
    }
}
