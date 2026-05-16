// -----------------------------------------------------------------------
// <copyright file="OperatorBindingConfiguration.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgentSwarm.Messaging.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// Stage 3.4 — EF Core configuration for <see cref="OperatorBinding"/>.
/// Persists operator-to-(user, chat, workspace) bindings in the
/// <c>operator_bindings</c> SQLite table (or PostgreSQL / SQL Server
/// in production — the model-level configuration here is
/// provider-agnostic).
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema layout.</b> One row per <c>(TelegramUserId,
/// TelegramChatId, WorkspaceId)</c> triple. The
/// <see cref="OperatorBinding.Id"/> <see cref="Guid"/> is the primary
/// key (surrogate) so the persistence rows can be referenced as a
/// foreign-key target from <see cref="TaskOversight.OperatorBindingId"/>
/// (architecture.md §3.1 lines 105–125 and the relationship diagram
/// at lines 405–417).
/// </para>
/// <para>
/// <b>Indexes (per implementation-plan.md Stage 3.4 step 1 and
/// architecture.md §3.1 "Constraints").</b>
/// <list type="bullet">
///   <item><description><b><c>ix_operator_bindings_user_chat</c> on
///   <c>(TelegramUserId, TelegramChatId)</c></b> — non-unique, supports
///   the hot-path runtime authorization lookup
///   <see cref="IOperatorRegistry.GetBindingsAsync"/> /
///   <see cref="IOperatorRegistry.IsAuthorizedAsync"/>. Non-unique
///   because a single (user, chat) pair may hold multiple bindings
///   (one per workspace — see the second unique index).</description></item>
///   <item><description><b><c>ux_operator_bindings_alias_tenant</c> on
///   <c>(OperatorAlias, TenantId)</c></b> — unique, enforces alias
///   uniqueness within a tenant boundary so
///   <see cref="IOperatorRegistry.GetByAliasAsync"/> (used by
///   <c>/handoff @alias</c>) cannot resolve an operator in a
///   different tenant. Per architecture.md lines 116–119 and the
///   §4.3 cross-doc note, two tenants may independently use the same
///   alias without collision.
///   <para>
///   <b>Lifecycle note — deactivation does NOT release the alias
///   (review-r0 item).</b> The unique index is intentionally
///   <i>not</i> filtered on <see cref="OperatorBinding.IsActive"/>.
///   Setting <c>IsActive = false</c> is a reversible soft-revocation
///   (architecture.md §7.1 "Access revocation is modeled by setting
///   <c>OperatorBinding.IsActive=false</c>" plus the
///   <c>RegisterAsync_RecreatesDeactivatedBinding_AsActive</c> test
///   in <c>PersistentOperatorRegistryTests</c>), and the alias is
///   treated as part of the operator's persistent identity — it
///   stays reserved for as long as the row exists so a subsequent
///   soft-restore of the same operator cannot silently lose its
///   identifier or collide with a fresh claimant that grabbed the
///   alias in the meantime. A partial index filtered on
///   <c>WHERE IsActive = 1</c> would defeat that guarantee: an
///   admin re-activating the original row would either fail at
///   <c>SaveChangesAsync</c> time (a now-active claimant occupies
///   the (alias, tenant) pair) or, worse, the race would resolve
///   non-deterministically depending on transaction ordering and
///   leave two rows that <i>both</i> claim the same alias
///   within the tenant — <see cref="IOperatorRegistry.GetByAliasAsync"/>
///   would then arbitrarily pick one and silently break
///   <c>/handoff @alias</c> routing.
///   </para>
///   <para>
///   <b>Operational consequence.</b> If an administrator wants to
///   transfer an alias to a different operator inside the same
///   tenant, they must FIRST mutate the deactivated row's
///   <see cref="OperatorBinding.OperatorAlias"/> (rename it to a
///   tombstone value, or hard-delete the row) before the new
///   registration can claim the freed <c>(alias, tenant)</c> pair.
///   The upsert path in
///   <see cref="PersistentOperatorRegistry"/>'s
///   <c>StageUpsertAsync</c> only looks up by
///   <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c> so it
///   will NOT discover a deactivated alias-holder owned by a
///   <i>different</i> user; the unique index will surface the
///   collision as a <see cref="DbUpdateException"/> on the new
///   operator's <c>/start</c>, which is the correct fail-loud
///   signal that alias transfer requires the explicit
///   admin-side alias-release step rather than silent reassignment.
///   </para></description></item>
///   <item><description><b><c>ux_operator_bindings_user_chat_workspace</c>
///   on <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c></b> —
///   unique, the canonical "no duplicate binding for the same
///   workspace" constraint from architecture.md §3.1
///   "Constraints". Without this an operator could be registered
///   twice for the same workspace via a concurrent <c>/start</c>
///   race; the upsert path in
///   <c>PersistentOperatorRegistry.RegisterAsync</c> uses this
///   index to detect "binding already exists" and refresh the
///   row in-place instead of inserting a second one.</description></item>
///   <item><description><b><c>ix_operator_bindings_user</c> on
///   <c>TelegramUserId</c></b> — non-unique, used by
///   <see cref="IOperatorRegistry.GetAllBindingsAsync"/> for the
///   administrative "every binding the user has across every chat"
///   query (<c>/status</c> across workspaces, admin audit
///   listings).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Roles column.</b> Stored as a serialized JSON string so the
/// table remains SQLite-compatible (SQLite has no native array type)
/// while the column shape is identical on PostgreSQL / SQL Server —
/// each provider stores the JSON as a <c>TEXT</c> / <c>nvarchar(max)</c>
/// column. The accompanying <see cref="ValueComparer{T}"/> implements
/// element-wise equality so EF Core change-tracking treats two
/// logically-identical role lists as equal (a missing comparer would
/// short-cut on reference equality and miss in-place role mutations
/// during <c>RegisterAsync</c>'s upsert path).
/// </para>
/// <para>
/// <b>Time encoding.</b> <see cref="OperatorBinding.RegisteredAt"/>
/// stores as Unix milliseconds via the same
/// <see cref="ValueConverter{TModel, TProvider}"/> pattern used by
/// <see cref="InboundUpdateConfiguration"/>,
/// <see cref="OutboundDeadLetterConfiguration"/>, and
/// <see cref="TaskOversightConfiguration"/>, so the time
/// representation is consistent across every messenger table.
/// </para>
/// </remarks>
public sealed class OperatorBindingConfiguration : IEntityTypeConfiguration<OperatorBinding>
{
    private static readonly ValueConverter<DateTimeOffset, long> DateTimeOffsetToUnixMillis =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    /// <summary>
    /// JSON serializer settings pinned to invariant defaults so the
    /// stored payload is byte-stable across processes / environments
    /// (no culture-dependent quoting, no escaped Unicode beyond the
    /// JSON spec, no whitespace).
    /// </summary>
    private static readonly JsonSerializerOptions RolesJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly ValueConverter<IReadOnlyList<string>, string> RolesToJson =
        new(
            v => SerializeRoles(v),
            v => DeserializeRoles(v));

    private static readonly ValueComparer<IReadOnlyList<string>> RolesComparer =
        new(
            (a, b) => RolesEqual(a, b),
            v => v == null
                ? 0
                : v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item ?? string.Empty)),
            v => v == null
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : v.ToArray());

    public void Configure(EntityTypeBuilder<OperatorBinding> builder)
    {
        builder.ToTable("operator_bindings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedNever();

        builder.Property(x => x.TelegramUserId)
            .IsRequired();

        builder.Property(x => x.TelegramChatId)
            .IsRequired();

        builder.Property(x => x.ChatType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(x => x.OperatorAlias)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.TenantId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.WorkspaceId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.Roles)
            .HasConversion(RolesToJson, RolesComparer)
            .IsRequired();

        builder.Property(x => x.RegisteredAt)
            .HasConversion(DateTimeOffsetToUnixMillis)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.HasIndex(x => new { x.TelegramUserId, x.TelegramChatId })
            .HasDatabaseName("ix_operator_bindings_user_chat");

        // ux_operator_bindings_alias_tenant — see the class-level
        // <remarks> "Lifecycle note" for why this index is
        // intentionally NOT filtered on IsActive. Deactivation is a
        // reversible soft-revoke (architecture.md §7.1 and the
        // RegisterAsync_RecreatesDeactivatedBinding_AsActive test);
        // the alias remains reserved for the dead row so the
        // soft-restore path cannot silently lose its identifier or
        // collide with a fresh claimant. Transferring an alias to a
        // different operator therefore requires an explicit admin
        // step that first renames or hard-deletes the deactivated
        // row — a new /start that tries to claim a soft-revoked
        // alias inside the same tenant will surface a
        // DbUpdateException, which is the correct fail-loud signal.
        builder.HasIndex(x => new { x.OperatorAlias, x.TenantId })
            .IsUnique()
            .HasDatabaseName("ux_operator_bindings_alias_tenant");

        builder.HasIndex(x => new { x.TelegramUserId, x.TelegramChatId, x.WorkspaceId })
            .IsUnique()
            .HasDatabaseName("ux_operator_bindings_user_chat_workspace");

        builder.HasIndex(x => x.TelegramUserId)
            .HasDatabaseName("ix_operator_bindings_user");
    }

    private static string SerializeRoles(IReadOnlyList<string>? roles)
    {
        var payload = roles is null ? Array.Empty<string>() : roles.ToArray();
        return JsonSerializer.Serialize(payload, RolesJsonOptions);
    }

    private static IReadOnlyList<string> DeserializeRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<string[]>(json, RolesJsonOptions);
            return decoded ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            // Stage 3.4 (iter-2 evaluator item 4) — corrupt persisted
            // authorization data MUST surface as a fail-fast error
            // rather than silently coerce to an empty role list.
            // Returning Array.Empty<string>() here would have stripped
            // the operator's privileges on the next materialization,
            // which would either silently downgrade the operator (e.g.
            // a TenantAdmin loses the role gate that lets them /pause
            // a swarm) OR silently elevate them if a downstream role
            // check uses "empty list ⇒ wildcard" semantics. Either
            // way, the integrity of the authorization data is more
            // important than continued availability of a corrupt row:
            // fail loud so the operator runs the repair migration or
            // restores from backup before any /handoff or /approve
            // decision flows through the bad binding.
            throw new InvalidOperationException(
                "operator_bindings.Roles column contains invalid JSON; "
                + "refusing to materialize the OperatorBinding with an "
                + "empty role list (Stage 3.4). Repair the persisted row "
                + "or restore from backup before continuing. "
                + $"Offending payload (first 256 chars): {Truncate(json, 256)}",
                ex);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";
    }

    private static bool RolesEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
