// -----------------------------------------------------------------------
// <copyright file="HttpClientSlackEphemeralResponder.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISlackEphemeralResponder"/> that posts a JSON
/// envelope (<c>{ "response_type": "ephemeral", "text": "..." }</c>)
/// to Slack's per-invocation <c>response_url</c> over a shared
/// <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implements implementation step 9 of Stage 5.1
/// (<c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>):
/// returning ephemeral error messages for unrecognized sub-commands or
/// missing arguments after the controller has already ACK'd the HTTP
/// request. The async ingestor cannot write to the original HTTP
/// response (it has been completed); <c>response_url</c> is the
/// Slack-supported channel for late replies.
/// </para>
/// <para>
/// All HTTP errors are swallowed and logged. Slack accepts the URL for
/// up to five posts within ~30 minutes; rendering a network glitch as
/// an unhandled exception would dead-letter an otherwise-correct
/// dispatch result.
/// </para>
/// </remarks>
internal sealed class HttpClientSlackEphemeralResponder : ISlackEphemeralResponder
{
    /// <summary>
    /// Name of the typed <see cref="HttpClient"/> registered through
    /// <see cref="IHttpClientFactory"/>. Hosts can layer resilience
    /// handlers (retry, circuit-breaker, telemetry) by name.
    /// </summary>
    public const string HttpClientName = "slack-response-url";

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<HttpClientSlackEphemeralResponder> logger;
    private readonly TimeSpan requestTimeout;

    public HttpClientSlackEphemeralResponder(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpClientSlackEphemeralResponder> logger)
        : this(httpClientFactory, logger, DefaultRequestTimeout)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a unit test pin the HTTP
    /// request timeout.
    /// </summary>
    public HttpClientSlackEphemeralResponder(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpClientSlackEphemeralResponder> logger,
        TimeSpan requestTimeout)
    {
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public async Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(responseUrl))
        {
            this.logger.LogInformation(
                "Slack ephemeral responder skipped a reply because no response_url was supplied. Message='{Message}'.",
                message);
            return;
        }

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, responseUrl)
        {
            Content = JsonContent.Create(new
            {
                response_type = "ephemeral",
                text = message,
            }),
        };

        using CancellationTokenSource timeoutCts = new(this.requestTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            HttpClient client = this.httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client
                .SendAsync(request, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                this.logger.LogWarning(
                    "Slack ephemeral responder received HTTP {StatusCode} from response_url; the user will not see the message.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            this.logger.LogWarning(
                "Slack ephemeral responder timed out after {TimeoutMs} ms; the user will not see the message.",
                this.requestTimeout.TotalMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack ephemeral responder transport error posting to response_url.");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack ephemeral responder failed unexpectedly while posting to response_url.");
        }
    }
}
