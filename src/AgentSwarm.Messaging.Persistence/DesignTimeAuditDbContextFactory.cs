// -----------------------------------------------------------------------
// <copyright file="DesignTimeAuditDbContextFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// <see cref="IDesignTimeDbContextFactory{TContext}"/> implementation
/// that lets the <c>dotnet ef migrations</c> tooling construct an
/// <see cref="AuditDbContext"/> WITHOUT booting the application
/// host. Mirrors <see cref="DesignTimeMessagingDbContextFactory"/>
/// for the audit context (a separate context requires a separate
/// design-time factory because EF Core resolves factories by closed
/// generic type, and the messaging factory only constructs
/// <see cref="MessagingDbContext"/>).
/// </summary>
internal sealed class DesignTimeAuditDbContextFactory
    : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite("Data Source=audit-design.db")
            .Options;
        return new AuditDbContext(options);
    }
}
