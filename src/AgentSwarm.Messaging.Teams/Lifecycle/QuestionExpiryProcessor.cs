using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Diagnostics;
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
/// <see cref="TeamsCardStatuses.Expired"/>.
/// </para>
/// <para>
/// <b>Iter-8 fix #4 — recoverable delete failures (compensation path).</b> When
/// <see cref="ITeamsCardManager.DeleteCardAsync"/> throws after the CAS has succeeded,
/// the processor falls back to
/// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, System.Threading.CancellationToken)"/>
/// with <see cref="CardUpdateAction.MarkExpired"/>. The update path uses Bot Framework's
/// <c>UpdateActivityAsync</c> (with its own stale-404 fallback that <i>sends a fresh
/// replacement card</i>) and persists the card-state row as
/// <see cref="TeamsCardStatuses.Expired"/> on success — so the user sees an "Expired"
/// notice in place of the original interactive card even when the delete API is
/// temporarily unavailable. The CAS-first order is intentional: flipping to
/// delete-first would risk deleting a confirmation card a concurrent user-resolution
/// just rendered (the card-state row's <c>Status</c> lags the Teams activity update,
/// so a status guard cannot eliminate the race). Both failure paths log at error level
/// and surface the orphan to the next scan via card-state inspection in future ops
/// tooling, but the user-visible lifecycle is closed atomically with the CAS.
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
        // Stage 6.3 iter-5 evaluator feedback item 2 (structural fix) — wrap the
        // ENTIRE worker lifecycle in a TeamsLogScope so the lifecycle log lines
        // (startup-disabled at line ~125, startup at line ~135, per-tick unhandled
        // exception at line ~152, and the per-scan "found N expired" debug at line
        // ~187 inside ProcessOnceAsync) ALL carry the canonical CorrelationId /
        // TenantId / UserId enrichment §6.3 step 5 demands. Background workers
        // have no inbound activity, so we mint synthetic worker-scoped values
        // here:
        //   * CorrelationId = "expiry-worker-{guid}" — a per-process worker id so
        //     log entries from a single worker lifetime can be threaded together
        //     across the scan loop.
        //   * TenantId      = "system" — identical sentinel to the health checks
        //     so dashboards can filter `TenantId == "system"` to surface ALL
        //     non-user-driven system logs.
        //   * UserId        = "system-question-expiry-worker" — distinguishes
        //     this worker's lifecycle logs from health-check probes / other
        //     background processors on dashboards.
        // The per-question scope inside ProcessOnceAsync (see below) inherits this
        // worker scope as its parent but overrides every key with the question's
        // own CorrelationId / TenantId / TargetUserId — TeamsLogContext.Push
        // composes parent-child scopes correctly (an inner scope's non-null value
        // shadows the parent's value for the lifetime of the inner scope).
        var workerCorrelationId = $"expiry-worker-{Guid.NewGuid():N}";
        using var workerScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: workerCorrelationId,
            tenantId: WorkerSystemTenantId,
            userId: WorkerSystemUserId);

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
    /// Stage 6.3 iter-5 evaluator feedback item 2 — synthetic TenantId pushed onto
    /// the worker-level <see cref="TeamsLogScope"/> for lifecycle log enrichment.
    /// </summary>
    public const string WorkerSystemTenantId = "system";

    /// <summary>
    /// Stage 6.3 iter-5 evaluator feedback item 2 — synthetic UserId pushed onto
    /// the worker-level <see cref="TeamsLogScope"/>. Identifies log entries from
    /// this worker's lifecycle / scan-loop paths on dashboards.
    /// </summary>
    public const string WorkerSystemUserId = "system-question-expiry-worker";

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

        // Stage 6.3 iter-5 evaluator feedback item 2 — wrap the SCAN-level log
        // path (the "found N expired questions" debug at the top of this method
        // and any pre-per-question entries) in a TeamsLogScope so callers that
        // invoke this method directly (the §3.3 integration tests) still get the
        // three canonical enrichment keys on every emitted log entry, NOT only
        // those that originate from inside the per-question scope below. When
        // ExecuteAsync drives the loop, the outer worker scope is already active
        // and TeamsLogContext.Push composes parent-child correctly — supplying
        // a per-scan CorrelationId here overrides the worker's correlation ID
        // for the duration of the scan but inherits the worker's TenantId /
        // UserId. When tests call this method directly with no parent scope,
        // the synthetic system tenant/user values match the worker path so
        // dashboards are consistent across both entry points.
        var scanCorrelationId = $"expiry-scan-{Guid.NewGuid():N}";
        using var scanScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: scanCorrelationId,
            tenantId: WorkerSystemTenantId,
            userId: WorkerSystemUserId);

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

            // Stage 6.3 iter-4 evaluator feedback item 6 (structural fix) — wrap
            // the per-question pipeline in a TeamsLogScope so every _logger call
            // emitted while processing this question (the CAS-race skip log, the
            // DeleteCardAsync failure log, and the UpdateCardAsync fallback
            // failure log) carries the question's CorrelationId (and the
            // requester's UserId / TenantId when available on the row). The
            // background worker has no inbound activity context, so without this
            // scope the per-question logs would lack the enrichment §6.3 step 5
            // requires. The scope is layered per question — disposing at the end
            // of each iteration pops back to the worker's outer (empty) context.
            using var questionScope = TeamsLogScope.BeginScope(
                _logger,
                correlationId: question.CorrelationId,
                tenantId: question.TenantId,
                userId: question.TargetUserId);

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
                // Iter-8 fix #4: instead of silently swallowing the delete failure and
                // leaving an orphan card in the channel forever, fall back to the
                // UpdateCardAsync(MarkExpired) compensation path. UpdateActivityAsync has
                // its own stale-404 fallback that sends a replacement card, so a
                // transient delete failure does not necessarily block the update. On
                // fallback success the card-state row lands at Expired (per the
                // canonical MarkExpired → TeamsCardStatuses.Expired mapping on the
                // connector). On fallback failure we log at error level — the durable
                // question state has already moved to Expired so the user-facing approval
                // is dead, but an operator-visible card may remain.
                _logger.LogError(
                    ex,
                    "ITeamsCardManager.DeleteCardAsync for question {QuestionId} failed after Open→Expired CAS; falling back to MarkExpired card update.",
                    question.QuestionId);

                try
                {
                    await _cardManager
                        .UpdateCardAsync(question.QuestionId, CardUpdateAction.MarkExpired, ct)
                        .ConfigureAwait(false);
                    processed++;
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(
                        fallbackEx,
                        "ITeamsCardManager.UpdateCardAsync(MarkExpired) compensation for question {QuestionId} also failed; orphan card remains visible.",
                        question.QuestionId);
                }
            }
        }

        return processed;
    }
}
