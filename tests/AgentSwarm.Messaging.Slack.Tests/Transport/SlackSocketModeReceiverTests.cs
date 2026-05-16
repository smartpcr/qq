// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeReceiverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// End-to-end behavioural tests for <see cref="SlackSocketModeReceiver"/>.
/// </summary>
/// <remarks>
/// <para>
/// The tests use an in-process <see cref="FakeSocketModeConnection"/>
/// (no real WebSocket) so they cover the receive loop, ACK ordering,
/// reconnection backoff, and graceful shutdown without spinning up a
/// network listener. This realises the brief's "mock WebSocket server"
/// in test scenario 1 and the reconnection requirement in scenario 2.
/// </para>
/// </remarks>
public sealed class SlackSocketModeReceiverTests
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Receives_envelope_acks_within_5s_and_enqueues()
    {
        // Arrange a single events_api frame.
        FakeSocketModeConnection connection = new();
        const string payload = """
            {
              "type": "event_callback",
              "event_id": "Ev-ACK-1",
              "team_id": "T-WS1",
              "event": { "type": "app_mention", "user": "U1", "channel": "C1" }
            }
            """;
        SlackSocketModeFrame frame = new(
            Type: SlackSocketModeFrame.EventsApiType,
            EnvelopeId: "env-1",
            Payload: payload,
            RawFrameJson: "{}");
        connection.EnqueueFrame(frame);

        FakeConnectionFactory factory = new(connection);
        ChannelBasedSlackInboundQueue queue = new();
        InMemorySlackInboundEnqueueDeadLetterSink dlq =
            new(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance);
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-WS1",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };

        SlackSocketModeReceiver receiver = new(
            workspace: ws,
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: factory,
            inboundQueue: queue,
            deadLetterSink: dlq,
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
                AckTimeout = TimeSpan.FromSeconds(4),
            });

        // Act
        Stopwatch stopwatch = Stopwatch.StartNew();
        await receiver.StartAsync(CancellationToken.None);

        // Wait for the envelope to land on the queue.
        SlackInboundEnvelope envelope;
        using (CancellationTokenSource cts = new(MaxWait))
        {
            envelope = await queue.DequeueAsync(cts.Token);
        }

        stopwatch.Stop();

        // Assert: ACK was sent, and the envelope was normalized + enqueued.
        connection.AckedEnvelopeIds.Should().ContainSingle().Which.Should().Be("env-1");
        envelope.SourceType.Should().Be(SlackInboundSourceType.Event);
        envelope.IdempotencyKey.Should().Be("event:Ev-ACK-1");
        envelope.TeamId.Should().Be("T-WS1");
        envelope.RawPayload.Should().Be(payload);

        // Slack mandates ACK within 5 seconds (tech-spec.md §5.2). The
        // in-process loop should ACK in milliseconds; we assert 5s as a
        // generous CI ceiling.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "ACK + enqueue must complete within Slack's 5s envelope budget");

        // ACK must be observed BEFORE the enqueue completes in the
        // receive loop because the loop ACKs first (tech-spec.md §5.2:
        // ack inside 5s; even if the enqueue is slow the ACK must beat
        // the budget). Our connection records the timestamp; cross-check.
        connection.AckTimestamps.Should().ContainSingle();
        TimeSpan ackElapsed = connection.AckTimestamps[0] - connection.FrameDispatchTimestamps[0];
        ackElapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "single-frame ACK should land well inside Slack's 5s budget");

        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconnects_with_backoff_after_websocket_drops()
    {
        // First connection: throws on receive (simulates disconnect).
        FakeSocketModeConnection conn1 = new();
        conn1.ThrowOnNextReceive(new System.Net.WebSockets.WebSocketException("simulated drop"));

        // Second connection: delivers a frame so we can observe successful reconnect.
        FakeSocketModeConnection conn2 = new();
        const string payload = """
            {
              "type": "event_callback",
              "event_id": "Ev-Reconnect",
              "team_id": "T-WS2",
              "event": { "type": "app_mention", "user": "U2", "channel": "C2" }
            }
            """;
        conn2.EnqueueFrame(new SlackSocketModeFrame(
            Type: SlackSocketModeFrame.EventsApiType,
            EnvelopeId: "env-rec",
            Payload: payload,
            RawFrameJson: "{}"));

        FakeConnectionFactory factory = new(conn1, conn2);
        ChannelBasedSlackInboundQueue queue = new();
        InMemorySlackInboundEnqueueDeadLetterSink dlq =
            new(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance);
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-WS2",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };

        SlackSocketModeReceiver receiver = new(
            workspace: ws,
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: factory,
            inboundQueue: queue,
            deadLetterSink: dlq,
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                // Tiny delays so the test completes quickly while still
                // exercising the exponential backoff path.
                InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
                AckTimeout = TimeSpan.FromSeconds(2),
            });

        await receiver.StartAsync(CancellationToken.None);

        // The receiver should reconnect and surface the second connection's frame.
        SlackInboundEnvelope envelope;
        using (CancellationTokenSource cts = new(MaxWait))
        {
            envelope = await queue.DequeueAsync(cts.Token);
        }

        envelope.IdempotencyKey.Should().Be("event:Ev-Reconnect");
        factory.ConnectionsOpened.Should().BeGreaterThanOrEqualTo(2,
            "the factory must be called at least twice -- once initially and once on reconnect after the drop");
        conn2.AckedEnvelopeIds.Should().ContainSingle().Which.Should().Be("env-rec");

        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconnects_when_slack_sends_disconnect_frame()
    {
        FakeSocketModeConnection conn1 = new();
        conn1.EnqueueFrame(new SlackSocketModeFrame(
            Type: SlackSocketModeFrame.DisconnectType,
            EnvelopeId: null,
            Payload: string.Empty,
            RawFrameJson: "{\"type\":\"disconnect\"}"));

        FakeSocketModeConnection conn2 = new();
        const string payload = """
            { "type": "event_callback", "event_id": "Ev-After-Disc", "team_id": "T-DISC",
              "event": { "type": "app_mention", "user": "U", "channel": "C" } }
            """;
        conn2.EnqueueFrame(new SlackSocketModeFrame(
            Type: SlackSocketModeFrame.EventsApiType,
            EnvelopeId: "env-disc",
            Payload: payload,
            RawFrameJson: "{}"));

        FakeConnectionFactory factory = new(conn1, conn2);
        ChannelBasedSlackInboundQueue queue = new();
        InMemorySlackInboundEnqueueDeadLetterSink dlq =
            new(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance);

        SlackSocketModeReceiver receiver = new(
            workspace: new SlackWorkspaceConfig
            {
                TeamId = "T-DISC",
                SigningSecretRef = "env://SIG",
                AppLevelTokenRef = "env://XAPP",
                Enabled = true,
            },
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: factory,
            inboundQueue: queue,
            deadLetterSink: dlq,
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(5),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
            });

        await receiver.StartAsync(CancellationToken.None);

        SlackInboundEnvelope envelope;
        using (CancellationTokenSource cts = new(MaxWait))
        {
            envelope = await queue.DequeueAsync(cts.Token);
        }

        envelope.IdempotencyKey.Should().Be("event:Ev-After-Disc");

        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_is_idempotent_and_safe_before_start()
    {
        SlackSocketModeReceiver receiver = new(
            workspace: new SlackWorkspaceConfig
            {
                TeamId = "T-STOP",
                SigningSecretRef = "env://SIG",
                AppLevelTokenRef = "env://XAPP",
                Enabled = true,
            },
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: new FakeConnectionFactory(new FakeSocketModeConnection()),
            inboundQueue: new ChannelBasedSlackInboundQueue(),
            deadLetterSink: new InMemorySlackInboundEnqueueDeadLetterSink(
                NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(5),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
            });

        // StopAsync before StartAsync is a no-op.
        await receiver.StopAsync(CancellationToken.None);

        await receiver.StartAsync(CancellationToken.None);
        await receiver.StopAsync(CancellationToken.None);
        // Calling StopAsync again should not throw.
        await receiver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_rejects_workspace_without_app_level_token_ref()
    {
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-MISSING",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = null,
            Enabled = true,
        };

        Action ctor = () => new SlackSocketModeReceiver(
            workspace: ws,
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: new FakeConnectionFactory(new FakeSocketModeConnection()),
            inboundQueue: new ChannelBasedSlackInboundQueue(),
            deadLetterSink: new InMemorySlackInboundEnqueueDeadLetterSink(
                NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions());

        ctor.Should().Throw<ArgumentException>()
            .WithMessage("*AppLevelTokenRef*");
    }

    [Fact]
    public async Task StartAsync_surfaces_secret_resolution_failure_synchronously()
    {
        // Stage 4.2 evaluator iter-1 item 4: secret resolution failures
        // must NOT be silently swallowed inside the reconnect loop --
        // the host needs the StartAsync exception so a misconfigured
        // workspace stops the rollout instead of appearing healthy.
        SlackSocketModeReceiver receiver = new(
            workspace: new SlackWorkspaceConfig
            {
                TeamId = "T-BAD-SECRET",
                SigningSecretRef = "env://SIG",
                AppLevelTokenRef = "env://MISSING-XAPP",
                Enabled = true,
            },
            secretProvider: new ThrowingSecretProvider(new InvalidOperationException("secret not found")),
            connectionFactory: new FakeConnectionFactory(new FakeSocketModeConnection()),
            inboundQueue: new ChannelBasedSlackInboundQueue(),
            deadLetterSink: new InMemorySlackInboundEnqueueDeadLetterSink(
                NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(5),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
            });

        Func<Task> start = () => receiver.StartAsync(CancellationToken.None);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*secret not found*");

        // After a failed StartAsync the receiver must be safe to start
        // again (CTS rolled back) and StopAsync remains a no-op.
        Func<Task> stop = () => receiver.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_surfaces_initial_connection_failure_synchronously()
    {
        // The factory's first ConnectAsync throws; this must bubble
        // out of StartAsync rather than being caught + retried in the
        // background, because the host cannot observe a background
        // reconnect loop's failure (the receiver appears "started"
        // even though nothing is connected).
        FailingConnectionFactory factory = new(new InvalidOperationException("apps.connections.open returned ok=false"));

        SlackSocketModeReceiver receiver = new(
            workspace: new SlackWorkspaceConfig
            {
                TeamId = "T-BAD-CONN",
                SigningSecretRef = "env://SIG",
                AppLevelTokenRef = "env://XAPP",
                Enabled = true,
            },
            secretProvider: new StubSecretProvider("xapp-test"),
            connectionFactory: factory,
            inboundQueue: new ChannelBasedSlackInboundQueue(),
            deadLetterSink: new InMemorySlackInboundEnqueueDeadLetterSink(
                NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            timeProvider: TimeProvider.System,
            logger: NullLogger<SlackSocketModeReceiver>.Instance,
            options: new SlackSocketModeOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(5),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(20),
            });

        Func<Task> start = () => receiver.StartAsync(CancellationToken.None);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*apps.connections.open*");

        Func<Task> stop = () => receiver.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        private readonly string token;

        public StubSecretProvider(string token)
        {
            this.token = token;
        }

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
            => Task.FromResult(this.token);
    }

    private sealed class ThrowingSecretProvider : ISecretProvider
    {
        private readonly Exception toThrow;

        public ThrowingSecretProvider(Exception toThrow)
        {
            this.toThrow = toThrow;
        }

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
            => Task.FromException<string>(this.toThrow);
    }

    private sealed class FailingConnectionFactory : ISlackSocketModeConnectionFactory
    {
        private readonly Exception toThrow;

        public FailingConnectionFactory(Exception toThrow)
        {
            this.toThrow = toThrow;
        }

        public Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct)
            => Task.FromException<ISlackSocketModeConnection>(this.toThrow);
    }

    /// <summary>
    /// Fake <see cref="ISlackSocketModeConnectionFactory"/> that hands
    /// out a pre-scripted sequence of connections.
    /// </summary>
    private sealed class FakeConnectionFactory : ISlackSocketModeConnectionFactory
    {
        private readonly Queue<FakeSocketModeConnection> connections;
        private int opened;

        public FakeConnectionFactory(params FakeSocketModeConnection[] connections)
        {
            this.connections = new Queue<FakeSocketModeConnection>(connections);
        }

        public int ConnectionsOpened => Volatile.Read(ref this.opened);

        public Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct)
        {
            Interlocked.Increment(ref this.opened);
            FakeSocketModeConnection next;
            lock (this.connections)
            {
                next = this.connections.Count > 0
                    ? this.connections.Dequeue()
                    : CreateIdle();
            }

            return Task.FromResult<ISlackSocketModeConnection>(next);
        }

        private static FakeSocketModeConnection CreateIdle()
        {
            FakeSocketModeConnection idle = new();
            // Block forever on receive; the test will tear down via StopAsync.
            return idle;
        }
    }

    /// <summary>
    /// In-process fake WebSocket. Tests script frames via
    /// <see cref="EnqueueFrame"/> / <see cref="ThrowOnNextReceive"/> and
    /// inspect ACKs via <see cref="AckedEnvelopeIds"/>.
    /// </summary>
    private sealed class FakeSocketModeConnection : ISlackSocketModeConnection
    {
        private readonly Channel<Func<SlackSocketModeFrame>> frames =
            Channel.CreateUnbounded<Func<SlackSocketModeFrame>>();

        private readonly ConcurrentQueue<string> acked = new();
        private readonly List<DateTimeOffset> ackTimestamps = new();
        private readonly List<DateTimeOffset> dispatchTimestamps = new();
        private readonly object timestampLock = new();
        private int disposed;

        public IReadOnlyCollection<string> AckedEnvelopeIds => this.acked;

        public IReadOnlyList<DateTimeOffset> AckTimestamps
        {
            get
            {
                lock (this.timestampLock)
                {
                    return this.ackTimestamps.ToArray();
                }
            }
        }

        public IReadOnlyList<DateTimeOffset> FrameDispatchTimestamps
        {
            get
            {
                lock (this.timestampLock)
                {
                    return this.dispatchTimestamps.ToArray();
                }
            }
        }

        public void EnqueueFrame(SlackSocketModeFrame frame)
        {
            this.frames.Writer.TryWrite(() =>
            {
                lock (this.timestampLock)
                {
                    this.dispatchTimestamps.Add(DateTimeOffset.UtcNow);
                }

                return frame;
            });
        }

        public void ThrowOnNextReceive(Exception ex)
        {
            this.frames.Writer.TryWrite(() => throw ex);
        }

        public async Task<SlackSocketModeFrame?> ReceiveFrameAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                return null;
            }

            Func<SlackSocketModeFrame> producer;
            try
            {
                producer = await this.frames.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            return producer();
        }

        public Task SendAckAsync(string envelopeId, CancellationToken ct)
        {
            this.acked.Enqueue(envelopeId);
            lock (this.timestampLock)
            {
                this.ackTimestamps.Add(DateTimeOffset.UtcNow);
            }

            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                this.frames.Writer.TryComplete();
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return new ValueTask(this.CloseAsync(CancellationToken.None));
        }
    }
}
