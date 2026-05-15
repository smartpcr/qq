namespace AgentSwarm.Messaging.Telegram.Polling
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Telegram.Bot;
    using Telegram.Bot.Exceptions;
    using Telegram.Bot.Types;

    /// <summary>
    /// Hosted service that drives Telegram long polling for development environments.
    ///
    /// Production deployments use webhook delivery (see story MSG-TG-001); this receiver
    /// exists so that a developer can run the gateway against a real bot without exposing
    /// a public HTTPS endpoint.
    ///
    /// Failure handling:
    ///   * HTTP 401 / 403 -- treated as terminal. The <see cref="ITelegramBotClient"/> is
    ///     registered as a singleton, so a rotated token cannot be picked up at runtime
    ///     without restarting the process. Continuing to retry would burn ~720 calls/hour,
    ///     flood the logs, and risk an IP-level rate limit from Telegram. The service
    ///     therefore stops the polling loop, flips its health state to
    ///     <see cref="TelegramPollingHealthState.UnauthorizedTerminal"/>, and emits a
    ///     Critical log so the operator restarts after rotating the secret.
    ///   * Other failures -- exponential backoff capped at <c>MaxBackoffSeconds</c>
    ///     (default 5 minutes). Each successful poll resets the backoff to its initial
    ///     value so a brief Telegram outage does not penalise the next hour of traffic.
    /// </summary>
    public sealed class TelegramPollingService : BackgroundService, ITelegramPollingHealth
    {
        private const int UnauthorizedStatusCode = 401;
        private const int ForbiddenStatusCode = 403;

        private readonly ITelegramBotClient botClient;
        private readonly ITelegramUpdateHandler updateHandler;
        private readonly TelegramPollingOptions options;
        private readonly ILogger<TelegramPollingService> logger;

        private int currentBackoffSeconds;
        private volatile TelegramPollingHealthState healthState = TelegramPollingHealthState.Starting;
        private string? lastFailureReason;

        public TelegramPollingService(
            ITelegramBotClient botClient,
            ITelegramUpdateHandler updateHandler,
            IOptions<TelegramPollingOptions> options,
            ILogger<TelegramPollingService> logger)
        {
            this.botClient = botClient ?? throw new ArgumentNullException(nameof(botClient));
            this.updateHandler = updateHandler ?? throw new ArgumentNullException(nameof(updateHandler));
            this.options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.currentBackoffSeconds = Math.Max(1, this.options.InitialBackoffSeconds);
        }

        public TelegramPollingHealthState HealthState => this.healthState;

        public string? LastFailureReason => this.lastFailureReason;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            this.logger.LogInformation(
                "Starting Telegram long-polling receiver. Timeout={TimeoutSeconds}s, BatchSize={BatchSize}, InitialBackoff={InitialBackoff}s, MaxBackoff={MaxBackoff}s",
                this.options.LongPollingTimeoutSeconds,
                this.options.BatchSize,
                this.options.InitialBackoffSeconds,
                this.options.MaxBackoffSeconds);

            this.healthState = TelegramPollingHealthState.Healthy;
            int offset = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Update[] updates = await this.botClient.GetUpdatesAsync(
                        offset: offset,
                        limit: this.options.BatchSize,
                        timeout: this.options.LongPollingTimeoutSeconds,
                        allowedUpdates: this.options.AllowedUpdates,
                        cancellationToken: stoppingToken).ConfigureAwait(false);

                    foreach (Update update in updates)
                    {
                        await this.updateHandler.HandleUpdateAsync(this.botClient, update, stoppingToken).ConfigureAwait(false);
                        offset = update.Id + 1;
                    }

                    this.currentBackoffSeconds = Math.Max(1, this.options.InitialBackoffSeconds);
                    this.lastFailureReason = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ApiRequestException ex) when (IsTerminalAuthFailure(ex))
                {
                    this.lastFailureReason =
                        $"Telegram rejected the bot token (HTTP {ex.ErrorCode}). " +
                        "Rotate the token in the configured secret store and restart the process; " +
                        "the singleton ITelegramBotClient cannot pick up a new token at runtime.";
                    this.healthState = TelegramPollingHealthState.UnauthorizedTerminal;

                    this.logger.LogCritical(
                        ex,
                        "Telegram long-polling receiver is shutting down: bot token rejected (HTTP {StatusCode}). " +
                        "Rotate the token in the configured secret store and restart the process; " +
                        "the singleton ITelegramBotClient cannot pick up a new token at runtime.",
                        ex.ErrorCode);

                    return;
                }
                catch (Exception ex)
                {
                    int delaySeconds = this.currentBackoffSeconds;
                    this.lastFailureReason = $"Transient polling failure ({ex.GetType().Name}): {ex.Message}";
                    this.logger.LogError(
                        ex,
                        "Transient failure during Telegram long-poll. Backing off for {DelaySeconds}s before retrying.",
                        delaySeconds);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    int cap = Math.Max(this.options.InitialBackoffSeconds, this.options.MaxBackoffSeconds);
                    long next = (long)delaySeconds * 2L;
                    this.currentBackoffSeconds = (int)Math.Min(cap, Math.Max(delaySeconds, next));
                }
            }

            if (this.healthState == TelegramPollingHealthState.Healthy)
            {
                this.healthState = TelegramPollingHealthState.Stopped;
            }

            this.logger.LogInformation(
                "Telegram long-polling receiver stopped. FinalState={State}",
                this.healthState);
        }

        private static bool IsTerminalAuthFailure(ApiRequestException ex)
        {
            return ex.ErrorCode == UnauthorizedStatusCode || ex.ErrorCode == ForbiddenStatusCode;
        }
    }
}
