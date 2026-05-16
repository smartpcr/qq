using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Webhook;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// Background consumer that reads <see cref="InboundUpdate.UpdateId"/>
/// items from <see cref="InboundUpdateChannel"/> and drives the
/// corresponding row through <see cref="InboundUpdateProcessor"/>. Runs
/// with bounded parallelism (default 4 workers, configurable via
/// <c>InboundProcessing:Concurrency</c>) and creates a fresh DI scope
/// per item so the scoped <see cref="IInboundUpdateStore"/> is not
/// shared across concurrent rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate scope per item.</b> EF Core's
/// <c>MessagingDbContext</c> is registered scoped — sharing the
/// same context across two concurrent rows would serialise their writes
/// through one connection and cause spurious change-tracker collisions.
/// A scope per item also matches the request-scope model used by the
/// webhook endpoint, so the same pipeline code path runs identically
/// in both contexts.
/// </para>
/// <para>
/// <b>Cancellation.</b> The dispatcher honours
/// <see cref="BackgroundService.StoppingToken"/>: when the host begins
/// shutdown the channel reader exits and any in-flight pipeline call
/// observes the cancellation token. Rows that were enqueued but not yet
/// drained stay in <see cref="IdempotencyStatus.Received"/>; the next
/// process restart's <c>InboundRecoverySweep</c> picks them up.
/// </para>
/// <para>
/// <b>Correlation id propagation.</b> The dispatcher always uses the
/// <see cref="InboundUpdate.CorrelationId"/> persisted by the webhook
/// endpoint as the trace identifier passed to the pipeline — so the
/// asynchronous processing leg shares the request-scoped trace id with
/// the synchronous receive leg per the "All messages include
/// trace/correlation ID" acceptance criterion. Only when the persisted
/// row has a <c>null</c>/blank <c>CorrelationId</c> (legacy rows from
/// before the column existed, or hand-seeded test data) does the
/// dispatcher fall back to a synthetic <c>dispatcher-&lt;id&gt;</c>
/// identifier — and that fallback is exclusively for back-compat
/// against the pre-column rows.
/// </para>
/// </remarks>
internal sealed class InboundUpdateDispatcher : BackgroundService
{
    public const string ConfigurationKey = "InboundProcessing:Concurrency";
    public const int DefaultConcurrency = 4;

    private readonly InboundUpdateChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly int _concurrency;
    private readonly ILogger<InboundUpdateDispatcher> _logger;

    public InboundUpdateDispatcher(
        InboundUpdateChannel channel,
        IServiceScopeFactory scopeFactory,
        ILogger<InboundUpdateDispatcher> logger,
        int concurrency = DefaultConcurrency)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (concurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency), concurrency, "must be positive.");
        }
        _concurrency = concurrency;
    }

    /// <summary>
    /// Factory used by the Worker bootstrap to build a dispatcher whose
    /// concurrency is bound to <see cref="IConfiguration"/> at
    /// <see cref="ConfigurationKey"/>. Encapsulated as a helper so the
    /// host wiring and the tests share a single contract for
    /// configuration parsing (negative / zero values fall back to
    /// <see cref="DefaultConcurrency"/> rather than throwing — invalid
    /// burst-capacity config should NOT crash the worker on boot).
    /// </summary>
    public static InboundUpdateDispatcher CreateFromConfiguration(
        IServiceProvider services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read the raw string ourselves so we degrade gracefully for
        // non-integer values rather than throwing the
        // InvalidOperationException ConfigurationBinder.GetValue<int?>
        // raises on "not-a-number". The advertised contract is "invalid
        // concurrency config falls back to the default", not
        // "misconfiguration crashes the worker on boot".
        var raw = configuration[ConfigurationKey];
        var concurrency = DefaultConcurrency;
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            concurrency = parsed;
        }

        return new InboundUpdateDispatcher(
            services.GetRequiredService<InboundUpdateChannel>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ILogger<InboundUpdateDispatcher>>(),
            concurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "InboundUpdateDispatcher started with Concurrency={Concurrency}", _concurrency);

        var workers = new Task[_concurrency];
        for (var i = 0; i < _concurrency; i++)
        {
            workers[i] = RunWorkerAsync(i, stoppingToken);
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int index, CancellationToken ct)
    {
        try
        {
            await foreach (var updateId in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessOneAsync(updateId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "InboundUpdateDispatcher worker {Index} cancelled.", index);
        }
    }

    private async Task ProcessOneAsync(long updateId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
            var processor = scope.ServiceProvider.GetRequiredService<InboundUpdateProcessor>();

            // Re-read the row inside the fresh scope so we pick up any
            // status mutations made between enqueue and dequeue (e.g. a
            // recovery sweep that already advanced the row).
            var row = await store.GetByUpdateIdAsync(updateId, ct).ConfigureAwait(false);
            if (row is null)
            {
                _logger.LogWarning(
                    "InboundUpdate not found at dequeue; skipping. UpdateId={UpdateId}", updateId);
                return;
            }

            if (row.IdempotencyStatus == IdempotencyStatus.Completed)
            {
                _logger.LogDebug(
                    "InboundUpdate already Completed at dequeue; skipping. UpdateId={UpdateId}", updateId);
                return;
            }

            var correlationId = ResolveCorrelationId(row);
            await processor.ProcessAsync(row, correlationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "InboundUpdateDispatcher caught exception while processing UpdateId={UpdateId}; sweep will retry.",
                updateId);
        }
    }

    /// <summary>
    /// Returns the persisted <see cref="InboundUpdate.CorrelationId"/>
    /// when present, otherwise a synthetic <c>dispatcher-&lt;id&gt;</c>
    /// trace id keyed on the Telegram update id so back-compat rows
    /// (persisted before the column existed) still flow through
    /// <see cref="InboundUpdateProcessor.ProcessAsync"/> (which rejects
    /// blank correlation ids).
    /// </summary>
    internal static string ResolveCorrelationId(InboundUpdate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !string.IsNullOrWhiteSpace(row.CorrelationId)
            ? row.CorrelationId!
            : "dispatcher-" + row.UpdateId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
