// -----------------------------------------------------------------------
// <copyright file="FakeTelegramApi.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 7.1 step 3 — WireMock.Net-backed fake Telegram Bot API for
/// integration tests. Stubs <c>sendMessage</c>,
/// <c>answerCallbackQuery</c>, <c>editMessageReplyMarkup</c>, and
/// <c>getMe</c> so the Stage 2.3 <c>TelegramMessageSender</c> can be
/// driven end-to-end inside the integration test process without
/// reaching <c>api.telegram.org</c>.
/// </summary>
/// <remarks>
/// <para>
/// Defaults: every <c>sendMessage</c> returns HTTP 200 with a fixed
/// envelope (<see cref="DefaultMessageId"/>) — enough for the
/// <see cref="AgentSwarm.Messaging.Core.SendResult.TelegramMessageId"/>
/// assertion to pass. Tests that need different behaviour call
/// <see cref="StubRateLimitOnce"/>, which installs a higher-priority
/// stub whose <see cref="Response.WithCallback(System.Func{IRequestMessage,WireMock.ResponseMessage})"/>
/// returns HTTP 429 on the first matching <c>sendMessage</c> request
/// and falls through to the default 200 envelope on every subsequent
/// request. The counter-based callback is more robust than WireMock's
/// scenario state machine (whose "first call" semantics depend on the
/// version-specific default initial state and have historically been
/// a source of flaky tests in this fixture).
/// </para>
/// <para>
/// Wildcard paths match the Telegram URL shape
/// <c>https://api.telegram.org/bot{TOKEN}/{METHOD}</c>: the test
/// fixture re-points the bot client at the WireMock base URL, and
/// Telegram.Bot still appends <c>/bot{TOKEN}/sendMessage</c>, so the
/// wildcard <c>/bot*/sendMessage</c> matches without us hard-coding
/// the token.
/// </para>
/// </remarks>
public sealed class FakeTelegramApi : IDisposable
{
    /// <summary>
    /// Stable <c>message_id</c> returned by the default
    /// <c>sendMessage</c> stub. Any positive number suffices for the
    /// Stage 2.3 outbound contract assertions.
    /// </summary>
    public const long DefaultMessageId = 1_000_001L;

    private const string ContentTypeJson = "application/json";

    // Priority bands: lower number = higher priority in WireMock.Net.
    // The default 200 stub registers at priority 1000 (very low) so
    // any test-specific overlays — like the StubRateLimitOnce 429 stub
    // at priority 1 — always win for matching requests.
    private const int RateLimitStubPriority = 1;
    private const int DefaultStubPriority = 1000;

    private readonly WireMockServer _server;

    public FakeTelegramApi()
    {
        _server = WireMockServer.Start();
        RegisterDefaultStubs();
    }

    /// <summary>
    /// HTTP base URL the Telegram.Bot client is pointed at by
    /// <see cref="TelegramTestFixture"/>. Includes scheme and port,
    /// e.g. <c>http://127.0.0.1:54321</c>.
    /// </summary>
    public string BaseUrl => _server.Urls[0];

    /// <summary>
    /// Every request WireMock has observed since startup, in arrival
    /// order. Tests filter this list to assert on <c>sendMessage</c>
    /// calls (chat_id, text body, parse mode).
    /// </summary>
    public IReadOnlyList<RecordedRequest> ReceivedRequests => _server.LogEntries
        .Select(e => new RecordedRequest(
            Path: e.RequestMessage.Path,
            BodyAsString: e.RequestMessage.Body ?? string.Empty,
            BodyAsJson: TryParseJson(e.RequestMessage.Body)))
        .ToList();

    /// <summary>
    /// Convenience filter — returns every <c>sendMessage</c> request
    /// observed since startup, parsed into a typed record so tests
    /// can assert on <see cref="SendMessageRequest.ChatId"/> and
    /// <see cref="SendMessageRequest.Text"/> directly.
    /// </summary>
    public IReadOnlyList<SendMessageRequest> SendMessageRequests => ReceivedRequests
        .Where(r => r.Path.EndsWith("/sendMessage", StringComparison.Ordinal))
        .Select(r => SendMessageRequest.Parse(r.BodyAsJson))
        .ToList();

    /// <summary>
    /// Resets the recorded request log without tearing the server
    /// down. Useful for tests that want to ignore startup probe
    /// traffic (<c>getMe</c>, <c>setWebhook</c>).
    /// </summary>
    public void ResetRequestLog() => _server.ResetLogEntries();

    /// <summary>
    /// Configures the fake so the very next <c>sendMessage</c> request
    /// returns HTTP 429 with <c>retry_after = retryAfterSeconds</c>, and
    /// every subsequent <c>sendMessage</c> falls through to the default
    /// HTTP 200 envelope. Tests use this to assert the Stage 2.3 sender
    /// honours <c>RetryAfter</c> and retries instead of throwing.
    /// </summary>
    /// <remarks>
    /// Implemented with a high-priority overlay stub whose
    /// <c>WithCallback</c> dispatches on a thread-safe call counter
    /// rather than WireMock's scenario state machine. Scenario state
    /// in WireMock.Net 1.5.x has version-specific initial-state quirks
    /// (the "Started" sentinel doesn't always match the freshly-
    /// initialized state, which caused the 429 stub to be silently
    /// bypassed and produced a flaky test that asserted on retry
    /// latency). The callback variant is deterministic.
    /// </remarks>
    public void StubRateLimitOnce(int retryAfterSeconds)
    {
        var rateLimitBody = JsonSerializer.Serialize(new
        {
            ok = false,
            error_code = 429,
            description = "Too Many Requests: retry after " + retryAfterSeconds,
            parameters = new { retry_after = retryAfterSeconds },
        });

        var callCount = 0;

        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/bot*/sendMessage"))
                .UsingPost())
            .AtPriority(RateLimitStubPriority)
            .RespondWith(Response.Create()
                .WithCallback(_ =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    return n == 1
                        ? BuildRawResponse(429, rateLimitBody)
                        : BuildRawResponse(200, DefaultSendMessageBody);
                }));
    }

    public void Dispose() => _server.Stop();

    private void RegisterDefaultStubs()
    {
        var getMeBody = JsonSerializer.Serialize(new
        {
            ok = true,
            result = new
            {
                id = 7777L,
                is_bot = true,
                first_name = "IntegrationTestBot",
                username = "integration_test_bot",
                can_join_groups = true,
                can_read_all_group_messages = false,
                supports_inline_queries = false,
            },
        });

        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/bot*/getMe"))
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(getMeBody));

        // Default sendMessage. Tests that want different behaviour
        // overlay higher-priority stubs (see StubRateLimitOnce).
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/bot*/sendMessage"))
                .UsingPost())
            .AtPriority(DefaultStubPriority)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", ContentTypeJson)
                .WithBody(DefaultSendMessageBody));

        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/bot*/answerCallbackQuery"))
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"ok\":true,\"result\":true}"));

        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/bot*/editMessageReplyMarkup"))
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"ok\":true,\"result\":true}"));
    }

    private static readonly string DefaultSendMessageBody = JsonSerializer.Serialize(new
    {
        ok = true,
        result = new
        {
            message_id = DefaultMessageId,
            date = 1_700_000_000L,
            chat = new { id = 1L, type = "private" },
            text = string.Empty,
        },
    });

    private static JsonDocument? TryParseJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a synthetic <see cref="ResponseMessage"/> matching the
    /// shape WireMock would have produced from
    /// <c>Response.Create().WithStatusCode(...).WithHeader(...).WithBody(...)</c>.
    /// Used by <see cref="StubRateLimitOnce"/>'s <c>WithCallback</c> path
    /// where the response depends on a runtime call counter that the
    /// fluent builder cannot express.
    /// </summary>
    private static ResponseMessage BuildRawResponse(int statusCode, string jsonBody) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, WireMockList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = new WireMockList<string>(ContentTypeJson),
        },
        BodyData = new BodyData
        {
            BodyAsString = jsonBody,
            DetectedBodyType = BodyType.String,
            Encoding = Encoding.UTF8,
        },
    };
}

/// <summary>
/// One observed HTTP request. <see cref="BodyAsJson"/> is null when
/// the request body was not valid JSON (e.g. a GET probe).
/// </summary>
public sealed record RecordedRequest(string Path, string BodyAsString, JsonDocument? BodyAsJson);

/// <summary>
/// Parsed Telegram <c>sendMessage</c> request body. Limited to the
/// fields current tests assert against; extend as new scenarios need
/// new fields.
/// </summary>
public sealed record SendMessageRequest(
    long ChatId,
    string Text,
    string? ParseMode,
    bool HasReplyMarkup,
    JsonDocument Raw)
{
    public static SendMessageRequest Parse(JsonDocument? doc)
    {
        if (doc is null)
        {
            throw new InvalidOperationException(
                "sendMessage body could not be parsed as JSON — fake API mis-stub?");
        }

        var root = doc.RootElement;
        long chatId = 0;
        if (root.TryGetProperty("chat_id", out var chatIdEl))
        {
            chatId = chatIdEl.ValueKind switch
            {
                JsonValueKind.Number => chatIdEl.GetInt64(),
                JsonValueKind.String when long.TryParse(chatIdEl.GetString(), out var p) => p,
                _ => 0,
            };
        }

        string text = root.TryGetProperty("text", out var textEl)
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        string? parseMode = root.TryGetProperty("parse_mode", out var pmEl)
            ? pmEl.GetString()
            : null;

        bool hasReplyMarkup = root.TryGetProperty("reply_markup", out _);

        return new SendMessageRequest(chatId, text, parseMode, hasReplyMarkup, doc);
    }
}
