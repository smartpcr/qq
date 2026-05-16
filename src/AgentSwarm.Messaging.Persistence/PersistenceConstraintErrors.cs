using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// Shared helpers for classifying <see cref="DbUpdateException"/> instances
/// raised by EF Core. Several stores need to distinguish a UNIQUE / PRIMARY
/// KEY collision (which they swallow as an idempotent no op) from a real
/// write failure (which must propagate). Centralising the provider specific
/// error string matching here keeps every store using the same heuristic.
/// </summary>
public static class PersistenceConstraintErrors
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> was raised
    /// by a UNIQUE or PRIMARY KEY violation. Walks the inner exception chain
    /// because SqliteException nests under DbUpdateException, and other
    /// providers wrap differently. Matches the SQLite, SQL Server, and
    /// PostgreSQL flavours of the message.
    /// </summary>
    /// <param name="ex">Exception raised by <c>SaveChanges</c>.</param>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        for (var current = (Exception?)ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrEmpty(message))
            {
                continue;
            }

            if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Violation of UNIQUE KEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("PRIMARY KEY must be unique", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
