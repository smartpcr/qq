using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Lifecycle;

/// <summary>
/// Periodic background worker that promotes expired but still-open
/// <see cref="AgentQuestion"/> records to <see cref="AgentQuestionStatuses.Expired"/> and
/// removes the corresponding Adaptive Card from the originating Teams conversation.
/// Implements step 6 of <c>implementation-plan.md</c> §3.3.
/// </summary>
/// <remarks>
/// <para>
/// The processor injects exactly the three dependencies enumerated by the Stage 3.3 brief:
/// <see cref="IAgentQuestionStore"/>, <see cref="ITeamsCardManager"/>, and
/// <see cref="TeamsMessagingOptions"/>. It deliberately does NOT depend on
/// <see cref="ICardStateStore"/> or directly invoke
/// <c>CloudAdapter.ContinueConversationAsync</c> / <c>UpdateActivityAsync</c> /
/// <c>DeleteActivityAsync</c> — every Teams-side concern (card-state lookup,
/// conversation-reference rehydration, Bot Framework call, inline retry) is encapsulated
/// behind <see cref="ITeamsCardManager.DeleteCardAsync"/> on
/// <see cref="TeamsMessengerConnector"/>. This is the canonical separation called out
/// at <c>implementation-plan.md</c> line 214 and the architecture contract for
/// <see cref="ICardStateStore"/> at <c>architecture.md</c> §4.3 (the store surface is
/// limited to <see cref="ICardStateStore.SaveAsync"/>,
/// <see cref="ICardStateStore.GetByQuestionIdAsync"/>, and
/// <see cref="ICardStateStore.UpdateStatusAsync"/>).
/// </para>
/// <para>
/// <b>Per-question pipeline:</b> for each question returned by
/// <see cref="IAgentQuestionStore.GetOpenExpiredAsync"/> the processor (a) attempts the
/// CAS transition <see cref="AgentQuestionStatuses.Open"/> →
/// <see cref="AgentQuestionStatuses.Expired"/> via
/// <see cref="IAgentQuestionStore.TryUpdateStatusAsync"/>; if the CAS fails the
/// question was already resolved or expired by another process, so the row is skipped;
/// (b) on a successful CAS calls
/// <see cref="ITeamsCardManager.DeleteCardAsync"/> which performs the inline-retry
/// Bot Framework delete and updates the card-state row to
/// <see cref="TeamsCardStatuses.Expired"/>. Card-delete failures are logged at error
/// level — the durable question state has already moved to <see cref="AgentQuestionStatuses.Expired"/>
/// (the user-facing approval is dead) and the worst-case outcome is a stale visible card
/// in the channel that an operator can later remove. Re-issuing a delete on the next scan
/// is left to a future cleanup workstream because the canonical card vocabulary
/// (Pending/Answered/Expired) does not support a "pending-deletion" state without
/// widening the <see cref="ICardStateStore"/> surface.
/// </para>
/// <para>
/// <b>Cadence and batch size</b> are read from <see cref="TeamsMessagingOptions"/>:
/// <see cref="TeamsMessagingOptions.ExpiryScanIntervalSeconds"/> defaults to 60 seconds
/// and <see cref="TeamsMessagingOptions.ExpiryBatchSize"/> defaults to 50. A
/// non-positive interval disables the periodic loop (the worker still runs once at
/// startup and then exits) so an operator can opt out without changing DI registration.
/// </para>
/// </remarks>
public sealed class QuestionExpiryProcessor : BackgroundService
{
    private readonly IAgentQuestionStore _questionStore;
    private readonly ITeamsCardManager _cardManager;
    private readonly TeamsMessagingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuestionExpiryProcessor> _logger;

    /// <summary>Construct the processor. Every parameter is null-guarded.</summary>
    public QuestionExpiryProcessor(
        IAgentQuestionStore questionStore,
        ITeamsCardManager cardManager,
        TeamsMessagingOptions options,
        ILogger<QuestionExpiryProcessor> logger)
        : this(questionStore, cardManager, options, TimeProvider.System, logger)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>
    /// so unit tests can advance the clock for cutoff comparisons without sleeping.
    /// </summary>
    public QuestionExpiryProcessor(
        IAgentQuestionStore questionStore,
        ITeamsCardManager cardManager,
        TeamsMessagingOptions options,
        TimeProvider timeProvider,
        ILogger<QuestionExpiryProcessor> logger)
    {
        _questionStore = questionStore ?? throw new ArgumentNullException(nameof(questionStore));
        _cardManager = cardManager ?? throw new ArgumentNullException(nameof(cardManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _options.ExpiryScanIntervalSeconds;
        var batchSize = Math.Max(1, _options.ExpiryBatchSize);

        if (intervalSeconds <= 0)
        {
            _logger.LogInformation(
                "QuestionExpiryProcessor disabled — ExpiryScanIntervalSeconds is {Interval}.",
                intervalSeconds);
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        _logger.LogInformation(
            "QuestionExpiryProcessor started (interval={Interval}, batchSize={BatchSize}).",
            interval,
            batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(batchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in QuestionExpiryProcessor scan; continuing.");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Run a single expiry scan. Public so tests can trigger one iteration without
    /// driving the <see cref="BackgroundService.ExecuteAsync"/> loop or wall-clock delays.
    /// </summary>
    /// <param name="batchSize">Maximum number of expired questions to process this scan.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of questions that successfully transitioned to Expired.</returns>
    public async Task<int> ProcessOnceAsync(int batchSize, CancellationToken ct)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
        }

        var cutoff = _timeProvider.GetUtcNow();
        var expired = await _questionStore.GetOpenExpiredAsync(cutoff, batchSize, ct).ConfigureAwait(false);
        if (expired.Count == 0)
        {
            return 0;
        }

        _logger.LogDebug(
            "QuestionExpiryProcessor scan found {Count} expired open question(s).",
            expired.Count);

        var processed = 0;
        foreach (var question in expired)
        {
            ct.ThrowIfCancellationRequested();

            var transitioned = await _questionStore
                .TryUpdateStatusAsync(
                    question.QuestionId,
                    AgentQuestionStatuses.Open,
                    AgentQuestionStatuses.Expired,
                    ct)
                .ConfigureAwait(false);

            if (!transitioned)
            {
                _logger.LogDebug(
                    "Skipping question {QuestionId} — concurrent process won the CAS.",
                    question.QuestionId);
                continue;
            }

            try
            {
                await _cardManager.DeleteCardAsync(question.QuestionId, ct).ConfigureAwait(false);
                processed++;
            }
            catch (Exception ex)
            {
                // Card delete failed after the question moved to Expired. The durable
                // resolution stands; an operator-visible card may remain in the channel
                // until manual cleanup. We do NOT roll the question back to Open because
                // the deadline has truly elapsed and re-promoting Open would re-trigger
                // approval logic on a question that has already missed its SLA.
                _logger.LogError(
                    ex,
                    "ITeamsCardManager.DeleteCardAsync for question {QuestionId} failed after Open→Expired CAS; question remains Expired with stale card.",
                    question.QuestionId);
            }
        }

        return processed;
    }
}
