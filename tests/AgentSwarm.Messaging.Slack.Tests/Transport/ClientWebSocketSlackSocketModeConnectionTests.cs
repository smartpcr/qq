// -----------------------------------------------------------------------
// <copyright file="ClientWebSocketSlackSocketModeConnectionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the JSON decoder used by
/// <see cref="ClientWebSocketSlackSocketModeConnection"/> against
/// representative Slack Socket Mode frames. We drive the production
/// type through a fake <see cref="WebSocket"/> so the test does not
/// depend on a network or loopback HTTP listener.
/// </summary>
public sealed class ClientWebSocketSlackSocketModeConnectionTests
{
    [Fact]
    public async Task ReceiveFrameAsync_decodes_events_api_frame()
    {
        const string raw = """
            {"envelope_id":"env-1","type":"events_api","accepts_response_payload":false,
             "payload":{"type":"event_callback","event_id":"Ev1","team_id":"T01",
                        "event":{"type":"app_mention","user":"U1","channel":"C1"}}}
            """;
        using FakeWebSocket ws = new();
        ws.EnqueueText(raw);
        await using ClientWebSocketSlackSocketModeConnection conn =
            new(ws, receiveBufferSize: 1024);

        SlackSocketModeFrame? frame = await conn.ReceiveFrameAsync(CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Type.Should().Be(SlackSocketModeFrame.EventsApiType);
        frame.EnvelopeId.Should().Be("env-1");
        frame.Payload.Should().Contain("\"event_id\":\"Ev1\"");
    }

    [Fact]
    public async Task ReceiveFrameAsync_returns_null_on_close()
    {
        using FakeWebSocket ws = new();
        ws.EnqueueClose();
        await using ClientWebSocketSlackSocketModeConnection conn =
            new(ws, receiveBufferSize: 1024);

        SlackSocketModeFrame? frame = await conn.ReceiveFrameAsync(CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public async Task SendAckAsync_writes_envelope_id_payload()
    {
        using FakeWebSocket ws = new();
        await using ClientWebSocketSlackSocketModeConnection conn =
            new(ws, receiveBufferSize: 1024);

        await conn.SendAckAsync("env-42", CancellationToken.None);

        ws.LastSent.Should().Be("{\"envelope_id\":\"env-42\"}");
    }

    /// <summary>
    /// Minimal in-process <see cref="WebSocket"/> for unit tests. The
    /// real ClientWebSocket is sealed, so this fake plugs into the
    /// receive / send abstraction exposed by the abstract base.
    /// </summary>
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly System.Collections.Generic.Queue<Action<MemoryStream>> scripted = new();
        private WebSocketState state = WebSocketState.Open;
        private bool closeQueued;
        private string? lastSent;

        public string? LastSent => this.lastSent;

        public void EnqueueText(string body)
        {
            this.scripted.Enqueue(buffer => buffer.Write(Encoding.UTF8.GetBytes(body)));
        }

        public void EnqueueClose()
        {
            this.closeQueued = true;
        }

        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;

        public override string? CloseStatusDescription => "ok";

        public override WebSocketState State => this.state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            this.state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            this.state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (this.closeQueued)
            {
                this.closeQueued = false;
                this.state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            if (this.scripted.Count == 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            }

            using MemoryStream payload = new();
            this.scripted.Dequeue()(payload);
            byte[] bytes = payload.ToArray();
            int count = Math.Min(bytes.Length, buffer.Count);
            Buffer.BlockCopy(bytes, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage: true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            this.lastSent = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            return Task.CompletedTask;
        }
    }
}
