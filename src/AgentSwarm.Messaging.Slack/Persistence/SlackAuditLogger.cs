// -----------------------------------------------------------------------
// <copyright file="SlackAuditLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Stage 7.1 production <see cref="ISlackAuditLogger"/> backed by EF
/// Core. Persists <see cref="SlackAuditEntry"/> rows through
/// <typeparamref name="TContext"/> (typically the upstream
/// <c>MessagingDbContext</c> or the Slack-owned
/// <see cref="SlackPersistenceDbContext"/>) and translates
/// <see cref="SlackAuditQuery"/> filters into LINQ predicates against
/// the <c>slack_audit_entry</c> table.
/// </summary>
/// <typeparam name="TContext">
/// EF Core context implementing <see cref="ISlackAuditEntryDbContext"/>.
/// Must be registered as <c>Scoped</c> via
/// <c>AddDbContext&lt;TContext&gt;</c> by the composition root.
/// </typeparam>
/// <remarks>
/// <para>
/// The logger is safe to register as a <em>singleton</em>: every
/// operation opens a fresh DI scope so the per-request
/// <typeparamref name="TContext"/> lifetime is respected without
/// becoming a captive dependency.
/// </para>
/// <para>
/// <b>Dual interface.</b> The class implements both
/// <see cref="ISlackAuditLogger"/> (the Stage 7.1 surface introduced by
/// architecture.md §4.6) and <see cref="ISlackAuditEntryWriter"/> (the
/// Stage 3.1 append seam). All existing call sites
/// (<see cref="Security.SlackAuditEntrySignatureSink"/>,
/// <see cref="Security.SlackAuditEntryAuthorizationSink"/>,
/// <see cref="Pipeline.SlackInboundAuditRecorder"/>,
/// <see cref="Transport.SlackModalAuditRecorder"/>,
/// <see cref="Pipeline.SlackOutboundDispatcher"/>,
/// <see cref="Pipeline.SlackThreadManager"/>, and
/// <see cref="Transport.SlackDirectApiClient"/>) already depend on
/// <see cref="ISlackAuditEntryWriter"/>, so wiring the logger as that
/// interface in DI automatically routes signature, authorization,
/// idempotency, command dispatch, interaction handling, outbound send,
/// modal open, thread lifecycle, and views.open audit writes through
/// <see cref="LogAsync"/>.
/// </para>
/// <para>
/// <b>Failure handling.</b> Persistence exceptions propagate to the
/// caller (which is responsible for swallowing + logging per its own
/// "audit never breaks the response" policy). Cancellation tokens are
/// honoured: <see cref="OperationCanceledException"/> bubbles up
/// unchanged so the cooperative-cancellation contract is preserved.
/// </para>
/// </remarks>
public sealed class SlackAuditLogger<TContext> : ISlackAuditLogger, ISlackAuditEntryWriter
    where TContext : DbContext, ISlackAuditEntryDbContext
{
    /// <summary>
    /// Stable substring of the EF Core SQL Server provider name. Used
    /// by <see cref="QueryAsync"/> to choose between
    /// <c>SELECT TOP(N) ...</c> (SQL Server) and
    /// <c>SELECT ... LIMIT N</c> (SQLite / PostgreSQL / MySQL) when
    /// emitting the row-cap clause. The same predicate gates the
    /// matching choice inside
    /// <see cref="SlackRetentionCleanupService{TContext}"/>.
    /// </summary>
    internal const string SqlServerProviderMarker = "SqlServer";

    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>
    /// Creates a logger that resolves <typeparamref name="TContext"/>
    /// from a fresh DI scope per call.
    /// </summary>
    /// <param name="scopeFactory">DI scope factory.</param>
    public SlackAuditLogger(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task LogAsync(SlackAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();
        context.SlackAuditEntries.Add(entry);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwards to <see cref="LogAsync"/> so a single instance can be
    /// registered against both <see cref="ISlackAuditEntryWriter"/> and
    /// <see cref="ISlackAuditLogger"/>. Keeps the existing
    /// append-only seam working while the broader query-capable
    /// surface lights up.
    /// </remarks>
    Task ISlackAuditEntryWriter.AppendAsync(SlackAuditEntry entry, CancellationToken ct)
        => this.LogAsync(entry, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SlackAuditEntry>> QueryAsync(
        SlackAuditQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Stage 7.1 evaluator iter-1 item 4: push EVERY filter
        // (string-equality, timestamp range), ordering, and limit to
        // the server so a "give me the most recent N rows in this
        // hour" query touches only the rows the
        // IX_SlackAuditEntry_Timestamp index covers, not the whole
        // audit table.
        //
        // Strategy. Build a parameterised raw SQL statement and
        // execute it via DbSet.FromSqlRaw + ToListAsync with NO
        // further LINQ composition. EF Core executes the SQL
        // verbatim in this shape -- if we composed an OrderBy or
        // Take on top, EF would wrap the FromSqlRaw output in a
        // subquery, and the SQLite provider would then attempt to
        // translate the LINQ DateTimeOffset comparison client-side
        // (see https://learn.microsoft.com/ef/core/providers/sqlite/limitations),
        // defeating the server-side pushdown.
        //
        // Provider differences:
        //   * SQL Server: row cap emitted as TOP(N) immediately
        //     after SELECT.
        //   * SQLite / PostgreSQL / MySQL: row cap emitted as
        //     LIMIT N at the tail.
        // DateTimeOffset bind: Microsoft.Data.Sqlite serialises
        // DateTimeOffset to ISO-8601 TEXT in a lexicographically-
        // sortable form ("yyyy-MM-dd HH:mm:ss.fffffff zzz") so
        // server-side WHERE / ORDER BY comparisons against a
        // UTC-normalised parameter match SQL Server's native
        // datetimeoffset semantics. Production hosts that mix offsets
        // should normalise to UTC at the call site.
        bool isSqlServer = this.IsSqlServer(context);
        int? limit = (query.Limit is int n && n > 0) ? n : null;
        List<object> parameters = new();
        StringBuilder sql = new();

        sql.Append("SELECT ");
        if (isSqlServer && limit is int sqlServerLimit)
        {
            sql.Append("TOP(")
                .Append(sqlServerLimit.ToString(CultureInfo.InvariantCulture))
                .Append(") ");
        }

        sql.Append("* FROM ")
            .Append(SlackAuditEntryConfiguration.TableName)
            .Append(" WHERE 1 = 1");

        AppendStringFilter(sql, parameters, "correlation_id", query.CorrelationId);
        AppendStringFilter(sql, parameters, "task_id", query.TaskId);
        AppendStringFilter(sql, parameters, "agent_id", query.AgentId);
        AppendStringFilter(sql, parameters, "team_id", query.TeamId);
        AppendStringFilter(sql, parameters, "channel_id", query.ChannelId);
        AppendStringFilter(sql, parameters, "user_id", query.UserId);
        AppendStringFilter(sql, parameters, "direction", query.Direction);
        AppendStringFilter(sql, parameters, "outcome", query.Outcome);

        if (query.FromTimestamp is { } from)
        {
            sql.Append(" AND timestamp >= {")
                .Append(parameters.Count.ToString(CultureInfo.InvariantCulture))
                .Append('}');
            parameters.Add(from);
        }

        if (query.ToTimestamp is { } to)
        {
            sql.Append(" AND timestamp <= {")
                .Append(parameters.Count.ToString(CultureInfo.InvariantCulture))
                .Append('}');
            parameters.Add(to);
        }

        sql.Append(" ORDER BY timestamp, id");

        if (!isSqlServer && limit is int sqliteLimit)
        {
            sql.Append(" LIMIT ")
                .Append(sqliteLimit.ToString(CultureInfo.InvariantCulture));
        }

        List<SlackAuditEntry> rows = await context.SlackAuditEntries
            .FromSqlRaw(sql.ToString(), parameters.ToArray())
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="context"/>
    /// is backed by the SQL Server provider. Used to choose between
    /// <c>TOP(N)</c> (SQL Server) and <c>LIMIT N</c> (SQLite /
    /// PostgreSQL / MySQL) when emitting the row cap clause.
    /// </summary>
    internal bool IsSqlServer(TContext context)
    {
        string? providerName = context.Database.ProviderName;
        return providerName is not null
            && providerName.IndexOf(SqlServerProviderMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Appends a parameterised <c>AND column = {n}</c> WHERE fragment
    /// when <paramref name="value"/> is non-empty. The column name is
    /// inlined as a literal (parameters can only appear in value
    /// positions, not identifier positions); each
    /// <see cref="SlackAuditQuery"/> filter column is a fixed
    /// snake_case identifier owned by
    /// <see cref="SlackAuditEntryConfiguration"/> so there is no
    /// SQL-injection surface.
    /// </summary>
    private static void AppendStringFilter(
        StringBuilder sql,
        List<object> parameters,
        string column,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        sql.Append(" AND ")
            .Append(column)
            .Append(" = {")
            .Append(parameters.Count.ToString(CultureInfo.InvariantCulture))
            .Append('}');
        parameters.Add(value);
    }
}
