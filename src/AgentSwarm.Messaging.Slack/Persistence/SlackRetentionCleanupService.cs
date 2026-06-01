// -----------------------------------------------------------------------
// <copyright file="SlackRetentionCleanupService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Stage 7.1 background cleanup that purges
/// <see cref="SlackAuditEntry"/> rows older than
/// <see cref="SlackRetentionOptions.RetentionDays"/> from the audit
/// table and the matching <see cref="SlackInboundRequestRecord"/>
/// rows from the idempotency ledger. Implements tech-spec.md §2.7 /
/// resolved decision OQ-2 (30-day retention, default daily cadence).
/// </summary>
/// <typeparam name="TContext">
/// EF Core context implementing BOTH
/// <see cref="ISlackAuditEntryDbContext"/> and
/// <see cref="ISlackInboundRequestRecordDbContext"/> so a single
/// sweep can purge both tables. The upstream
/// <see cref="SlackPersistenceDbContext"/> satisfies the constraint.
/// </typeparam>
/// <remarks>
/// <para>
/// Implementation strategy. Each sweep issues one parameterised
/// <c>DELETE</c> per table via
/// <see cref="RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(DatabaseFacade, FormattableString, CancellationToken)"/>.
/// Raw SQL is used because the EF Core SQLite provider cannot
/// translate <see cref="DateTimeOffset"/> WHERE/ORDER BY predicates
/// (see https://learn.microsoft.com/ef/core/providers/sqlite/limitations),
/// and parameter binding for the cutoff value goes through the same
/// provider converter as the column write path, keeping comparison
/// semantics correct on SQLite (TEXT ISO-8601) and SQL Server
/// (native <c>datetimeoffset</c>) alike. Table and column names are
/// pinned to the canonical snake_case identifiers set by
/// <see cref="SlackAuditEntryConfiguration"/> and
/// <see cref="SlackInboundRequestRecordConfiguration"/>.
/// </para>
/// <para>
/// Failures inside the sweep are logged at <c>Error</c> and never
/// crash the host: the next tick attempts again. Cancellation
/// (host shutdown) breaks out of the loop cleanly.
/// </para>
/// <para>
/// Tests drive cleanup directly via
/// <see cref="RunOnceAsync(DateTimeOffset, CancellationToken)"/> so
/// the time-based sweep schedule is observable without spinning the
/// BackgroundService loop.
/// </para>
/// </remarks>
public sealed class SlackRetentionCleanupService<TContext> : BackgroundService
    where TContext : DbContext, ISlackAuditEntryDbContext, ISlackInboundRequestRecordDbContext
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptionsMonitor<SlackRetentionOptions> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SlackRetentionCleanupService<TContext>> logger;

    /// <summary>Creates the cleanup service.</summary>
    public SlackRetentionCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SlackRetentionOptions> options,
        ILogger<SlackRetentionCleanupService<TContext>> logger)
        : this(scopeFactory, options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-visible constructor that lets the schema-validation
    /// fixtures inject a virtual <see cref="TimeProvider"/>.
    /// </summary>
    internal SlackRetentionCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SlackRetentionOptions> options,
        ILogger<SlackRetentionCleanupService<TContext>> logger,
        TimeProvider timeProvider)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SlackRetentionOptions snapshot = this.options.CurrentValue;
        if (!snapshot.Enabled)
        {
            this.logger.LogInformation(
                "SlackRetentionCleanupService disabled via {Section}.Enabled=false; cleanup loop will not run.",
                SlackRetentionOptions.SectionName);
            return;
        }

        this.logger.LogInformation(
            "SlackRetentionCleanupService starting (retention={RetentionDays}d, interval={Interval}, initialDelay={InitialDelay}).",
            snapshot.RetentionDays,
            snapshot.SweepInterval,
            snapshot.InitialDelay);

        try
        {
            if (snapshot.InitialDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(snapshot.InitialDelay, this.timeProvider, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                SlackRetentionOptions current = this.options.CurrentValue;
                if (!current.Enabled)
                {
                    this.logger.LogInformation(
                        "SlackRetentionCleanupService observed Enabled=false; exiting loop.");
                    return;
                }

                try
                {
                    DateTimeOffset now = this.timeProvider.GetUtcNow();
                    SlackRetentionSweepResult result = await this.RunOnceAsync(now, stoppingToken)
                        .ConfigureAwait(false);
                    this.logger.LogInformation(
                        "SlackRetentionCleanupService sweep complete: purged {AuditDeleted} audit row(s) and {InboundDeleted} idempotency row(s) older than {Cutoff:o}.",
                        result.AuditEntriesDeleted,
                        result.InboundRequestsDeleted,
                        result.Cutoff);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    this.logger.LogError(
                        ex,
                        "SlackRetentionCleanupService sweep failed; will retry at next interval.");
                }

                TimeSpan interval = current.SweepInterval > TimeSpan.Zero
                    ? current.SweepInterval
                    : SlackRetentionOptions.DefaultSweepInterval;

                try
                {
                    await Task.Delay(interval, this.timeProvider, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            this.logger.LogInformation("SlackRetentionCleanupService stopping.");
        }
    }

    /// <summary>
    /// Runs a single purge sweep and returns the count of rows
    /// deleted from each table. Exposed so tests can invoke the
    /// purge deterministically without driving the background loop.
    /// </summary>
    /// <param name="now">
    /// Sweep reference time. The cutoff is computed as
    /// <c>now - RetentionDays</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Counts of deleted rows per table.</returns>
    public async Task<SlackRetentionSweepResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        SlackRetentionOptions snapshot = this.options.CurrentValue;
        int retentionDays = snapshot.RetentionDays > 0
            ? snapshot.RetentionDays
            : SlackRetentionOptions.DefaultRetentionDays;
        int batchSize = snapshot.BatchSize > 0
            ? snapshot.BatchSize
            : SlackRetentionOptions.DefaultBatchSize;
        DateTimeOffset cutoff = now - TimeSpan.FromDays(retentionDays);

        int auditDeleted = await this.PurgeAuditEntriesAsync(cutoff, batchSize, ct).ConfigureAwait(false);
        int inboundDeleted = await this.PurgeInboundRequestsAsync(cutoff, batchSize, ct).ConfigureAwait(false);

        return new SlackRetentionSweepResult(cutoff, auditDeleted, inboundDeleted);
    }

    private async Task<int> PurgeAuditEntriesAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        return await this.PurgeInBatchesAsync(
            tableName: SlackAuditEntryConfiguration.TableName,
            keyColumn: "id",
            timestampColumn: "timestamp",
            cutoff: cutoff,
            batchSize: batchSize,
            ct: ct).ConfigureAwait(false);
    }

    private async Task<int> PurgeInboundRequestsAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        return await this.PurgeInBatchesAsync(
            tableName: SlackInboundRequestRecordConfiguration.TableName,
            keyColumn: "idempotency_key",
            timestampColumn: "first_seen_at",
            cutoff: cutoff,
            batchSize: batchSize,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains a table of rows whose <paramref name="timestampColumn"/>
    /// is older than <paramref name="cutoff"/>, in batches of
    /// <paramref name="batchSize"/>. Uses provider-specific subquery
    /// SQL so the batch limit is enforced server-side on both SQLite
    /// (which honours <c>LIMIT</c> inside a sub-select) and SQL
    /// Server (which uses <c>TOP (@n)</c>). Each batch runs in its
    /// own DI scope so a long sweep does not pin a single DbContext
    /// across the whole run.
    /// </summary>
    private async Task<int> PurgeInBatchesAsync(
        string tableName,
        string keyColumn,
        string timestampColumn,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        int total = 0;

        while (!ct.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
            TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

            string providerName = context.Database.ProviderName ?? string.Empty;
            string sql = BuildBatchDeleteSql(
                providerName,
                tableName,
                keyColumn,
                timestampColumn);

            int rows = await context.Database
                .ExecuteSqlRawAsync(sql, new object[] { cutoff, batchSize }, ct)
                .ConfigureAwait(false);

            if (rows == 0)
            {
                break;
            }

            total += rows;

            if (rows < batchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Composes a provider-portable batched DELETE. Uses a sub-select
    /// + <c>LIMIT</c> on SQLite / PostgreSQL and a CTE with
    /// <c>TOP (@n)</c> on SQL Server so the batch ceiling is enforced
    /// server-side. The cutoff timestamp is parameterised as
    /// <c>{0}</c> and the batch size as <c>{1}</c> so EF Core's
    /// provider binding handles the type-mapping (TEXT on SQLite,
    /// native <c>datetimeoffset</c> on SQL Server).
    /// </summary>
    private static string BuildBatchDeleteSql(
        string providerName,
        string tableName,
        string keyColumn,
        string timestampColumn)
    {
        bool isSqlServer = providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);

        if (isSqlServer)
        {
            // SQL Server: DELETE TOP (N) preserves the batch ceiling
            // without needing a sub-select; the IX_*_Timestamp /
            // IX_SlackInboundRequestRecord_FirstSeenAt index covers
            // the predicate so the operation is index-bound.
            return $"DELETE TOP ({{1}}) FROM {tableName} WHERE {timestampColumn} < {{0}}";
        }

        // SQLite (and most other relational providers): sub-select
        // with LIMIT keeps the batch ceiling enforced server-side. We
        // cannot use SQLite's "DELETE ... LIMIT" extension because it
        // is not compiled into Microsoft.Data.Sqlite by default; the
        // sub-select form works on every SQLite build.
        return $"DELETE FROM {tableName} WHERE {keyColumn} IN ("
            + $"SELECT {keyColumn} FROM {tableName} WHERE {timestampColumn} < {{0}} LIMIT {{1}})";
    }
}

/// <summary>
/// Aggregate result for a single
/// <see cref="SlackRetentionCleanupService{TContext}.RunOnceAsync(DateTimeOffset, CancellationToken)"/>
/// invocation.
/// </summary>
/// <param name="Cutoff">Inclusive cutoff timestamp; rows older were purged.</param>
/// <param name="AuditEntriesDeleted">Rows deleted from <c>slack_audit_entry</c>.</param>
/// <param name="InboundRequestsDeleted">Rows deleted from <c>slack_inbound_request_record</c>.</param>
public readonly record struct SlackRetentionSweepResult(
    DateTimeOffset Cutoff,
    int AuditEntriesDeleted,
    int InboundRequestsDeleted);
