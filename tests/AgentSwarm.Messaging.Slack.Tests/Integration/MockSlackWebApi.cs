// -----------------------------------------------------------------------
// <copyright file="MockSlackWebApi.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Integration;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Stage 8.2 mock Slack Web API server. Hosts an ASP.NET Core
/// <see cref="TestServer"/> that simulates the five Slack endpoints the
/// connector calls -- <c>chat.postMessage</c>, <c>chat.update</c>,
/// <c>views.open</c>, <c>auth.test</c>, <c>usergroups.users.list</c> --
/// plus an opt-in <c>response_url</c> sink for ephemeral replies. Every
/// inbound request is recorded so integration tests can assert against
/// the exact payload the production Slack clients posted, and the
/// server returns Slack-shaped success JSON so the production
/// callers' parse paths exercise the same code that runs against the
/// real API.
/// </summary>
/// <remarks>
/// <para>
/// The server is paired with <see cref="CreateRewritingHandler"/>, a
/// <see cref="DelegatingHandler"/> that intercepts every HTTP request
/// targeted at the real Slack hostnames (<c>slack.com</c>,
/// <c>hooks.slack.com</c>) and rewrites the request to point at the
/// TestServer's primary handler. The fixture wires the handler as the
/// primary message handler of every named <see cref="HttpClient"/> the
/// Slack connector registers
/// (<c>slack-thread-postmessage</c>, <c>slack-chat-update</c>,
/// <c>slack-views-open</c>, <c>slack-outbound-dispatch</c>,
/// <c>slack-response-url</c>, <c>slack-chat-postmessage</c>) so the
/// production clients reach this mock without any URL changes in the
/// product code.
/// </para>
/// <para>
/// All recorded request properties are stored as Slack returns them
/// in the JSON body (channel id, thread_ts, blocks, etc.). Tests
/// inspect them via the typed accessor lists exposed below.
/// </para>
/// </remarks>
internal sealed class MockSlackWebApi : IDisposable
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IHost host;
    private readonly TestServer testServer;
    private readonly ConcurrentQueue<RecordedChatPostMessage> postMessages = new();
    private readonly ConcurrentQueue<RecordedChatUpdate> chatUpdates = new();
    private readonly ConcurrentQueue<RecordedViewsOpen> viewsOpens = new();
    private readonly ConcurrentQueue<RecordedAuthTest> authTests = new();
    private readonly ConcurrentQueue<RecordedUserGroupsList> userGroupsLists = new();
    private readonly ConcurrentQueue<RecordedEphemeralResponse> ephemeralResponses = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> userGroupMemberships =
        new(StringComparer.Ordinal);
    private long messageTsCounter = 1_700_000_000L;
    private long viewIdCounter = 1;

    /// <summary>
    /// Default usergroup id reported by <c>usergroups.users.list</c>
    /// when the test has not seeded a more specific membership map.
    /// </summary>
    public const string DefaultMockUserGroupId = "S_MOCK_AUTHORIZED";

    /// <summary>
    /// Default workspace name returned by <c>auth.test</c>.
    /// </summary>
    public const string DefaultMockTeamName = "MockSlackTestTeam";

    /// <summary>
    /// Builds and starts the mock server.
    /// </summary>
    public MockSlackWebApi()
    {
        IHostBuilder hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        this.MapEndpoints(endpoints);
                    });
                });
            });

        this.host = hostBuilder.Start();
        this.testServer = this.host.GetTestServer();
    }

    /// <summary>
    /// Every <c>chat.postMessage</c> request observed, in the order
    /// they arrived. Includes both the connector-side thread root
    /// post (<see cref="HttpClientSlackChatPostMessageClient"/>) and
    /// the outbound dispatcher's threaded replies
    /// (<see cref="HttpClientSlackOutboundDispatchClient"/>).
    /// </summary>
    public IReadOnlyList<RecordedChatPostMessage> PostMessages => this.postMessages.ToArray();

    /// <summary>Every <c>chat.update</c> request observed.</summary>
    public IReadOnlyList<RecordedChatUpdate> ChatUpdates => this.chatUpdates.ToArray();

    /// <summary>Every <c>views.open</c> request observed.</summary>
    public IReadOnlyList<RecordedViewsOpen> ViewsOpens => this.viewsOpens.ToArray();

    /// <summary>Every <c>auth.test</c> request observed.</summary>
    public IReadOnlyList<RecordedAuthTest> AuthTests => this.authTests.ToArray();

    /// <summary>Every <c>usergroups.users.list</c> request observed.</summary>
    public IReadOnlyList<RecordedUserGroupsList> UserGroupsLists => this.userGroupsLists.ToArray();

    /// <summary>
    /// Every ephemeral <c>response_url</c> POST observed. Slack's
    /// production response_url targets <c>hooks.slack.com</c>; the
    /// rewriting handler routes those to <c>/mock/response_url</c>
    /// here so the tests can assert the ephemeral text reached the
    /// user without standing up a separate webhook receiver.
    /// </summary>
    public IReadOnlyList<RecordedEphemeralResponse> EphemeralResponses => this.ephemeralResponses.ToArray();

    /// <summary>
    /// Stable base URL used when the rewriting handler rebuilds the
    /// request URI. The TestServer is otherwise reachable only via
    /// its handler -- this base URL never leaves the rewriting
    /// handler so any value works as long as it is a well-formed
    /// absolute URI.
    /// </summary>
    public Uri MockBaseAddress { get; } = new("http://mock-slack.localhost");

    /// <summary>
    /// Builds a <see cref="DelegatingHandler"/> that rewrites every
    /// request targeted at <c>slack.com</c> or <c>hooks.slack.com</c>
    /// (real Slack Web API and response_url hosts) to point at this
    /// mock TestServer. Each call returns a fresh handler so it can
    /// be attached to multiple named <see cref="HttpClient"/>
    /// pipelines via <c>ConfigurePrimaryHttpMessageHandler</c>.
    /// </summary>
    public DelegatingHandler CreateRewritingHandler()
    {
        return new RewritingHandler(this.testServer.CreateHandler(), this.MockBaseAddress);
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> that posts directly to the
    /// mock without URL rewriting. Useful for in-test scaffolding
    /// that needs to inspect the mock server (rare).
    /// </summary>
    public HttpClient CreateDirectClient() => this.testServer.CreateClient();

    /// <summary>
    /// Seeds the mock's <c>usergroups.users.list</c> response for the
    /// supplied user-group id. The fixture calls this so the
    /// production <see cref="SlackNet.SlackApiClient"/> -> mock
    /// TestServer round-trip returns the seeded membership list,
    /// proving end-to-end that the mock Slack Web API endpoint is
    /// actually exercised by the host wiring (Stage 8.2 evaluator
    /// item 2). When no entry is seeded the mock falls back to the
    /// default <c>U_MOCK_USER</c> list.
    /// </summary>
    public void SeedUserGroupMembers(string userGroupId, params string[] memberUserIds)
    {
        if (string.IsNullOrEmpty(userGroupId))
        {
            throw new ArgumentException("User group id must be supplied.", nameof(userGroupId));
        }

        this.userGroupMemberships[userGroupId] = memberUserIds.ToArray();
    }

    /// <summary>
    /// Builds a fresh <see cref="HttpClient"/> whose primary handler
    /// is a new <see cref="CreateRewritingHandler"/>. Used by the
    /// fixture's SlackNet wiring so the production
    /// <see cref="SlackNet.SlackApiClient"/> POSTs end up on this
    /// TestServer instead of the real Slack API.
    /// </summary>
    public HttpClient CreateHttpClient()
    {
        return new HttpClient(this.CreateRewritingHandler());
    }

    /// <summary>
    /// Clears every recorded request collection. Call between
    /// scenarios that reuse the same fixture instance.
    /// </summary>
    public void Reset()
    {
        while (this.postMessages.TryDequeue(out _))
        {
        }

        while (this.chatUpdates.TryDequeue(out _))
        {
        }

        while (this.viewsOpens.TryDequeue(out _))
        {
        }

        while (this.authTests.TryDequeue(out _))
        {
        }

        while (this.userGroupsLists.TryDequeue(out _))
        {
        }

        while (this.ephemeralResponses.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for at least
    /// <paramref name="minCount"/> entries to appear in
    /// <paramref name="source"/>. Returns immediately once the count
    /// is satisfied or the timeout expires.
    /// </summary>
    public static async Task<bool> WaitForCountAsync<T>(
        Func<IReadOnlyList<T>> source,
        int minCount,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (source().Count >= minCount)
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return source().Count >= minCount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.testServer.Dispose();
        this.host.Dispose();
    }

    private void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/chat.postMessage", this.HandleChatPostMessageAsync);
        endpoints.MapPost("/api/chat.update", this.HandleChatUpdateAsync);
        endpoints.MapPost("/api/views.open", this.HandleViewsOpenAsync);
        endpoints.MapPost("/api/auth.test", this.HandleAuthTestAsync);

        // usergroups.users.list is sent by SlackNet's typed client as
        // an HTTP GET with the usergroup id on the query string (see
        // SlackNet.WebApi.UserGroupUsersApi.List). Also register POST
        // so a hand-rolled HttpClient test (or a future SlackNet
        // version that switches verbs) still resolves to the mock.
        endpoints.MapGet("/api/usergroups.users.list", this.HandleUserGroupsListAsync);
        endpoints.MapPost("/api/usergroups.users.list", this.HandleUserGroupsListAsync);

        endpoints.MapPost("/mock/response_url/{*tail}", this.HandleResponseUrlAsync);
    }

    private async Task HandleChatPostMessageAsync(HttpContext context)
    {
        string body = await ReadBodyAsync(context).ConfigureAwait(false);
        string? channel = ExtractJsonString(body, "channel");
        string? threadTs = ExtractJsonString(body, "thread_ts");
        string ts = this.NextMessageTs();
        string? auth = context.Request.Headers["Authorization"].ToString();

        this.postMessages.Enqueue(new RecordedChatPostMessage(
            ChannelId: channel ?? string.Empty,
            ThreadTs: threadTs,
            Body: body,
            Authorization: auth,
            Ts: ts));

        await WriteJsonAsync(context, new
        {
            ok = true,
            channel = channel ?? "C_MOCK",
            ts,
            message = new
            {
                ts,
                text = ExtractJsonString(body, "text") ?? string.Empty,
            },
        }).ConfigureAwait(false);
    }

    private async Task HandleChatUpdateAsync(HttpContext context)
    {
        string body = await ReadBodyAsync(context).ConfigureAwait(false);
        string? channel = ExtractJsonString(body, "channel");
        string? messageTs = ExtractJsonString(body, "ts");

        this.chatUpdates.Enqueue(new RecordedChatUpdate(
            ChannelId: channel ?? string.Empty,
            MessageTs: messageTs ?? string.Empty,
            Body: body,
            Authorization: context.Request.Headers["Authorization"].ToString()));

        await WriteJsonAsync(context, new
        {
            ok = true,
            channel = channel ?? "C_MOCK",
            ts = messageTs ?? this.NextMessageTs(),
        }).ConfigureAwait(false);
    }

    private async Task HandleViewsOpenAsync(HttpContext context)
    {
        string body = await ReadBodyAsync(context).ConfigureAwait(false);
        string? triggerId = ExtractJsonString(body, "trigger_id");
        string viewId = "V_MOCK_" + Interlocked.Increment(ref this.viewIdCounter);

        this.viewsOpens.Enqueue(new RecordedViewsOpen(
            TriggerId: triggerId ?? string.Empty,
            Body: body,
            Authorization: context.Request.Headers["Authorization"].ToString(),
            ViewId: viewId));

        await WriteJsonAsync(context, new
        {
            ok = true,
            view = new
            {
                id = viewId,
                type = "modal",
                callback_id = ExtractJsonString(body, "callback_id") ?? "agent_comment_modal",
            },
        }).ConfigureAwait(false);
    }

    private async Task HandleAuthTestAsync(HttpContext context)
    {
        string body = await ReadBodyAsync(context).ConfigureAwait(false);
        string auth = context.Request.Headers["Authorization"].ToString();
        this.authTests.Enqueue(new RecordedAuthTest(
            Authorization: auth,
            Body: body));

        await WriteJsonAsync(context, new
        {
            ok = true,
            url = "https://mock.slack.test/",
            team = DefaultMockTeamName,
            user = "MockBot",
            team_id = "T_MOCK",
            user_id = "U_MOCK_BOT",
            bot_id = "B_MOCK_BOT",
        }).ConfigureAwait(false);
    }

    private async Task HandleUserGroupsListAsync(HttpContext context)
    {
        // SlackNet's UserGroupUsersApi.List sends an HTTP GET with the
        // usergroup id on the query string; older / future SDK shapes
        // (and direct HttpClient callers) may use form-encoded POST
        // body. Both surfaces are normalised here so the mock can
        // resolve the usergroup id regardless of caller shape.
        string body = string.Empty;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            body = await ReadBodyAsync(context).ConfigureAwait(false);
        }

        string? usergroupId = context.Request.Query["usergroup"].FirstOrDefault();
        if (string.IsNullOrEmpty(usergroupId))
        {
            usergroupId = ExtractFormField(body, "usergroup")
                ?? ExtractJsonString(body, "usergroup");
        }

        this.userGroupsLists.Enqueue(new RecordedUserGroupsList(
            Authorization: context.Request.Headers["Authorization"].ToString(),
            Method: context.Request.Method,
            Query: context.Request.QueryString.HasValue ? context.Request.QueryString.Value! : string.Empty,
            UserGroupId: usergroupId ?? string.Empty,
            Body: body));

        IReadOnlyList<string> users = usergroupId is not null
            && this.userGroupMemberships.TryGetValue(usergroupId, out IReadOnlyList<string>? seeded)
                ? seeded
                : new[] { "U_MOCK_USER" };

        await WriteJsonAsync(context, new
        {
            ok = true,
            users,
        }).ConfigureAwait(false);
    }

    private async Task HandleResponseUrlAsync(HttpContext context)
    {
        string body = await ReadBodyAsync(context).ConfigureAwait(false);
        string? responseType = ExtractJsonString(body, "response_type");
        string? text = ExtractJsonString(body, "text");

        this.ephemeralResponses.Enqueue(new RecordedEphemeralResponse(
            ResponseType: responseType,
            Text: text,
            Path: context.Request.Path.Value ?? string.Empty,
            Body: body));

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("ok").ConfigureAwait(false);
    }

    private string NextMessageTs()
    {
        long seconds = Interlocked.Increment(ref this.messageTsCounter);
        return seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".000000";
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using StreamReader reader = new(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);
        context.Request.Body.Position = 0;
        return body;
    }

    private static async Task WriteJsonAsync(HttpContext context, object payload)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        string json = JsonSerializer.Serialize(payload, ResponseJsonOptions);
        await context.Response.WriteAsync(json, Encoding.UTF8).ConfigureAwait(false);
    }

    private static string? ExtractFormField(string body, string fieldName)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        // Slack callers serialize x-www-form-urlencoded pairs separated
        // by '&'. Hand-parse so the mock does not need to allocate a
        // FormReader (which expects the body to be on a request stream).
        string[] pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            int equalsIndex = pair.IndexOf('=');
            string key = equalsIndex < 0
                ? Uri.UnescapeDataString(pair)
                : Uri.UnescapeDataString(pair[..equalsIndex]);
            if (string.Equals(key, fieldName, StringComparison.Ordinal))
            {
                if (equalsIndex < 0 || equalsIndex == pair.Length - 1)
                {
                    return string.Empty;
                }

                return Uri.UnescapeDataString(pair[(equalsIndex + 1)..]);
            }
        }

        return null;
    }

    private static string? ExtractJsonString(string body, string fieldName)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        // Fast in-test extractor: parse the body once and read the
        // requested field as a string. The Slack client payloads are
        // small JSON objects so JsonDocument is cheap; we deliberately
        // avoid building a typed model per endpoint so tests can
        // inspect the raw Body when they need fields beyond what the
        // recorded record surface exposes.
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty(fieldName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A single <c>chat.postMessage</c> request observed by the mock.
    /// </summary>
    public sealed record RecordedChatPostMessage(
        string ChannelId,
        string? ThreadTs,
        string Body,
        string? Authorization,
        string Ts);

    /// <summary>A single <c>chat.update</c> request observed by the mock.</summary>
    public sealed record RecordedChatUpdate(
        string ChannelId,
        string MessageTs,
        string Body,
        string? Authorization);

    /// <summary>A single <c>views.open</c> request observed by the mock.</summary>
    public sealed record RecordedViewsOpen(
        string TriggerId,
        string Body,
        string? Authorization,
        string ViewId);

    /// <summary>A single <c>auth.test</c> request observed by the mock.</summary>
    public sealed record RecordedAuthTest(
        string? Authorization,
        string Body);

    /// <summary>A single <c>usergroups.users.list</c> request observed.</summary>
    public sealed record RecordedUserGroupsList(
        string? Authorization,
        string Method,
        string Query,
        string UserGroupId,
        string Body);

    /// <summary>
    /// A single ephemeral <c>response_url</c> POST observed by the
    /// mock (rewritten from <c>hooks.slack.com</c> to the
    /// <c>/mock/response_url/*</c> sink).
    /// </summary>
    public sealed record RecordedEphemeralResponse(
        string? ResponseType,
        string? Text,
        string Path,
        string Body);

    /// <summary>
    /// <see cref="DelegatingHandler"/> that rewrites every request
    /// targeted at the real Slack hostnames onto the TestServer's
    /// primary handler. The wrapped <see cref="HttpMessageHandler"/>
    /// is the same handler <see cref="TestServer.CreateHandler"/>
    /// returns -- it dispatches each request through the
    /// in-process HTTP pipeline this <see cref="MockSlackWebApi"/>
    /// configured above.
    /// </summary>
    private sealed class RewritingHandler : DelegatingHandler
    {
        private static readonly HashSet<string> SlackHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "slack.com",
            "www.slack.com",
            "hooks.slack.com",
        };

        private readonly Uri mockBaseAddress;

        public RewritingHandler(HttpMessageHandler inner, Uri mockBaseAddress)
            : base(inner)
        {
            this.mockBaseAddress = mockBaseAddress;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null && SlackHosts.Contains(request.RequestUri.Host))
            {
                request.RequestUri = this.RewriteUri(request.RequestUri);
            }

            return base.SendAsync(request, cancellationToken);
        }

        private Uri RewriteUri(Uri original)
        {
            // hooks.slack.com hosts response_url callbacks; rewrite
            // those onto a dedicated /mock/response_url/* path so the
            // chat / view / auth endpoints stay isolated.
            UriBuilder builder = new(this.mockBaseAddress)
            {
                Path = original.Host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase)
                    ? "/mock/response_url" + original.AbsolutePath
                    : original.AbsolutePath,
                Query = original.Query.TrimStart('?'),
            };

            return builder.Uri;
        }
    }
}
