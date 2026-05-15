using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram.Polling;

/// <summary>
/// Long-polling receiver for the Telegram Bot API, intended for local
/// development and CI scenarios where the gateway is not publicly
/// reachable for webhook callbacks. Production deployments use the
/// Stage 2.4 webhook controller; this service is mutually exclusive with
/// webhook mode (the conflict is rejected at host startup by
/// <see cref="TelegramOptionsValidator"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Loop semantics.</b> The service issues
/// <c>getUpdates(offset, timeout=PollingTimeoutSeconds)</c> in a loop.
/// Each batch is iterated update-by-update; the offset advances to
/// <c>update.Id + 1</c> ONLY after the inbound pipeline returns (regardless
/// of <see cref="PipelineResult.Handled"/> / <see cref="PipelineResult.Succeeded"/>
/// — both outcomes are terminal at the pipeline boundary). If the mapper
/// or the pipeline throws on a particular update the batch is broken and
/// the next <c>getUpdates</c> call re-polls from that update — this is the
/// at-least-once recovery primitive for polling mode (webhook mode has the
/// durable <c>InboundUpdate</c> sweep instead). The pipeline's
/// release-on-throw guard ensures the dedup gate does not silently swallow
/// the retry.
/// </para>
/// <para>
/// <b>Graceful shutdown.</b> The stopping <see cref="CancellationToken"/>
/// flows into both
/// <see cref="ITelegramUpdatePoller.DeleteWebhookAsync"/> (the startup
/// webhook clear) AND <see cref="ITelegramUpdatePoller.GetUpdatesAsync"/>
/// so any in-flight HTTP call aborts immediately. Both calls execute
/// inside the same outer <c>try</c>/<c>finally</c>: if cancellation fires
/// before the first poll — for instance during the startup
/// <c>deleteWebhook</c> call — the <c>finally</c> block still logs the
/// final offset (<c>(none)</c> in that case). Exiting cleanly is what
/// <see cref="BackgroundService"/> expects from a stop-driven shutdown.
/// </para>
/// <para>
/// <b>Webhook conflict prevention.</b> On the first iteration the service
/// calls <see cref="ITelegramUpdatePoller.DeleteWebhookAsync"/> with
/// <c>dropPendingUpdates=false</c>. Telegram returns HTTP 409 from
/// <c>getUpdates</c> while a webhook is registered server-side, even
/// after the operator removes <c>WebhookUrl</c> from config; deleting it
/// here makes the switch from webhook → polling friction-free without
/// silently discarding pending updates.
/// </para>
/// <para>
/// <b>Error handling.</b> Transient failures (network, 5xx) are logged at
/// <see cref="LogLevel.Warning"/> and the loop backs off for
/// <see cref="TransientBackoff"/> before retrying. Auth/config failures
/// (<see cref="ApiRequestException"/> with 401/403) are logged at
/// <see cref="LogLevel.Error"/> — the loop still continues because dev
/// operators frequently rotate tokens and we do not want to crash the
/// host on a misconfiguration. The mutual-exclusion guard already
/// rejects an outright missing token at startup.
/// </para>
/// </remarks>
internal sealed class TelegramPollingService : BackgroundService
{
    /// <summary>How long to wait after a transient poll failure before retrying.</summary>
    internal static readonly TimeSpan DefaultTransientBackoff = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Overridable backoff after a transient poll failure or a broken
    /// batch. Test-scoped seam: production code paths construct the
    /// service via DI without setting this and pick up the
    /// <see cref="DefaultTransientBackoff"/> default; tests use object-
    /// initializer syntax to shorten the delay so the suite is fast.
    /// </summary>
    internal TimeSpan TransientBackoff { get; init; } = DefaultTransientBackoff;

    private readonly ITelegramUpdatePoller _poller;
    private readonly ITelegramUpdatePipeline _pipeline;
    private readonly IOptions<TelegramOptions> _options;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        ITelegramUpdatePoller poller,
        ITelegramUpdatePipeline pipeline,
        IOptions<TelegramOptions> options,
        ILogger<TelegramPollingService> logger)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.UsePolling)
        {
            // Defense-in-depth: AddTelegram only registers this service when
            // UsePolling=true, but if a downstream caller wires it manually
            // with UsePolling=false we should not start the loop.
            _logger.LogInformation(
                "Telegram polling service is disabled (Telegram:UsePolling is false). Exiting without polling.");
            return;
        }

        var timeout = ResolvePollingTimeout(opts);
        _logger.LogInformation(
            "Telegram polling service starting. PollingTimeoutSeconds={Timeout}", timeout);

        // `offset` is declared BEFORE the startup webhook clear so that the
        // finally block can log the final offset even if cancellation fires
        // during the DeleteWebhookAsync call (covered by
        // TelegramPollingServiceTests.ExecuteAsync_LogsFinalOffset_WhenCancelledDuringWebhookClear).
        int? offset = null;
        try
        {
            await TryClearStaleWebhookAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                Update[] updates;
                try
                {
                    updates = await _poller
                        .GetUpdatesAsync(offset, timeout, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Falls through to the outer catch which logs the final
                    // offset and exits ExecuteAsync.
                    throw;
                }
                catch (ApiRequestException ex) when (IsAuthFailure(ex))
                {
                    _logger.LogError(
                        ex,
                        "Telegram polling auth/config failure (HTTP {Code}). Backing off {Backoff} before retrying.",
                        ex.HttpStatusCode,
                        TransientBackoff);
                    await DelayWithCancellationAsync(TransientBackoff, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Telegram polling transient failure. Backing off {Backoff} before retrying. CurrentOffset={Offset}",
                        TransientBackoff,
                        offset);
                    await DelayWithCancellationAsync(TransientBackoff, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (updates is null || updates.Length == 0)
                {
                    // Empty batch — long-poll timed out without new traffic.
                    // Loop back without changing offset.
                    continue;
                }

                // Process updates one-by-one so a single failure breaks the
                // batch and re-polls from the failed update next iteration.
                // Telegram's getUpdates semantics: server-side acknowledgement
                // is implicit when the next poll passes offset > update.Id;
                // by NOT advancing past a failed update we get an automatic
                // at-least-once retry.
                var batchAdvanced = true;
                foreach (var update in updates)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        batchAdvanced = false;
                        break;
                    }

                    try
                    {
                        var messengerEvent = TelegramUpdateMapper.Map(update);
                        await _pipeline
                            .ProcessAsync(messengerEvent, stoppingToken)
                            .ConfigureAwait(false);

                        // Advance offset only after a successful pipeline
                        // return. Subsequent getUpdates calls won't redeliver
                        // anything with id <= update.Id.
                        offset = update.Id + 1;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Re-throw so the outer cancellation handler runs.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Telegram polling: failed to process update {UpdateId}; batch broken and will retry on next poll. CurrentOffset={Offset}",
                            update.Id,
                            offset);
                        // Leave offset unchanged so the failed update is
                        // re-fetched. Break out of the foreach so we do not
                        // skip past it by advancing on a sibling success.
                        batchAdvanced = false;
                        break;
                    }
                }

                if (!batchAdvanced)
                {
                    // Apply a small backoff so we don't tight-loop on a
                    // persistent failure / cancellation race.
                    await DelayWithCancellationAsync(TransientBackoff, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected: host requested shutdown — may have fired during the
            // startup DeleteWebhookAsync call OR during the long-poll loop.
            // Either way, the finally block below logs the final offset.
        }
        finally
        {
            _logger.LogInformation(
                "Telegram polling service stopped. FinalOffset={FinalOffset}",
                offset.HasValue ? offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "(none)");
        }
    }

    private static int ResolvePollingTimeout(TelegramOptions opts)
    {
        // Clamp to the Telegram-server limit (50s); fall back to 30s when
        // unset. The validator rejects out-of-range values at startup, but
        // this guard keeps the loop safe if tests construct options
        // bypassing the validator.
        var seconds = opts.PollingTimeoutSeconds;
        if (seconds <= 0)
        {
            return 30;
        }

        return Math.Min(seconds, 50);
    }

    private async Task TryClearStaleWebhookAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _poller
                .DeleteWebhookAsync(dropPendingUpdates: false, stoppingToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Telegram polling service cleared any stale webhook before starting long-poll loop.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Non-fatal: if there was no webhook, or the API call failed,
            // the loop will surface the real failure via the GetUpdates 409.
            _logger.LogWarning(
                ex,
                "Telegram polling service could not clear stale webhook on startup; continuing.");
        }
    }

    private static async Task DelayWithCancellationAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Swallow — caller's loop condition (or outer catch) will react.
        }
    }

    private static bool IsAuthFailure(ApiRequestException ex)
    {
        // HTTP 401 (Unauthorized) and 403 (Forbidden) are the canonical
        // "your bot token is wrong / the bot has been blocked" failures.
        var status = ex.HttpStatusCode;
        return status == System.Net.HttpStatusCode.Unauthorized
            || status == System.Net.HttpStatusCode.Forbidden;
    }
}
