// -----------------------------------------------------------------------
// <copyright file="RotatingTelegramBotClient.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 5.1 — singleton <see cref="ITelegramBotClient"/> proxy that
/// rebuilds its inner <see cref="TelegramBotClient"/> when the
/// <see cref="TelegramOptions.BotToken"/> changes via
/// <see cref="IOptionsMonitor{T}.OnChange"/>. Lets a Key Vault secret
/// rotation propagate to the very next outbound API call without
/// requiring a process restart — the contract architecture.md §10
/// line 1018 and §11 line 1091 require.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a proxy is required.</b> Before this type existed the
/// container registered a singleton <c>ITelegramBotClient</c> via a
/// factory delegate; the delegate ran exactly once at first
/// resolution, captured the then-current token into a
/// <see cref="TelegramBotClient"/> instance, and stored that instance
/// in the singleton slot. Every downstream consumer
/// (<c>TelegramMessageSender</c>, <c>CallbackQueryHandler</c>,
/// <c>QuestionTimeoutService</c>, <c>TelegramWebhookRegistrationService</c>,
/// <c>TelegramBotClientUpdatePoller</c>) injects that singleton into
/// its own constructor and caches the reference. A subsequent vault
/// rotation that updated <c>IOptionsMonitor&lt;TelegramOptions&gt;.CurrentValue.BotToken</c>
/// would never reach those cached references, so the connector would
/// keep using the old token until the worker process was restarted.
/// The proxy fixes that by sitting between consumers and the real
/// client: consumers hold a stable reference to the proxy, but every
/// method call is forwarded to a freshly-rebuilt inner client whose
/// constructor captured the current token.
/// </para>
/// <para>
/// <b>How rotation is observed.</b> The proxy subscribes to
/// <see cref="IOptionsMonitor{TOptions}.OnChange"/> in its
/// constructor. The Azure Key Vault configuration provider fires
/// <c>IConfiguration</c>'s change token on every successful refresh
/// (the <see cref="Azure.Extensions.AspNetCore.Configuration.Secrets.AzureKeyVaultConfigurationOptions.ReloadInterval"/>
/// pin in Program.cs is what makes those refreshes happen). The
/// configuration change token re-binds <see cref="TelegramOptions"/>,
/// which triggers the OnChange callback. The callback compares the
/// new <c>BotToken</c> to the cached one; when it differs the inner
/// client is dropped, a new one is constructed lazily on the next
/// call, and the old <see cref="HttpClient"/> reference (which is
/// pooled by <see cref="IHttpClientFactory"/>) remains untouched.
/// </para>
/// <para>
/// <b>Thread safety.</b> The inner-client slot is guarded by
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> on writes and a
/// volatile read on the fast path so concurrent calls during a token
/// rotation never observe a torn assignment. Two callers can briefly
/// race to construct an inner client; only one wins via the
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> publish,
/// and the loser's <see cref="TelegramBotClient"/> is discarded
/// (constructing a <see cref="TelegramBotClient"/> is cheap — it
/// just stores the token and resolves the named HttpClient).
/// </para>
/// <para>
/// <b>Property forwarding semantics.</b> <see cref="Timeout"/> and
/// <see cref="ExceptionsParser"/> are settable on the underlying
/// client. The proxy forwards each get/set to the currently-active
/// inner instance. A rotation discards prior overrides — the
/// expectation is that consumers configure these once at startup
/// (the production code does not currently mutate them), and a
/// future need to preserve them across rotations should be solved
/// by storing the override on the proxy and re-applying on rebuild.
/// </para>
/// <para>
/// <b>Event forwarding semantics.</b> <see cref="OnMakingApiRequest"/>
/// and <see cref="OnApiResponseReceived"/> are multicast events on the
/// underlying client used by instrumentation / diagnostics callers.
/// Because a rotation swaps the inner client out, naively forwarding
/// <c>add</c>/<c>remove</c> straight to <see cref="Current"/> would
/// strand handlers on the discarded instance and silently stop firing
/// after the first rotation. The proxy therefore tracks the aggregated
/// multicast delegate for each event on its own field, attaches the
/// new handler to the current inner client on <c>add</c> (so the very
/// next API call observes it), and re-attaches the full aggregated
/// delegate to every freshly-built inner client in <see cref="Rebuild"/>.
/// The discarded inner is detached defensively so it doesn't keep the
/// handler references alive past its own GC eligibility. All mutation
/// of the aggregated delegates happens under <see cref="_rebuildGate"/>
/// so a rotation cannot race a concurrent <c>add</c>/<c>remove</c>.
/// </para>
/// </remarks>
public sealed class RotatingTelegramBotClient : ITelegramBotClient, IDisposable
{
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<RotatingTelegramBotClient> _logger;
    private readonly IDisposable? _changeSubscription;
    private readonly object _rebuildGate = new();

    private TelegramBotClient? _inner;
    private string? _currentToken;
    private bool _disposed;

    // Aggregated multicast delegates for forwarded events. Tracked on
    // the proxy so they survive an inner-client rotation; re-attached
    // to every freshly-built inner client in Rebuild(). All mutation
    // is gated by _rebuildGate.
    private AsyncEventHandler<ApiRequestEventArgs>? _onMakingApiRequest;
    private AsyncEventHandler<ApiResponseEventArgs>? _onApiResponseReceived;

    public RotatingTelegramBotClient(
        IOptionsMonitor<TelegramOptions> options,
        ILogger<RotatingTelegramBotClient> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory;

        // Subscribe BEFORE the first lazy build so a refresh that
        // arrives between construction and first use still wins.
        _changeSubscription = _options.OnChange(OnOptionsChanged);
    }

    /// <inheritdoc/>
    public long BotId => Current.BotId;

    /// <inheritdoc/>
    public bool LocalBotServer => Current.LocalBotServer;

    /// <inheritdoc/>
    public TimeSpan Timeout
    {
        get => Current.Timeout;
        set => Current.Timeout = value;
    }

    /// <inheritdoc/>
    public IExceptionParser ExceptionsParser
    {
        get => Current.ExceptionsParser;
        set => Current.ExceptionsParser = value;
    }

    /// <inheritdoc/>
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_rebuildGate)
            {
                _onMakingApiRequest += value;
                var inner = Volatile.Read(ref _inner);
                if (inner is not null)
                {
                    inner.OnMakingApiRequest += value;
                }
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_rebuildGate)
            {
                _onMakingApiRequest -= value;
                var inner = Volatile.Read(ref _inner);
                if (inner is not null)
                {
                    inner.OnMakingApiRequest -= value;
                }
            }
        }
    }

    /// <inheritdoc/>
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_rebuildGate)
            {
                _onApiResponseReceived += value;
                var inner = Volatile.Read(ref _inner);
                if (inner is not null)
                {
                    inner.OnApiResponseReceived += value;
                }
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_rebuildGate)
            {
                _onApiResponseReceived -= value;
                var inner = Volatile.Read(ref _inner);
                if (inner is not null)
                {
                    inner.OnApiResponseReceived -= value;
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task<TResponse> SendRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => Current.SendRequest(request, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> TestApi(CancellationToken cancellationToken = default)
        => Current.TestApi(cancellationToken);

    /// <inheritdoc/>
    public Task DownloadFile(
        string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Current.DownloadFile(filePath, destination, cancellationToken);

    /// <inheritdoc/>
    public Task DownloadFile(
        TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => Current.DownloadFile(file, destination, cancellationToken);

    /// <summary>
    /// Test hook: returns the active inner client's token without
    /// exposing the field directly. Production code MUST NOT call
    /// this; it exists so integration tests can assert that rotation
    /// actually swapped the inner instance.
    /// </summary>
    internal string? CurrentTokenForTesting => _currentToken;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changeSubscription?.Dispose();
    }

    private TelegramBotClient Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _inner);
            if (snapshot is not null)
            {
                return snapshot;
            }

            return Rebuild(_options.CurrentValue);
        }
    }

    private TelegramBotClient Rebuild(TelegramOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            throw new InvalidOperationException(
                "TelegramOptions.BotToken is not configured. Refusing to create Telegram bot client.");
        }

        lock (_rebuildGate)
        {
            var existing = Volatile.Read(ref _inner);
            if (existing is not null && string.Equals(_currentToken, opts.BotToken, StringComparison.Ordinal))
            {
                return existing;
            }

            var httpClient = _httpClientFactory?.CreateClient(TelegramBotClientFactory.HttpClientName);
            var fresh = new TelegramBotClient(opts.BotToken, httpClient);

            // Re-attach any handlers subscribed on the proxy so they
            // continue to fire after a rotation. Without this the
            // discarded `existing` instance below would silently swallow
            // every future invocation — see "Event forwarding semantics"
            // in the type remarks.
            var requestHandlers = _onMakingApiRequest;
            if (requestHandlers is not null)
            {
                fresh.OnMakingApiRequest += requestHandlers;
            }

            var responseHandlers = _onApiResponseReceived;
            if (responseHandlers is not null)
            {
                fresh.OnApiResponseReceived += responseHandlers;
            }

            if (existing is not null)
            {
                // Detach defensively so the discarded inner client does
                // not keep handler references alive past its own GC
                // eligibility. Safe even if the same handler is now
                // attached to `fresh` — the delegates are reference-
                // equal and event accessors use Delegate.Remove.
                if (requestHandlers is not null)
                {
                    existing.OnMakingApiRequest -= requestHandlers;
                }

                if (responseHandlers is not null)
                {
                    existing.OnApiResponseReceived -= responseHandlers;
                }
            }

            Volatile.Write(ref _inner, fresh);
            _currentToken = opts.BotToken;
            return fresh;
        }
    }

    private void OnOptionsChanged(TelegramOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            // Don't tear down the existing client on a transient blank —
            // the validator at startup rejects blank tokens, and a
            // Key Vault refresh that returns blank is a vault outage,
            // not a rotation. Log and continue using the cached client.
            _logger.LogWarning(
                "IOptionsMonitor<TelegramOptions> fired with a blank BotToken; keeping the currently-cached Telegram bot client to avoid an outage. Investigate the configuration source.");
            return;
        }

        if (string.Equals(_currentToken, opts.BotToken, StringComparison.Ordinal))
        {
            return;
        }

        // Rebuild eagerly so the first post-rotation call hits the new
        // client without re-entering the rebuild lock.
        _logger.LogInformation(
            "Telegram bot token rotated via configuration reload; rebuilding inner TelegramBotClient. Token value is never logged.");
        Rebuild(opts);
    }
}
