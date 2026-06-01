// -----------------------------------------------------------------------
// <copyright file="ClientWebSocketSlackSocketModeConnection.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Production <see cref="ISlackSocketModeConnection"/> backed by
/// <see cref="ClientWebSocket"/>. Reads text frames from Slack,
/// reassembling fragmented messages into a single string before
/// decoding the JSON envelope. Serialises ACK writes through a
/// <see cref="SemaphoreSlim"/> because <see cref="WebSocket"/>
/// disallows concurrent send operations.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The class is internal: production callers depend on
/// <see cref="ISlackSocketModeConnection"/> so the wire format never
/// leaks out of the transport layer.
/// </para>
/// </remarks>
internal sealed class ClientWebSocketSlackSocketModeConnection : ISlackSocketModeConnection
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly WebSocket socket;
    private readonly SemaphoreSlim sendLock = new(initialCount: 1, maxCount: 1);
    private readonly int receiveBufferSize;
    private int disposed;

    public ClientWebSocketSlackSocketModeConnection(WebSocket socket, int receiveBufferSize)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        if (receiveBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(receiveBufferSize));
        }

        this.receiveBufferSize = receiveBufferSize;
    }

    /// <inheritdoc />
    public async Task<SlackSocketModeFrame?> ReceiveFrameAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return null;
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(this.receiveBufferSize);
        try
        {
            using MemoryStream assembled = new();
            WebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await this.socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    throw;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                assembled.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string text = Utf8.GetString(assembled.ToArray());
            return DecodeFrame(text);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public async Task SendAckAsync(string envelopeId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelopeId))
        {
            throw new ArgumentException("Envelope id must be non-empty.", nameof(envelopeId));
        }

        string ack = "{\"envelope_id\":\"" + JsonEncodedText.Encode(envelopeId).ToString() + "\"}";

        await this.sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] payload = Utf8.GetBytes(ack);
            await this.socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (this.socket.State == WebSocketState.Open
                || this.socket.State == WebSocketState.CloseReceived)
            {
                await this.socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "shutdown",
                    ct).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Already broken -- nothing to do.
        }
        catch (OperationCanceledException)
        {
            // Caller asked us to stop waiting; the disposed flag is set.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        this.socket.Dispose();
        this.sendLock.Dispose();
    }

    private static SlackSocketModeFrame DecodeFrame(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new SlackSocketModeFrame(
                Type: string.Empty,
                EnvelopeId: null,
                Payload: string.Empty,
                RawFrameJson: json);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SlackSocketModeFrame(string.Empty, null, string.Empty, json);
            }

            string type = root.TryGetProperty("type", out JsonElement typeElt)
                && typeElt.ValueKind == JsonValueKind.String
                ? typeElt.GetString() ?? string.Empty
                : string.Empty;

            string? envelopeId = root.TryGetProperty("envelope_id", out JsonElement envElt)
                && envElt.ValueKind == JsonValueKind.String
                ? envElt.GetString()
                : null;

            string payload = root.TryGetProperty("payload", out JsonElement payloadElt)
                ? payloadElt.GetRawText()
                : string.Empty;

            return new SlackSocketModeFrame(type, envelopeId, payload, json);
        }
        catch (JsonException)
        {
            return new SlackSocketModeFrame(string.Empty, null, string.Empty, json);
        }
    }
}
