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
/// Stage 3.4 ΓÇö EF Core configuration for <see cref="OperatorBinding"/>.
/// Persists operator-to-(user, chat, workspace) bindings in the
/// <c>operator_bindings</c> SQLite table (or PostgreSQL / SQL Server
/// in production ΓÇö the model-level configuration here is
/// provider-agnostic except for
/// <see cref="AliasTenantUniqueIndexFilter"/>, whose double-quoted
/// identifier form targets SQLite and PostgreSQL; SQL Server hosts
/// must override the entity configuration to supply the bracket-quoted
/// equivalent (see that field's remarks).
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema layout.</b> One row per <c>(TelegramUserId,
/// TelegramChatId, WorkspaceId)</c> triple. The
/// <see cref="OperatorBinding.Id"/> <see cref="Guid"/> is the primary
/// key (surrogate) so the persistence rows can be referenced as a
/// foreign-key target from <see cref="TaskOversight.OperatorBindingId"/>
/// (architecture.md ┬º3.1 lines 105ΓÇô125 and the relationship diagram
/// at lines 405ΓÇô417).
/// </para>
/// <para>
/// <b>Indexes (per implementation-plan.md Stage 3.4 step 1 and
/// architecture.md ┬º3.1 "Constraints").</b>
/// <list type="bullet">
///   <item><description><b><c>ix_operator_bindings_user_chat</c> on
///   <c>(TelegramUserId, TelegramChatId)</c></b> ΓÇö non-unique, supports
///   the hot-path runtime authorization lookup
///   <see cref="IOperatorRegistry.GetBindingsAsync"/> /
///   <see cref="IOperatorRegistry.IsAuthorizedAsync"/>. Non-unique
///   because a single (user, chat) pair may hold multiple bindings
///   (one per workspace ΓÇö see the second unique index).</description></item>
///   <item><description><b><c>ux_operator_bindings_alias_tenant</c> on
///   <c>(OperatorAlias, TenantId)</c></b> ΓÇö unique AND <b>filtered</b>
///   on <c>IsActive = 1</c> (see
///   <see cref="AliasTenantUniqueIndexFilter"/>), enforces alias
///   uniqueness within a tenant boundary so
///   <see cref="IOperatorRegistry.GetByAliasAsync"/> (used by
///   <c>/handoff @alias</c>) cannot resolve an operator in a
///   different tenant. Per architecture.md lines 116ΓÇô119 and the
///   ┬º4.3 cross-doc note, two tenants may independently use the same
///   alias without collision. The <c>IsActive = 1</c> filter is
///   load-bearing: <c>PersistentOperatorRegistry.StageUpsertAsync</c>
///   looks up existing rows by <c>(UserId, ChatId, WorkspaceId)</c>
///   only, so a deactivated binding owned by a DIFFERENT user would
///   otherwise survive in the index and reject a fresh
///   <c>INSERT</c> when the next operator legitimately claims the
///   same alias inside the same tenant (the deactivation-then-realias
///   path called out in the iter-3 review). With the filter, a row
///   whose <c>IsActive</c> flag has been flipped to <c>false</c> is
///   excluded from uniqueness enforcement and the alias is free to be
///   reassigned without an out-of-band cleanup
///   step.</description></item>
///   <item><description><b><c>ux_operator_bindings_user_chat_workspace</c>
///   on <c>(TelegramUserId, TelegramChatId, WorkspaceId)</c></b> ΓÇö
///   unique, the canonical "no duplicate binding for the same
///   workspace" constraint from architecture.md ┬º3.1
///   "Constraints". Without this an operator could be registered
///   twice for the same workspace via a concurrent <c>/start</c>
///   race; the upsert path in
///   <c>PersistentOperatorRegistry.RegisterAsync</c> uses this
///   index to detect "binding already exists" and refresh the
///   row in-place instead of inserting a second one.</description></item>
///   <item><description><b><c>ix_operator_bindings_user</c> on
///   <c>TelegramUserId</c></b> ΓÇö non-unique, used by
///   <see cref="IOperatorRegistry.GetAllBindingsAsync"/> for the
///   administrative "every binding the user has across every chat"
///   query (<c>/status</c> across workspaces, admin audit
///   listings).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Roles column.</b> Stored as a serialized JSON string so the
/// table remains SQLite-compatible (SQLite has no native array type)
/// while the column shape is identical on PostgreSQL / SQL Server ΓÇö
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
    /// <summary>
    /// Filter expression for the <c>ux_operator_bindings_alias_tenant</c>
    /// <b>partial</b> unique index. Restricts uniqueness enforcement to
    /// <c>IsActive = 1</c> rows so a deactivated binding does NOT block
    /// a later operator from claiming the same alias inside the same
    /// tenant. The literal uses the double-quoted column identifier
    /// form which both SQLite and PostgreSQL accept verbatim ΓÇö the two
    /// providers the messenger persistence layer currently targets.
    /// SQL Server hosts (not yet wired in) would have to override this
    /// entity configuration to substitute the <c>[IsActive] = 1</c>
    /// bracket-quoted form; the quoting style is the only SQL-syntax
    /// wedge between the providers for this expression.
    /// </summary>
    /// <remarks>
    /// Without this filter, the deactivation-then-realias path is
    /// silently broken: <c>PersistentOperatorRegistry.StageUpsertAsync</c>
    /// only looks up a candidate row by <c>(TelegramUserId,
    /// TelegramChatId, WorkspaceId)</c>, so a deactivated row owned by
    /// user A would not be found when user B re-registers under the
    /// same alias and tenant in a different (chat, workspace) ΓÇö the
    /// insert would surface as an opaque unique-constraint violation
    /// at the DB layer rather than as a clean upsert. Adding
    /// <c>WHERE "IsActive" = 1</c> makes the index match the logical
    /// invariant ("at most one ACTIVE operator holds this alias in
    /// this tenant") instead of the stricter physical invariant
    /// ("at most one row, active or not, holds this alias in this
    /// tenant") that the original Stage 3.4 draft mistakenly encoded.
    /// </remarks>
    internal const string AliasTenantUniqueIndexFilter = "\"IsActive\" = 1";

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

        builder.HasIndex(x => new { x.OperatorAlias, x.TenantId })
            .IsUnique()
            .HasFilter(AliasTenantUniqueIndexFilter)
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
            // Stage 3.4 (iter-2 evaluator item 4) ΓÇö corrupt persisted
            // authorization data MUST surface as a fail-fast error
            // rather than silently coerce to an empty role list.
            // Returning Array.Empty<string>() here would have stripped
            // the operator's privileges on the next materialization,
            // which would either silently downgrade the operator (e.g.
            // a TenantAdmin loses the role gate that lets them /pause
            // a swarm) OR silently elevate them if a downstream role
            // check uses "empty list ΓçÆ wildcard" semantics. Either
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
        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "ΓÇª";
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