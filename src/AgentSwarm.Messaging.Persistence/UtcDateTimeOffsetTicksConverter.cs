using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Provider-agnostic <see cref="DateTimeOffset"/> ↔ <see cref="long"/>
/// value converters for the messenger persistence schema. The
/// <see cref="OutboundMessage"/> entity stores its timestamp columns
/// (<c>CreatedAt</c>, <c>NextRetryAt</c>, <c>SentAt</c>) as UTC-ticks
/// <c>INTEGER</c> rather than the EF Core SQLite default ISO-8601 TEXT
/// because:
/// </summary>
/// <remarks>
/// <para>
/// 1. The EF Core 8 SQLite query translator cannot translate range
/// comparisons (<c>&lt;=</c>, <c>&gt;</c>, etc.) on
/// <see cref="DateTimeOffset"/> TEXT columns; it raises
/// <c>InvalidOperationException: The LINQ expression ... could not be
/// translated.</c> Stage 2.3's
/// <c>PersistentOutboundQueue.DequeueAsync</c> performs exactly such a
/// comparison (<c>NextRetryAt &lt;= now</c>) when deciding whether a
/// Failed-state message is due for retry, so the column has to be
/// numeric for the query to run on SQLite at all.
/// </para>
/// <para>
/// 2. <c>ORDER BY CreatedAt</c> on a numeric column is unambiguous;
/// lexical ordering on TEXT happens to coincide with chronological
/// ordering when every value uses the same offset, but the contract is
/// stronger when expressed in ticks.
/// </para>
/// <para>
/// All callers in this codebase use <see cref="DateTimeOffset.UtcNow"/>
/// or <see cref="TimeProvider.GetUtcNow"/>, so the offset component is
/// always <see cref="TimeSpan.Zero"/>. Storing
/// <see cref="DateTimeOffset.UtcTicks"/> is loss-free for those values
/// and round-trips back to <see cref="DateTimeOffset"/> with the same
/// zero offset.
/// </para>
/// </remarks>
internal static class UtcDateTimeOffsetTicksConverter
{
    /// <summary>
    /// Loss-free UTC-ticks converter for non-nullable
    /// <see cref="DateTimeOffset"/> columns.
    /// </summary>
    public static readonly ValueConverter<DateTimeOffset, long> Instance =
        new(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
}

/// <summary>
/// Nullable companion to <see cref="UtcDateTimeOffsetTicksConverter"/>.
/// </summary>
internal static class NullableUtcDateTimeOffsetTicksConverter
{
    /// <summary>
    /// Loss-free UTC-ticks converter for nullable
    /// <see cref="DateTimeOffset"/> columns.
    /// </summary>
    public static readonly ValueConverter<DateTimeOffset?, long?> Instance =
        new(
            v => v.HasValue ? v.Value.UtcTicks : (long?)null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);
}
