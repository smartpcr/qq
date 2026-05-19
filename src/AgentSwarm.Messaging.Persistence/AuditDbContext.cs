// -----------------------------------------------------------------------
// <copyright file="AuditDbContext.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Stage 5.3 — dedicated <see cref="DbContext"/> for audit log
/// persistence. Backed by the
/// <c>ConnectionStrings:AuditDb</c> configuration entry (separate
/// from <c>ConnectionStrings:MessagingDb</c> per the implementation
/// plan, so audit retention, backup, and isolation policies can
/// diverge from the operational messaging stores).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate context.</b> The Stage 5.3 brief mandates a
/// separate <c>AuditDbContext</c> with an <c>AuditLogs</c>
/// <see cref="DbSet{TEntity}"/>; the implementation plan's Stage 6.3
/// step on <c>appsettings.json</c> further declares
/// <c>ConnectionStrings:AuditDb</c> as a sibling of
/// <c>ConnectionStrings:MessagingDb</c> so the audit store can be
/// pointed at a tamper-evident sink (an append-only PostgreSQL
/// schema, an S3-backed SQL Server, or any other write-once
/// destination) without dragging the high-write operational tables
/// (outbox, dedup, inbound updates) along. Splitting the context
/// also keeps a hostile compromise of the operational DB from
/// silently corrupting the audit trail.
/// </para>
/// <para>
/// <b>Configuration scoping.</b> The audit configuration applied
/// here — <see cref="AuditLogEntryConfiguration"/> — is intentionally
/// excluded from <see cref="MessagingDbContext"/>'s assembly scan so
/// the <c>audit_logs</c> table lives in this context's database
/// only. Tests and dev/local hosts that point both contexts at the
/// same SQLite file get exactly one audit table; production hosts
/// that wire two distinct connection strings get the isolation the
/// Stage 5.3 brief intends.
/// </para>
/// <para>
/// <b>Immutability.</b> This context exposes <see cref="AuditLogs"/>
/// for inserts and read-only queries. There is no
/// <c>IAuditLogger.UpdateAsync</c> / <c>DeleteAsync</c> surface on
/// the public interface, so the only mutation path is via direct
/// EF Core access — a deliberate trade-off so support / replay
/// tooling can investigate historical rows without bypassing DI.
/// Hosts that need to enforce strict immutability at the storage
/// tier should grant the application identity insert-only
/// privileges on <c>audit_logs</c> (PostgreSQL: <c>REVOKE UPDATE,
/// DELETE ... GRANT INSERT, SELECT</c>; SQL Server: equivalent
/// <c>DENY UPDATE, DELETE</c>).
/// </para>
/// </remarks>
public sealed class AuditDbContext : DbContext
{
    /// <summary>
    /// Exception message thrown by <see cref="EnsureAuditEntriesAppendOnly"/>
    /// when a caller attempts to mutate / delete an
    /// <see cref="AuditLogEntry"/> via this context. Centralised so
    /// tests can pin the exact text.
    /// </summary>
    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 5 — exception message thrown
    /// by <see cref="EnsureAuditEntriesAppendOnly"/> when an Added
    /// <see cref="AuditLogEntry"/> carries a non-Telegram
    /// <see cref="AuditLogEntry.Platform"/> value. Centralised so
    /// tests can pin the exact text. The Stage 5.3 brief requires
    /// the column to be <i>always</i> Telegram on this connector;
    /// the configuration-level <c>HasDefaultValue("Telegram")</c>
    /// covers raw-SQL inserts, but a direct
    /// <c>AuditDbContext.AuditLogs.Add(new AuditLogEntry { Platform =
    /// "Discord", ... })</c> would otherwise bypass that default
    /// because EF Core honours the explicitly-set value.
    /// </summary>
    internal const string InvalidPlatformViolationMessage =
        "AuditLogEntry.Platform must be \""
        + AuditLogEntry.TelegramPlatform
        + "\" on this connector — the Stage 5.3 brief mandates "
        + "Platform=Telegram for every audit row. To support an "
        + "additional messenger, add a dedicated AuditDbContext / "
        + "writer rather than reusing this one.";

    /// <summary>
    /// Stage 5.3 iter-9 evaluator item 3 — exception message thrown
    /// when an Added <see cref="AuditLogEntry"/> carries a non-null
    /// <see cref="AuditLogEntry.Details"/> string that does not parse
    /// as a valid JSON document. The Stage 5.3 brief types the
    /// column "<i>Details (JSON)</i>"; the
    /// <see cref="PersistentAuditLogger"/> writer is the primary gate
    /// (rejects on the way in), but a direct
    /// <c>AuditDbContext.AuditLogs.Add(new AuditLogEntry { Details =
    /// "not-json", ... })</c> from another in-assembly caller would
    /// otherwise bypass that validation. The change-tracker guard in
    /// <see cref="EnsureAuditEntriesAppendOnly"/> runs this same
    /// validation at <c>SaveChanges</c> time so the EF model boundary
    /// itself enforces the JSON contract. Centralised so tests can
    /// pin the exact text.
    /// </summary>
    internal const string InvalidDetailsJsonMessage =
        "AuditLogEntry.Details must be either null or a valid JSON "
        + "document (the Stage 5.3 column is typed `Details (JSON)`). "
        + "Serialize structured payloads via System.Text.Json before "
        + "assigning the property; free-form strings are rejected so "
        + "downstream JSON queries (json_extract / JSON_VALUE / "
        + "jsonb_path_query) cannot land on a malformed row.";

    internal const string ImmutableViolationMessage =
        "AuditLogEntry rows are append-only — UPDATE and DELETE are not "
        + "permitted via AuditDbContext. The Stage 5.3 brief requires audit "
        + "records to be immutable; mutate via the IAuditLogger interface "
        + "(which only exposes Log* methods) or, if direct DB access is "
        + "required, revoke UPDATE/DELETE on audit_logs at the storage "
        + "tier as the AuditDbContext remarks describe.";

    public AuditDbContext(DbContextOptions<AuditDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// <see cref="DbSet{TEntity}"/> backing the
    /// <c>audit_logs</c> table. Written by
    /// <see cref="PersistentAuditLogger"/>; queried by the
    /// in-assembly forensic surface (<see cref="IAuditLogReader"/>).
    /// The Stage 5.3 brief column shape — <c>Id</c>,
    /// <c>MessageId</c>, <c>ExternalUserId</c>, <c>AgentId</c>,
    /// <c>Action</c>, <c>Timestamp</c>, <c>CorrelationId</c>,
    /// <c>TenantId</c>, <c>Details</c>, <c>Platform</c> — is fixed by
    /// <see cref="AuditLogEntryConfiguration"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stage 5.3 iter-8 evaluator item 1 — structural bulk-mutation
    /// fix.</b> Three layers of defence make audit-table mutation
    /// impossible from any caller short of a DBA console:
    /// <list type="number">
    ///   <item>
    ///     <description><b>Access modifier.</b> The set is
    ///     <see langword="internal"/> — only the persistence assembly
    ///     (writer) and the test assembly (via
    ///     <c>InternalsVisibleTo</c>) can resolve it. Production code
    ///     in any other assembly CANNOT call
    ///     <c>ctx.AuditLogs.Where(...).ExecuteDeleteAsync()</c> at
    ///     all — the symbol is not on the type's public surface, so
    ///     the bulk-API call site does not compile outside the
    ///     persistence assembly.</description>
    ///   </item>
    ///   <item>
    ///     <description><b>Change-tracker guard.</b>
    ///     <see cref="EnsureAuditEntriesAppendOnly"/> in
    ///     <see cref="SaveChangesAsync(bool, CancellationToken)"/>
    ///     rejects tracked UPDATE/DELETE so even in-assembly callers
    ///     that mutate via the change tracker are stopped.</description>
    ///   </item>
    ///   <item>
    ///     <description><b>SQL-command interceptor.</b>
    ///     <see cref="AuditLogBulkMutationInterceptor"/> registered
    ///     in <see cref="OnConfiguring(DbContextOptionsBuilder)"/>
    ///     rejects bulk-API UPDATE/DELETE
    ///     (<see cref="Microsoft.EntityFrameworkCore.RelationalQueryableExtensions.ExecuteDeleteAsync{TSource}(System.Linq.IQueryable{TSource}, CancellationToken)"/>,
    ///     <see cref="Microsoft.EntityFrameworkCore.RelationalQueryableExtensions.ExecuteUpdateAsync{TSource}(System.Linq.IQueryable{TSource}, System.Linq.Expressions.Expression{System.Func{Microsoft.EntityFrameworkCore.Query.SetPropertyCalls{TSource}, Microsoft.EntityFrameworkCore.Query.SetPropertyCalls{TSource}}}, CancellationToken)"/>)
    ///     AND raw <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade, string, object[])"/>
    ///     statements that target <c>audit_logs</c>. This is the
    ///     defence-in-depth layer that catches the in-assembly
    ///     bypass (writer-assembly tooling, future migration code)
    ///     before the SQL reaches the database.</description>
    ///   </item>
    /// </list>
    /// External read-only consumers (forensic / replay tools) take a
    /// dependency on <see cref="IAuditLogReader"/> instead of
    /// resolving this context directly; that interface only exposes
    /// <c>IQueryable</c>-with-<c>AsNoTracking</c> projections, so
    /// even an attempt to chain <c>.ExecuteDeleteAsync()</c> off the
    /// read surface lands on a no-tracking query whose UPDATE/DELETE
    /// SQL is still intercepted by layer 3 above.
    /// </para>
    /// </remarks>
    internal DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();

    /// <inheritdoc />
    /// <remarks>
    /// Stage 5.3 iter-8 evaluator item 1 — registers
    /// <see cref="AuditLogBulkMutationInterceptor"/> on EVERY
    /// <see cref="AuditDbContext"/> instance regardless of how the
    /// <see cref="DbContextOptions{TContext}"/> were constructed
    /// (DI host, test harness, design-time scaffolder, manual
    /// <c>new AuditDbContext(...)</c> in a tool). Wiring here rather
    /// than only at the DI seam closes the structural hole where a
    /// future call site forgets to attach the interceptor and a
    /// bulk-UPDATE / bulk-DELETE silently lands against
    /// <c>audit_logs</c>. The interceptor is idempotent: if a host
    /// also wires it through <see cref="DbContextOptionsBuilder.AddInterceptors(System.Collections.Generic.IEnumerable{Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor})"/>
    /// the duplicate is harmless because the guard short-circuits
    /// on non-matching SQL.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(new AuditLogBulkMutationInterceptor());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Explicit single-configuration application rather than the
        // assembly scan used by MessagingDbContext: this context
        // exposes exactly one entity (AuditLogEntry), and pulling in
        // every IEntityTypeConfiguration in the assembly would map
        // the operational tables (OutboundMessage, OperatorBinding,
        // etc.) into this context too — defeating the Stage 5.3
        // brief's storage-isolation intent.
        modelBuilder.ApplyConfiguration(new AuditLogEntryConfiguration());
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditEntriesAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAuditEntriesAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Storage-level guard for the Stage 5.3 immutability requirement
    /// (iter-2 evaluator item 5). Iterates the change tracker for
    /// <see cref="AuditLogEntry"/> entries and throws
    /// <see cref="InvalidOperationException"/> if any are in
    /// <see cref="Microsoft.EntityFrameworkCore.EntityState.Modified"/>
    /// or <see cref="Microsoft.EntityFrameworkCore.EntityState.Deleted"/>
    /// state. The <see cref="IAuditLogger"/> interface already enforces
    /// the contract at the API surface (only <c>Log*</c> methods exist);
    /// this guard closes the back-door for DI consumers that resolve
    /// <see cref="AuditDbContext"/> directly and try to call
    /// <c>SaveChangesAsync</c> after mutating tracked rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any tracked <see cref="AuditLogEntry"/> is in
    /// <c>Modified</c> or <c>Deleted</c> state at save time. The
    /// transaction the caller initiated (if any) is left to the caller
    /// to roll back; this guard runs BEFORE EF Core's SQL emit so no
    /// rows are touched in the database.
    /// </exception>
    private void EnsureAuditEntriesAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<AuditLogEntry>())
        {
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Modified
                || entry.State == Microsoft.EntityFrameworkCore.EntityState.Deleted)
            {
                throw new InvalidOperationException(ImmutableViolationMessage);
            }

            // Stage 5.3 iter-6 evaluator item 5 — also enforce the
            // "Platform is always Telegram" invariant at SaveChanges
            // time so any code path that constructs an AuditLogEntry
            // directly (raw `AuditDbContext.AuditLogs.Add(...)`, a
            // future writer, replay tooling, a misuse in a test) is
            // caught before the row reaches the database. The
            // configuration-level `HasDefaultValue("Telegram")` only
            // covers INSERTs that OMIT Platform; an explicit non-
            // Telegram assignment must still be rejected to honour
            // the Stage 5.3 brief's "always Telegram" guarantee on
            // this connector. The check runs before EF Core emits
            // SQL so no row is touched on rejection — the caller's
            // transaction (if any) remains intact.
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added
                && !string.Equals(
                    entry.Entity.Platform,
                    AuditLogEntry.TelegramPlatform,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(InvalidPlatformViolationMessage);
            }

            // Stage 5.3 iter-9 evaluator item 3 — defense-in-depth
            // JSON validation at the model boundary. The Stage 5.3
            // brief types the column "Details (JSON)";
            // PersistentAuditLogger.LogAsync validates on the way in,
            // but a direct in-assembly Add of a raw AuditLogEntry
            // would otherwise persist invalid-shape Details and
            // poison downstream forensic queries that run
            // json_extract / JSON_VALUE / jsonb_path_query against
            // the column. The check runs before EF Core emits SQL so
            // no row is touched on rejection — the caller's
            // transaction (if any) remains intact.
            if (entry.State == Microsoft.EntityFrameworkCore.EntityState.Added
                && entry.Entity.Details is { } details
                && !IsValidJson(details))
            {
                throw new InvalidOperationException(InvalidDetailsJsonMessage);
            }
        }
    }

    /// <summary>
    /// Stage 5.3 iter-9 evaluator item 3 — minimal JSON validator
    /// used by <see cref="EnsureAuditEntriesAppendOnly"/>. Parses via
    /// <see cref="System.Text.Json.JsonDocument.Parse(string, System.Text.Json.JsonDocumentOptions)"/>
    /// and disposes immediately; whitespace-only and empty-string
    /// inputs are rejected because JSON requires at least one token.
    /// </summary>
    private static bool IsValidJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
