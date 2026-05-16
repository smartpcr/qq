// -----------------------------------------------------------------------
// <copyright file="SlackColumnTypes.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System.Globalization;

/// <summary>
/// Centralised SQL column-type strings used by the Slack entity
/// configurations. Names are emitted verbatim into the
/// <c>CREATE TABLE</c> statement, so they must be acceptable to every
/// database provider the connector is expected to run on. The current
/// provider matrix is:
/// <list type="bullet">
///   <item><description>SQL Server -- direct support for
///   <c>nvarchar(N)</c>, <c>nvarchar(max)</c>, <c>bit</c>,
///   <c>datetimeoffset</c>.</description></item>
///   <item><description>SQLite (used by integration tests) -- type
///   declarations are accepted verbatim and resolved to a storage class
///   via SQLite's affinity rules
///   (<c>nvarchar</c> / <c>char</c> -> TEXT, <c>bit</c> -> NUMERIC,
///   <c>datetimeoffset</c> -> NUMERIC, but EF Core's value converters
///   write the actual storage as TEXT for date/time and INTEGER for
///   bool, so persistence is functionally correct).</description></item>
/// </list>
/// </summary>
internal static class SlackColumnTypes
{
    /// <summary>
    /// Column type for a bounded-length unicode string column. Returns
    /// <c>nvarchar(N)</c> with the supplied length.
    /// </summary>
    /// <param name="length">Maximum length, in characters.</param>
    public static string UnicodeString(int length)
        => string.Format(CultureInfo.InvariantCulture, "nvarchar({0})", length);

    /// <summary>
    /// Column type for an unbounded unicode text column. Resolves to
    /// <c>TEXT</c> -- SQLite's native unbounded text type, and a
    /// SQL Server legacy alias still accepted by <c>CREATE TABLE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite cannot parse <c>nvarchar(max)</c> (rejects <c>max</c> as
    /// a non-integer length argument), so the canonical SQL Server
    /// spelling is not portable. Production migrations targeting SQL
    /// Server may override this column type to <c>nvarchar(max)</c>
    /// after the Slack tables are added to the upstream
    /// <c>MessagingDbContext</c> in Stage 2.3.
    /// </para>
    /// </remarks>
    public const string UnicodeStringMax = "TEXT";

    /// <summary>
    /// Column type for a boolean. <c>bit</c> on SQL Server; resolved to
    /// NUMERIC affinity (storing 0/1) by SQLite.
    /// </summary>
    public const string Boolean = "bit";

    /// <summary>
    /// Column type for a <see cref="System.DateTimeOffset"/>.
    /// <c>datetimeoffset</c> on SQL Server; SQLite stores it as TEXT in
    /// ISO-8601 form via EF Core's value converter.
    /// </summary>
    public const string DateTimeOffset = "datetimeoffset";
}
