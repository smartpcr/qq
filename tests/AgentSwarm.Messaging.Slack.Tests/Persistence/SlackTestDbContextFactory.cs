// -----------------------------------------------------------------------
// <copyright file="SlackTestDbContextFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Persistence;

using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Builds <see cref="SlackTestDbContext"/> instances backed by a fresh
/// in-memory SQLite database. The connection is held open for the
/// lifetime of the factory so the in-memory schema persists across
/// individual <see cref="SlackTestDbContext"/> instances inside a single
/// test fact (e.g., when a test inserts with one context and reads with
/// another).
/// </summary>
internal sealed class SlackTestDbContextFactory : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<SlackTestDbContext> options;
    private bool disposed;

    public SlackTestDbContextFactory()
    {
        this.connection = new SqliteConnection("Filename=:memory:");
        this.connection.Open();
        this.options = new DbContextOptionsBuilder<SlackTestDbContext>()
            .UseSqlite(this.connection)
            .EnableSensitiveDataLogging()
            .Options;

        using SlackTestDbContext bootstrap = this.CreateContext();
        bootstrap.Database.EnsureCreated();
    }

    /// <summary>
    /// Returns a fresh <see cref="SlackTestDbContext"/> sharing the same
    /// underlying SQLite connection.
    /// </summary>
    public SlackTestDbContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        return new SlackTestDbContext(this.options);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.connection.Dispose();
        this.disposed = true;
    }
}
