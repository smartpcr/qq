// -----------------------------------------------------------------------
// <copyright file="PersistentAuditLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stage 5.3 EF Core-backed <see cref="IAuditLogger"/>. Persists
/// every audit entry passed through either the general-purpose
/// <see cref="IAuditLogger.LogAsync"/> path or the typed
/// <see cref="IAuditLogger.LogHumanResponseAsync"/> path to the
/// dedicated <see cref="AuditDbContext.AuditLogs"/> table
/// (discriminated by <see cref="AuditLogEntry.EntryKind"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 5.3 brief.</b> The writer maps the abstraction records
/// (<see cref="AuditEntry"/>, <see cref="HumanResponseAuditEntry"/>)
/// into the Stage 5.3 persistence shape:
/// <list type="bullet">
///   <item><description><c>AuditEntry.EntryId</c> /
///   <c>HumanResponseAuditEntry.EntryId</c>
///   → <see cref="AuditLogEntry.Id"/></description></item>
///   <item><description><c>AuditEntry.UserId</c> /
///   <c>HumanResponseAuditEntry.UserId</c>
///   → <see cref="AuditLogEntry.ExternalUserId"/> (rename per
///   architecture.md §3.1)</description></item>
///   <item><description><c>AuditEntry.TenantId</c> /
///   <c>HumanResponseAuditEntry.TenantId</c>
///   → <see cref="AuditLogEntry.TenantId"/> (optional)</description></item>
///   <item><description>Pinned <see cref="AuditLogEntry.Platform"/>
///   = <see cref="AuditLogEntry.TelegramPlatform"/>
///   (<c>"Telegram"</c>) per the brief: <i>"Platform (always
///   'Telegram')"</i>.</description></item>
///   <item><description><see cref="AuditLogEntry.EntryKind"/> set
///   to <see cref="AuditEntryKinds.General"/> or
///   <see cref="AuditEntryKinds.HumanResponse"/> so the row's
///   provenance is recoverable.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Lifetime + scope.</b> Singleton dependency that opens a fresh
/// <see cref="IServiceScope"/> per call to acquire the scoped
/// <see cref="AuditDbContext"/>. Mirrors
/// <see cref="PersistentOutboundMessageIdIndex"/>,
/// <see cref="PersistentOutboundDeadLetterStore"/>,
/// <see cref="PersistentTaskOversightRepository"/>.
/// </para>
/// <para>
/// <b>Transactional write.</b> The Stage 5.3 brief mandates audit
/// writes are persisted <i>transactionally</i>. EF Core wraps the
/// single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
/// call in an implicit transaction already (per provider docs); we
/// nevertheless open an explicit
/// <see cref="DatabaseFacade.BeginTransactionAsync(System.Threading.CancellationToken)"/>
/// so the contract is observable in profiler traces and so the
/// failure-path log carries the transaction-rollback note. The
/// transaction is committed before the method returns; a failure
/// to commit reverts the row and is logged via the catch below.
/// </para>
/// <para>
/// <b>Pre-transaction column gates.</b> The writer validates the
/// string-shape invariants of the mandatory required-and-bounded
/// columns (<see cref="AuditLogEntry.Action"/>,
/// <see cref="AuditLogEntry.EventFamily"/>,
/// <see cref="AuditLogEntry.CorrelationId"/>,
/// <see cref="AuditLogEntry.ExternalUserId"/>) <em>before</em>
/// <see cref="DatabaseFacade.BeginTransactionAsync(System.Threading.CancellationToken)"/>
/// opens a transaction. The motivation is iter-X reviewer item: the
/// EF configuration declares each of those columns
/// <c>HasMaxLength(N).IsRequired()</c>; a null or over-length value
/// would otherwise surface as a provider-specific
/// <see cref="DbUpdateException"/> at <c>SaveChangesAsync</c> time
/// (silent truncation on some providers, an opaque integrity error
/// on others) with no diagnostic pointing at <em>which</em> field is
/// at fault. Failing fast at the persistence boundary with a typed
/// <see cref="ArgumentException"/> whose <c>ParamName</c> names the
/// offending property gives callers an unambiguous signal and keeps
/// the transaction from opening for a request that cannot possibly
/// succeed. The human-response path additionally null-coalesces
/// <see cref="HumanResponseAuditEntry.ActionValue"/> to
/// <see cref="MissingActionValuePlaceholder"/> before mapping it
/// onto <see cref="AuditLogEntry.Action"/> — although the
/// abstraction marks the field <c>required</c>, the runtime cannot
/// guarantee a non-null value (reflection-based deserializers,
/// <c>null!</c> initializers in tests, or any future code path that
/// mints an entry without going through the constructor would
/// otherwise crash the row with an opaque NULL-in-required-column
/// error). The placeholder keeps the audit row landable so the
/// failure is investigable rather than silently dropped.
/// </para>
/// <para>
/// <b>Failure semantics.</b> Audit writes are <b>strict</b> — both
/// the <see cref="IAuditLogger.LogAsync"/> and
/// <see cref="IAuditLogger.LogHumanResponseAsync"/> paths log the
/// failure AND rethrow the underlying
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
/// (or other persistence exception). The Stage 5.3 brief mandates
/// "persist every human response with message ID, user ID, agent
/// ID, timestamp, and correlation ID" and the iter-1 audit
/// guarantee requires the writer NOT to silently drop the row.
/// </para>
/// <para>
/// Callers that genuinely tolerate an audit gap (for example
/// <c>QuestionTimeoutService</c>'s best-effort sweep, which prefers
/// emitting the timeout default to the consuming agent over
/// blocking on the audit DB) MUST catch the exception themselves;
/// the writer is the wrong layer to make that decision because the
/// router and decision handlers do NOT tolerate the gap — a
/// successful <c>CommandResult</c> with no audit row would silently
/// violate the "log every inbound command / log every outbound
/// decision" contract. iter-3 evaluator item 6 explicitly required
/// this propagation.
/// </para>
/// </remarks>
public sealed class PersistentAuditLogger : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentAuditLogger> _logger;

    public PersistentAuditLogger(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentAuditLogger> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Stage 5.3 iter-9 evaluator item 3 — exception message thrown
    /// when a caller passes a non-null <see cref="AuditEntry.Details"/>
    /// or <see cref="HumanResponseAuditEntry.Details"/> that does not
    /// parse as JSON. The Stage 5.3 brief mandates the column is
    /// "<i>Details (JSON)</i>"; this writer enforces the contract at
    /// the persistence boundary so a downstream forensic query that
    /// runs <c>json_extract</c> / <c>jsonb_path_query</c> / SQL Server
    /// <c>JSON_VALUE</c> against the column will never encounter an
    /// invalid-shape row. Centralised as an internal constant so
    /// tests can pin the exact wording without duplicating the
    /// literal.
    /// </summary>
    internal const string InvalidDetailsJsonMessage =
        "AuditEntry.Details / HumanResponseAuditEntry.Details must be "
        + "either null or a valid JSON document (the Stage 5.3 column "
        + "is typed `Details (JSON)`). Serialize structured payloads "
        + "via System.Text.Json before passing them to IAuditLogger; "
        + "free-form strings are rejected so downstream JSON queries "
        + "(json_extract / JSON_VALUE / jsonb_path_query) cannot land "
        + "on a malformed row.";

    /// <summary>
    /// Maximum length of the <see cref="AuditLogEntry.Action"/> column
    /// per <c>AuditLogEntryConfiguration</c>'s
    /// <c>HasMaxLength(64).IsRequired()</c>. Pinned here so the
    /// pre-transaction gate and tests reference one symbol rather
    /// than duplicating the literal across files.
    /// </summary>
    internal const int ActionMaxLength = 64;

    /// <summary>
    /// Maximum length of the <see cref="AuditLogEntry.EventFamily"/>
    /// column per <c>AuditLogEntryConfiguration</c>'s
    /// <c>HasMaxLength(32).IsRequired()</c>.
    /// </summary>
    internal const int EventFamilyMaxLength = 32;

    /// <summary>
    /// Maximum length of the
    /// <see cref="AuditLogEntry.CorrelationId"/> column per
    /// <c>AuditLogEntryConfiguration</c>'s
    /// <c>HasMaxLength(128).IsRequired()</c>.
    /// </summary>
    internal const int CorrelationIdMaxLength = 128;

    /// <summary>
    /// Maximum length of the
    /// <see cref="AuditLogEntry.ExternalUserId"/> column per
    /// <c>AuditLogEntryConfiguration</c>'s
    /// <c>HasMaxLength(128).IsRequired()</c>.
    /// </summary>
    internal const int ExternalUserIdMaxLength = 128;

    /// <summary>
    /// Placeholder value mapped onto
    /// <see cref="AuditLogEntry.Action"/> when a
    /// <see cref="HumanResponseAuditEntry.ActionValue"/> arrives as
    /// <see langword="null"/>. The abstraction marks
    /// <c>ActionValue</c> as <c>required</c> so the compiler
    /// rejects construction sites that omit it, but runtime paths
    /// that bypass the constructor (reflection-based deserializers,
    /// <c>null!</c> initializers in tests, future code paths that
    /// mint an entry by other means) can still land a null. Coercing
    /// to a stable sentinel keeps the row landable and the failure
    /// investigable instead of crashing on the
    /// <c>IsRequired()</c> column constraint at
    /// <c>SaveChangesAsync</c> time with an opaque provider error.
    /// </summary>
    internal const string MissingActionValuePlaceholder = "unknown";

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateDetailsIsJsonOrNull(entry.Details, nameof(entry.Details));

        // Resolve EventFamily once: the abstraction defaults to
        // "general" via property initializer, but a caller can still
        // pass null/whitespace explicitly. Validate the resolved
        // value (not the raw entry.EventFamily) because that is what
        // actually lands in the column.
        var eventFamily = string.IsNullOrWhiteSpace(entry.EventFamily)
            ? AuditEventFamilies.General
            : entry.EventFamily;

        // Pre-transaction column gates (see class remarks). Validate
        // all four required-and-bounded columns BEFORE WriteAsync
        // opens a transaction so a malformed entry fails fast with a
        // typed ArgumentException naming the offending field rather
        // than as an opaque DbUpdateException at SaveChangesAsync.
        ValidateRequiredColumn(entry.Action, ActionMaxLength, nameof(entry.Action));
        ValidateRequiredColumn(eventFamily, EventFamilyMaxLength, nameof(entry.EventFamily));
        ValidateRequiredColumn(entry.CorrelationId, CorrelationIdMaxLength, nameof(entry.CorrelationId));
        ValidateRequiredColumn(entry.UserId, ExternalUserIdMaxLength, nameof(entry.UserId));

        var row = new AuditLogEntry
        {
            Id = entry.EntryId,
            EntryKind = AuditEntryKinds.General,
            EventFamily = eventFamily,
            MessageId = entry.MessageId,
            ExternalUserId = entry.UserId,
            AgentId = entry.AgentId,
            Action = entry.Action,
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
            TenantId = entry.TenantId,
            Platform = AuditLogEntry.TelegramPlatform,
            Details = entry.Details,
            QuestionId = null,
            ActionValue = null,
            Comment = null,
        };

        await WriteAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateDetailsIsJsonOrNull(entry.Details, nameof(entry.Details));

        // Null-coalesce ActionValue → MissingActionValuePlaceholder
        // before mapping it onto AuditLogEntry.Action (see
        // MissingActionValuePlaceholder remarks for the rationale).
        // EventFamily is hardcoded to AuditEventFamilies.Decision
        // ("decision", 8 chars) below, so no per-call validation is
        // needed there; CorrelationId and UserId are validated against
        // their column max-lengths because both are caller-supplied
        // strings landing in required-and-bounded columns.
        var action = entry.ActionValue ?? MissingActionValuePlaceholder;

        ValidateRequiredColumn(action, ActionMaxLength, nameof(entry.ActionValue));
        ValidateRequiredColumn(entry.CorrelationId, CorrelationIdMaxLength, nameof(entry.CorrelationId));
        ValidateRequiredColumn(entry.UserId, ExternalUserIdMaxLength, nameof(entry.UserId));

        var row = new AuditLogEntry
        {
            Id = entry.EntryId,
            EntryKind = AuditEntryKinds.HumanResponse,
            EventFamily = AuditEventFamilies.Decision,
            MessageId = entry.MessageId,
            ExternalUserId = entry.UserId,
            AgentId = entry.AgentId,
            // Stage 5.3 acceptance: "AuditLogEntry exists with
            // Action=approve" for an approve button press. The canonical
            // action verb the operator selected lives in
            // HumanResponseAuditEntry.ActionValue (e.g. "approve" /
            // "reject" / "__timeout__") — surface it directly on the
            // persistence row's Action column so a forensic query can
            // filter `WHERE Action='approve'` without consulting
            // ActionValue. EntryKind / EventFamily still distinguish
            // decisions from commands when needed. The value is
            // coalesced above so a null ActionValue (which the
            // `required` modifier should prevent at compile time but
            // cannot guarantee at runtime — reflection-based
            // deserializers, `null!` test initializers, etc.) lands
            // as `MissingActionValuePlaceholder` rather than crashing
            // the required-column constraint at SaveChanges time.
            Action = action,
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
            TenantId = entry.TenantId,
            Platform = AuditLogEntry.TelegramPlatform,
            // Details carries the workspace / callback context the
            // caller hydrated (chat id, workspace id, etc.) so decision
            // rows are logged with full context per Stage 5.3.
            Details = entry.Details,
            QuestionId = entry.QuestionId,
            ActionValue = entry.ActionValue,
            Comment = entry.Comment,
        };

        await WriteAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage 5.3 iter-9 evaluator item 3 — JSON-shape gate for the
    /// <c>Details</c> column. The Stage 5.3 brief types
    /// <c>Details (JSON)</c>; the IAuditLogger surface accepts
    /// <see cref="string"/> for ergonomics (callers serialize their
    /// own typed payloads), but a free-form string would let a caller
    /// land an invalid-JSON row. We parse via
    /// <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/>
    /// and throw <see cref="ArgumentException"/> if the parse fails;
    /// the row is rejected BEFORE the transaction opens so the audit
    /// table can never carry an invalid-shape <c>Details</c>.
    /// <para>
    /// Whitespace-only and empty-string inputs are also rejected —
    /// JSON requires at least a primitive token (<c>null</c>,
    /// <c>true</c>, <c>false</c>, a number, a string, an object, or
    /// an array), so the empty payload is structurally invalid. A
    /// caller that wants "no details" must pass <see langword="null"/>
    /// explicitly.
    /// </para>
    /// </summary>
    private static void ValidateDetailsIsJsonOrNull(string? details, string paramName)
    {
        if (details is null)
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(details);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(InvalidDetailsJsonMessage, paramName, ex);
        }
    }

    /// <summary>
    /// Pre-transaction gate for required, length-bounded string
    /// columns. Throws <see cref="ArgumentException"/> (or
    /// <see cref="ArgumentNullException"/>) BEFORE
    /// <see cref="WriteAsync"/> opens a transaction so a malformed
    /// entry fails fast with a typed exception naming the offending
    /// property rather than landing as an opaque
    /// <see cref="DbUpdateException"/> at <c>SaveChangesAsync</c>
    /// time. See class remarks "Pre-transaction column gates" for
    /// the full rationale.
    /// </summary>
    /// <param name="value">Value the caller passed.</param>
    /// <param name="maxLength">
    /// Maximum length permitted by the EF configuration's
    /// <c>HasMaxLength(N)</c> for the destination column.
    /// </param>
    /// <param name="paramName">
    /// Name of the caller-facing property (used as
    /// <see cref="ArgumentException.ParamName"/>) so the diagnostic
    /// points operators at the offending field.
    /// </param>
    private static void ValidateRequiredColumn(string? value, int maxLength, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(
                paramName,
                $"{paramName} maps to a required AuditLogEntry column and cannot be null.");
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"{paramName} length {value.Length} exceeds the AuditLogEntry column "
                + $"maximum of {maxLength} characters. Trim or rehash the value at "
                + "the call site before invoking IAuditLogger; the persistence "
                + "boundary rejects over-length values to avoid silent provider "
                + "truncation and opaque DbUpdateException at SaveChanges time.",
                paramName);
        }
    }

    private async Task WriteAsync(AuditLogEntry row, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        db.AuditLogs.Add(row);
        IDbContextTransaction? tx = null;
        try
        {
            // Explicit transaction so the Stage 5.3 brief's "writes
            // audit entries transactionally" contract is observable
            // in profiler traces. EF Core's SaveChangesAsync already
            // wraps a single batch in an implicit transaction, but
            // the explicit Begin/Commit pair makes the boundary
            // explicit for support / forensic tooling and reads
            // cleanly in the catch log below when commit fails.
            // In-memory providers that don't support transactions
            // (e.g. some test doubles) throw on
            // BeginTransactionAsync; in that case we fall through to
            // SaveChangesAsync (which still runs as a single batch).
            if (db.Database.CurrentTransaction is null)
            {
                try
                {
                    tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // Transactions not supported on this provider — fall
                    // back to the implicit-transaction SaveChangesAsync.
                    tx = null;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (tx is not null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Stage 5.3 audit guarantee: "Persist every human response
            // … transactional writes". Iter-1 evaluator item 4: the
            // prior "log and swallow" path let the audit row silently
            // vanish while the business event proceeded — that
            // violates the persist-every-human-response contract and
            // hides the failure from the caller. We now log loudly
            // AND propagate so the caller's audit-failure decision is
            // explicit (handlers that can survive an audit gap, e.g.
            // QuestionTimeoutService's best-effort sweep, can catch
            // and continue; handlers that must not return success
            // without a durable audit row let the exception bubble
            // and the operator sees a failure rather than a silently
            // un-audited approval).
            _logger.LogError(
                ex,
                "PersistentAuditLogger failed to persist audit row; rethrowing so the caller can honor the Stage 5.3 transactional-write contract. Id={Id} EntryKind={EntryKind} EventFamily={EventFamily} CorrelationId={CorrelationId} Platform={Platform}",
                row.Id,
                row.EntryKind,
                row.EventFamily,
                row.CorrelationId,
                row.Platform);
            if (tx is not null)
            {
                try
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogWarning(
                        rollbackEx,
                        "PersistentAuditLogger transaction rollback failed after primary failure. Id={Id}",
                        row.Id);
                }
            }
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
