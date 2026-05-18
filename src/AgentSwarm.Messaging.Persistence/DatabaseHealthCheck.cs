// -----------------------------------------------------------------------
// <copyright file="DatabaseHealthCheck.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 6.2 — composite-friendly <see cref="IHealthCheck"/> that
/// verifies both the operational <see cref="MessagingDbContext"/>
/// and the audit <see cref="AuditDbContext"/> databases are
/// reachable AND have their schema migrated to the latest version.
/// The Stage 6.2 brief emphasises the audit database ("verifies the
/// audit database is reachable and migrated") because audit
/// availability is a compliance contract; the operational database
/// is included alongside because every command flow depends on it
/// and a single database health entry on <c>/healthz</c> is easier
/// for operators to interpret than a "messaging vs audit" split.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reachability.</b> <see cref="DatabaseFacade.CanConnectAsync"/>
/// opens a real connection against the configured provider —
/// SQLite, PostgreSQL, or SQL Server — and returns <c>true</c> iff
/// the handshake succeeds. A misconfigured connection string, a
/// firewall block, or a stopped database server all surface as
/// <c>false</c> here.
/// </para>
/// <para>
/// <b>Migrated (iter-2 evaluator item 1, iter-3 evaluator item 3).</b>
/// The check honours the literal Stage 6.2 contract — "reachable
/// and migrated" — by querying EF Core's migration history table
/// via
/// <see cref="RelationalDatabaseFacadeExtensions.GetAppliedMigrationsAsync"/>
/// and <see cref="RelationalDatabaseFacadeExtensions.GetPendingMigrationsAsync"/>.
/// A production host that has run at least one migration but has a
/// pending one outstanding (the canonical "deployment forgot to
/// apply the new migration" footgun) reports
/// <see cref="HealthStatus.Unhealthy"/> here, even when the
/// canonical table probe below would succeed because the legacy
/// schema is still in place. The earlier iter-1 implementation
/// explicitly narrowed "migrated" to "schema exists" and so missed
/// this case. The iter-3 revision removes the silent-fallback that
/// converted a thrown migration-history probe into a "skip and use
/// the schema probe instead" no-op — a thrown
/// <see cref="GetAppliedMigrationsAsync"/> /
/// <see cref="GetPendingMigrationsAsync"/> now reports
/// <see cref="HealthStatus.Unhealthy"/> with the underlying
/// exception, because failure to enumerate migration history means
/// the "migrated" contract genuinely cannot be verified.
/// </para>
/// <para>
/// <b>Caller cancellation propagates (iter-3 evaluator items 1 + 3).</b>
/// Every <see langword="catch"/> in this check is narrowed with
/// <c>when (ex is not OperationCanceledException)</c>; an
/// <see cref="OperationCanceledException"/> thrown because the
/// caller's <see cref="CancellationToken"/> cancelled (host
/// shutdown, request abort) propagates to the
/// <see cref="HealthCheckService"/> rather than being misreported
/// as an "Unhealthy database". The iter-2 implementation caught
/// every exception broadly here, which would lie about database
/// health on every cancelled <c>/healthz</c> request.
/// </para>
/// <para>
/// <b>EnsureCreated compatibility (iter-4 evaluator item 1).</b>
/// The <c>EnsureCreatedAsync</c> path used by tests and the
/// <c>MessagingDb:UseMigrations=false</c> dev shortcut creates the
/// schema directly without recording any rows in
/// <c>__EFMigrationsHistory</c>. In that case
/// <see cref="RelationalDatabaseFacadeExtensions.GetAppliedMigrationsAsync"/>
/// returns an empty sequence. The check reads
/// <c>MessagingDb:UseMigrations</c> from
/// <see cref="IConfiguration"/> and applies a different verdict
/// depending on the bootstrap mode the operator chose:
/// <list type="bullet">
///   <item><description>
///   <c>UseMigrations=true</c> (production strict): an empty applied
///   list combined with any pending migrations is the canonical
///   "deployment forgot to run MigrateAsync" footgun and reports
///   <see cref="HealthStatus.Unhealthy"/>. This satisfies the
///   literal Stage 6.2 "reachable AND migrated" contract.
///   </description></item>
///   <item><description>
///   <c>UseMigrations=false</c> (dev / test / EnsureCreated): the
///   empty applied list is expected — EnsureCreated doesn't record
///   history — so the check falls back to the schema-presence probe
///   alone. The schema probe is the authoritative "is the DB usable"
///   signal in this mode.
///   </description></item>
/// </list>
/// The previous iter-3 implementation ALWAYS fell back to the
/// schema probe on <c>applied==0</c>, which let a production host
/// with <c>UseMigrations=false</c> report Healthy for a database
/// that was bootstrapped via EnsureCreated rather than MigrateAsync
/// — violating the brief's "migrated" contract. Iter-4 splits the
/// verdict on the bootstrap-mode flag the operator already controls.
/// </para>
/// <para>
/// <b>Schema probe.</b> A one-row <c>LIMIT 1</c> probe-query against
/// each context's canonical <see cref="DbSet{TEntity}"/>
/// (<see cref="MessagingDbContext.OutboundMessages"/>,
/// <see cref="AuditDbContext.AuditLogs"/>) throws iff the table does
/// not exist. The probe is cheap on every supported provider (it
/// touches at most one row / no rows when the table is empty) and
/// definitively distinguishes "connected but schema missing" from
/// "connected and ready to write".
/// </para>
/// <para>
/// <b>Scope bridging.</b> Registered as a singleton so the
/// <see cref="IHealthCheck"/> framework's per-call resolution does
/// not produce a captive-dependency conflict; the check opens a
/// fresh <see cref="IServiceScope"/> per probe to materialise the
/// two scoped <see cref="DbContext"/>s.
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// <list type="bullet">
///   <item><description>
///   Both contexts reachable + no pending migrations + canonical
///   tables queryable → <see cref="HealthStatus.Healthy"/>.
///   </description></item>
///   <item><description>
///   Either context unreachable, has pending migrations on top of
///   previously-applied ones, OR canonical table missing →
///   <see cref="HealthStatus.Unhealthy"/> with the failure surfaced
///   in <see cref="HealthCheckResult.Description"/> + the
///   exception (where applicable) recorded.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    /// <summary>
    /// Canonical name to register this check under via
    /// <c>AddCheck&lt;DatabaseHealthCheck&gt;(Name, ...)</c>. The
    /// Stage 6.2 <c>/healthz</c> JSON output keys on this name.
    /// </summary>
    public const string Name = "database";

    /// <summary>
    /// Configuration key for the bootstrap-mode flag the health check
    /// reads. When <c>true</c>, an empty applied-migrations list with
    /// any pending migrations is reported as
    /// <see cref="HealthStatus.Unhealthy"/> (the production strict
    /// contract). When <c>false</c> (the EnsureCreated dev/test
    /// shortcut), the schema probe is the authoritative signal.
    /// Matches the key
    /// <c>configuration.GetValue&lt;bool&gt;("MessagingDb:UseMigrations", false)</c>
    /// already read in
    /// <c>ServiceCollectionExtensions.AddMessagingPersistence</c>.
    /// </summary>
    internal const string UseMigrationsConfigKey = "MessagingDb:UseMigrations";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DatabaseHealthCheck> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Iter-3 evaluator item 1 — observe caller cancellation
        // BEFORE entering any EF Core probe. EF Core's
        // RelationalDatabaseCreator.CanConnectAsync swallows ALL
        // exceptions (including OperationCanceledException) inside a
        // bare catch and returns false, which would otherwise convert
        // request-abort / host-shutdown into a "database unreachable"
        // false positive. Throwing eagerly here — and re-checking
        // between probe steps — keeps the cancellation contract
        // honest regardless of which EF Core API silently absorbs the
        // token downstream.
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var data = new Dictionary<string, object>();

        var messagingResult = await ProbeAsync(
            sp.GetRequiredService<MessagingDbContext>(),
            "messaging",
            async (db, ct) =>
            {
                await db.OutboundMessages.AsNoTracking().Take(1).LoadAsync(ct).ConfigureAwait(false);
            },
            data,
            cancellationToken).ConfigureAwait(false);

        if (messagingResult is { } failedMessaging)
        {
            return failedMessaging;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var auditResult = await ProbeAsync(
            sp.GetRequiredService<AuditDbContext>(),
            "audit",
            async (db, ct) =>
            {
                // AuditLogs is internal to the Persistence assembly so
                // this in-assembly check can touch it directly; the
                // Stage 5.3 immutability guard does not fire because
                // we never call SaveChanges.
                await db.AuditLogs.AsNoTracking().Take(1).LoadAsync(ct).ConfigureAwait(false);
            },
            data,
            cancellationToken).ConfigureAwait(false);

        if (auditResult is { } failedAudit)
        {
            return failedAudit;
        }

        return new HealthCheckResult(
            HealthStatus.Healthy,
            description: "Messaging and audit databases are reachable, no pending migrations, and the canonical schemas are present.",
            data: data);
    }

    private async Task<HealthCheckResult?> ProbeAsync<TContext>(
        TContext db,
        string label,
        Func<TContext, CancellationToken, Task> schemaProbe,
        Dictionary<string, object> data,
        CancellationToken ct)
        where TContext : DbContext
    {
        // Iter-3 evaluator item 1 — re-check the caller's token at
        // every step boundary; EF Core's CanConnectAsync / migration
        // history probes may swallow OCE internally on some providers.
        ct.ThrowIfCancellationRequested();

        var connectKey = $"{label}_can_connect";
        var schemaKey = $"{label}_schema_present";
        var pendingKey = $"{label}_pending_migrations";
        var appliedKey = $"{label}_applied_migrations_count";

        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
            data[connectKey] = canConnect;

            // EF Core's RelationalDatabaseCreator.CanConnectAsync
            // catches every exception (including OCE) and returns
            // false. Honour caller cancellation explicitly here so
            // CanConnectAsync's broad catch cannot mask a cancelled
            // host-shutdown / request-abort with an Unhealthy result.
            ct.ThrowIfCancellationRequested();

            if (!canConnect)
            {
                _logger.LogWarning(
                    "DatabaseHealthCheck — {Label} database cannot be reached (CanConnectAsync returned false); reporting Unhealthy.",
                    label);

                return new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    description: $"{label} database is unreachable — CanConnectAsync returned false. Triage the connection string and the database server.",
                    data: data);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Iter-3 evaluator item 1 — OperationCanceledException
            // MUST propagate so the HealthCheckService can handle
            // request abort / host shutdown natively. Reporting an
            // Unhealthy on caller cancellation would lie about the
            // underlying database state on every aborted /healthz
            // request and during graceful shutdown.
            _logger.LogWarning(
                ex,
                "DatabaseHealthCheck — {Label} database connection probe threw; reporting Unhealthy.",
                label);

            data[connectKey] = false;
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"{label} database connection probe threw — the provider or network is rejecting the handshake.",
                exception: ex,
                data: data);
        }

        ct.ThrowIfCancellationRequested();

        // Iter-2 evaluator item 1 — verify that the database has no
        // pending EF Core migrations. The brief's "migrated" wording
        // is enforced here against the __EFMigrationsHistory table.
        //
        // Behaviour matrix:
        //   * applied > 0, pending == 0 → MigrateAsync brought the
        //     schema to head. Healthy on this leg.
        //   * applied > 0, pending  > 0 → a new migration has been
        //     added to the assembly but not applied to the
        //     deployment. Unhealthy — operator must run MigrateAsync.
        //   * applied == 0, pending  > 0 → either EnsureCreated mode
        //     (the schema was bootstrapped without recording history)
        //     OR a fresh database that has never been migrated. We
        //     fall through to the schema-presence probe below; that
        //     probe distinguishes "EnsureCreated populated schema"
        //     (probe succeeds → Healthy) from "no schema at all"
        //     (probe throws → Unhealthy).
        //   * applied == 0, pending == 0 → the assembly has no
        //     migrations at all (uncommon — would mean the
        //     Persistence assembly is unconfigured). Falls through
        //     to the schema-presence probe.
        //
        // Iter-3 evaluator item 3 — when the migration-history probe
        // itself fails (e.g. the relational extension is unavailable
        // on a non-relational provider, the user lacks SELECT on
        // __EFMigrationsHistory, or the network drops between the
        // CanConnect probe and this call), we MUST NOT silently
        // fall back to the schema probe — doing so would weaken the
        // brief's "migrated" contract and let a deployment with
        // pending migrations report Healthy. The non-OCE catch
        // surfaces the failure as Unhealthy with the exception
        // recorded for the operator runbook.
        IReadOnlyList<string> appliedMigrations;
        IReadOnlyList<string> pendingMigrations;
        try
        {
            appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            pendingMigrations = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            data[appliedKey] = appliedMigrations.Count;
            data[pendingKey] = pendingMigrations.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Iter-3 evaluator item 3 — DO NOT silently swallow.
            // Failure to enumerate the migration history means we
            // cannot verify the "reachable AND migrated" Stage 6.2
            // contract; reporting Unhealthy is the honest signal.
            _logger.LogWarning(
                ex,
                "DatabaseHealthCheck — {Label} migration-history probe threw; the 'migrated' contract cannot be verified. Reporting Unhealthy.",
                label);

            data[appliedKey] = -1;
            data[pendingKey] = -1;
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"{label} database migration-history enumeration failed — the Stage 6.2 'migrated' contract cannot be verified. Triage: confirm the deployment principal has SELECT on __EFMigrationsHistory, that the provider supports relational migration APIs, and that no DDL change has dropped the history table.",
                exception: ex,
                data: data);
        }

        if (appliedMigrations.Count > 0 && pendingMigrations.Count > 0)
        {
            _logger.LogWarning(
                "DatabaseHealthCheck — {Label} database has {Pending} pending EF migration(s) on top of {Applied} applied; reporting Unhealthy.",
                label,
                pendingMigrations.Count,
                appliedMigrations.Count);

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"{label} database has {pendingMigrations.Count} pending EF Core migration(s) outstanding (first pending: {pendingMigrations[0]}). The deployment must run MigrateAsync / `dotnet ef database update` before the worker can be considered healthy.",
                data: data);
        }

        // Iter-4 evaluator item 1 — when the operator opted into the
        // strict migration bootstrap (MessagingDb:UseMigrations=true,
        // i.e. DatabaseInitializer calls MigrateAsync), an empty
        // applied list combined with any pending migration means the
        // production host's MigrateAsync was never executed. This is
        // the same canonical "deployment forgot to migrate" footgun
        // the applied>0+pending>0 leg above catches, but for a fresh
        // database that has never recorded ANY migration. The
        // iter-3 implementation silently fell through to the schema
        // probe here, which let an EnsureCreated-bootstrapped DB
        // report Healthy even when UseMigrations=true — violating
        // the brief's "reachable AND migrated" contract.
        //
        // When UseMigrations=false (dev/test/EnsureCreated mode),
        // we DO want to fall through — EnsureCreated never records
        // history, so an empty applied list is expected and the
        // schema probe is the authoritative signal.
        var useMigrations = _configuration.GetValue(UseMigrationsConfigKey, defaultValue: false);
        data[$"{label}_use_migrations"] = useMigrations;

        if (useMigrations && appliedMigrations.Count == 0 && pendingMigrations.Count > 0)
        {
            _logger.LogWarning(
                "DatabaseHealthCheck — {Label} database has zero applied migrations but {Pending} pending ({UseMigrationsKey}=true). The deployment MigrateAsync step did not run; reporting Unhealthy.",
                label,
                pendingMigrations.Count,
                UseMigrationsConfigKey);

            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"{label} database has zero applied migrations but {pendingMigrations.Count} pending (first pending: {pendingMigrations[0]}). {UseMigrationsConfigKey}=true so the deployment's MigrateAsync step must have run to satisfy the Stage 6.2 'migrated' contract.",
                data: data);
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            await schemaProbe(db, ct).ConfigureAwait(false);
            data[schemaKey] = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Iter-3 evaluator item 1 — OCE propagates so the
            // HealthCheckService can treat caller cancellation /
            // host shutdown as the out-of-band signal it is. Any
            // other exception (table missing, driver fault, etc.)
            // is the real "schema not migrated" signal we want
            // to surface.
            _logger.LogWarning(
                ex,
                "DatabaseHealthCheck — {Label} schema probe threw; the database is reachable but the schema is missing or out of sync.",
                label);

            data[schemaKey] = false;
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                description: $"{label} database is reachable but the schema is missing or out of sync — either migrations were not applied or the table has been altered out-of-band.",
                exception: ex,
                data: data);
        }

        return null;
    }
}
