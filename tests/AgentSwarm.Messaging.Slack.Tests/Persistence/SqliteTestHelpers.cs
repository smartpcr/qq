// -----------------------------------------------------------------------
// <copyright file="SqliteTestHelpers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;

/// <summary>
/// Shared SQLite probing helpers used by the Stage 2.3 and later schema
/// tests. Kept in one place so the same predicate (filter out internal
/// <c>sqlite_%</c> tables, sort alphabetically) is applied everywhere.
/// </summary>
/// <remarks>
/// The helpers deliberately accept a live <see cref="SqliteConnection"/>
/// rather than a connection string so tests can keep an in-memory database
/// open across multiple <see cref="DbCommand"/> invocations without losing
/// the schema (an in-memory SQLite database is destroyed when its last
/// connection closes).
/// </remarks>
internal static class SqliteTestHelpers
{
    /// <summary>
    /// Returns the names of all non-internal tables on the supplied
    /// SQLite connection, ordered alphabetically. Internal SQLite tables
    /// (<c>sqlite_master</c>, <c>sqlite_sequence</c>, ...) are filtered
    /// out so callers can compare results against an exact expected set.
    /// </summary>
    /// <param name="connection">
    /// Live SQLite connection. Will be opened if not already open. Must
    /// not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A snapshot list of user-defined table names. Empty when the
    /// database has no tables.
    /// </returns>
    public static IReadOnlyList<string> ListSqliteUserTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using DbCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        List<string> names = new();
        using DbDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
