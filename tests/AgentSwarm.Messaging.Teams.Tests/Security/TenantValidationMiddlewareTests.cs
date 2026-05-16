using System.Text;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;
using static AgentSwarm.Messaging.Teams.Tests.Security.StaticUserRoleProviderTests;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class TenantValidationMiddlewareTests
{
    private const string AllowedTenant = "tenant-allowed";
    private const string DisallowedTenant = "tenant-blocked";

    [Fact]
    public async Task InvokeAsync_AllowedTenant_PassesThroughAndDoesNotAudit()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: AllowedTenant));
        var called = false;
        Task NextAsync(HttpContext _) { called = true; return Task.CompletedTask; }

        await middleware.InvokeAsync(context, NextAsync);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_DisallowedTenant_Rejects403AndAudits_UnauthorizedTenantRejected()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: DisallowedTenant));
        var called = false;
        Task NextAsync(HttpContext _) { called = true; return Task.CompletedTask; }

        await middleware.InvokeAsync(context, NextAsync);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.SecurityRejection, entry.EventType);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.Equal("UnauthorizedTenantRejected", entry.Action);
        Assert.Equal(DisallowedTenant, entry.TenantId);
        Assert.Equal(AuditActorTypes.User, entry.ActorType);
        Assert.Equal(DisallowedTenant, entry.ActorId);
    }

    [Fact]
    public async Task InvokeAsync_BodyMissingTenant_Rejects403AndAuditsWithUnknownActor()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: null));

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("unknown", entry.ActorId);
        Assert.Equal(string.Empty, entry.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_EmptyAllowList_RejectsEveryRequest()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: Array.Empty<string>());
        var context = NewBotContext(BuildBody(tenantId: AllowedTenant));

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("UnauthorizedTenantRejected", entry.Action);
        Assert.Contains("deny-all", entry.PayloadJson);
    }

    [Fact]
    public async Task InvokeAsync_FallsBackToConversationTenantIdWhenChannelDataAbsent()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var body = "{\"conversation\":{\"tenantId\":\"" + AllowedTenant + "\"}}";
        var context = NewBotContext(body);
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_NonBotPath_PassesThroughWithoutInspectingBody()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: DisallowedTenant), path: "/healthz");
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_MalformedJson_RejectsAsTenantMissing()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext("not-json");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("UnauthorizedTenantRejected", entry.Action);
    }

    [Fact]
    public async Task InvokeAsync_AllowedTenant_RestoresBodyStreamPositionForCloudAdapter()
    {
        // CloudAdapter re-reads the body after the middleware. The middleware must rewind
        // the buffered body to position 0 before forwarding.
        var (_, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var body = BuildBody(tenantId: AllowedTenant);
        var context = NewBotContext(body);
        long observedPosition = -1;

        await middleware.InvokeAsync(context, ctx =>
        {
            observedPosition = ctx.Request.Body.Position;
            return Task.CompletedTask;
        });

        Assert.Equal(0, observedPosition);
    }

    [Fact]
    public async Task InvokeAsync_DisallowedTenant_CorrelationIdHeaderReusedInAudit()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: DisallowedTenant));
        context.Request.Headers["x-ms-correlation-id"] = "corr-trace-001";

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("corr-trace-001", entry.CorrelationId);
    }

    [Fact]
    public async Task InvokeAsync_AuditLoggerThrows_FailsClosed_WithUnauthorizedTenantAuditException()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        auditLogger.Throw = new InvalidOperationException("audit-store-down");
        var context = NewBotContext(BuildBody(tenantId: DisallowedTenant));

        // Stage 5.1 iter-4 evaluator feedback item 5 — when the audit logger fails the
        // middleware MUST throw UnauthorizedTenantAuditException so the host's exception
        // pipeline surfaces an HTTP 500. The previous behaviour (swallow + still 403)
        // left no immutable SecurityRejection record while still telling the caller the
        // request was rejected — a silent compliance gap the evaluator explicitly flagged.
        var ex = await Assert.ThrowsAsync<UnauthorizedTenantAuditException>(
            () => middleware.InvokeAsync(context, _ => Task.CompletedTask));

        Assert.Contains(DisallowedTenant, ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task InvokeAsync_NullContext_Throws()
    {
        var (_, middleware) = BuildMiddleware(allowed: AllowedTenant);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => middleware.InvokeAsync(null!, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task InvokeAsync_NullNext_Throws()
    {
        var (_, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var context = NewBotContext(BuildBody(tenantId: AllowedTenant));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => middleware.InvokeAsync(context, null!));
    }

    // iter-10 hardening regression suite — whitespace-only tenant IDs must NOT pass the
    // allow-list comparison, and surrounding whitespace must not be able to mask a
    // mismatching tenant. The middleware now trims + rejects whitespace-only values at
    // both the parse layer (ExtractTenantIdAsync) and the allow-list layer (InvokeAsync)
    // so the fail-closed contract holds even if a future parser regression returns "  ".

    [Fact]
    public async Task InvokeAsync_WhitespaceOnlyChannelDataTenantId_FallsBackToConversationTenantId()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"   \"}},\"conversation\":{\"tenantId\":\"" + AllowedTenant + "\"}}";
        var context = NewBotContext(body);
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_WhitespaceOnlyConversationTenantId_Rejects403AsTenantMissing()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var body = "{\"type\":\"message\",\"conversation\":{\"tenantId\":\"\\t\\t \"}}";
        var context = NewBotContext(body);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("unknown", entry.ActorId);
        Assert.Equal(string.Empty, entry.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_PaddedTenantId_IsTrimmedAndAccepted()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        // Leading + trailing whitespace must be trimmed by the parser; otherwise a benign
        // operator-supplied allow-list entry would be defeated by a stray channel-formatting
        // space.
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"  " + AllowedTenant + "  \"}}}";
        var context = NewBotContext(body);
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_JsonNullTenantId_FallsBackToConversationTenantId()
    {
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        // The channelData.tenant.id is JSON null (ValueKind.Null, not String) — the parser
        // must skip it and consult the conversation.tenantId fallback rather than blowing up.
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":null}},\"conversation\":{\"tenantId\":\"" + AllowedTenant + "\"}}";
        var context = NewBotContext(body);
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_TenantIdCaseMismatch_Rejects()
    {
        // Acceptance criteria: "Unauthorized tenant/user is rejected." The allow-list uses
        // StringComparer.Ordinal (case-sensitive) so an operator-supplied lower-case allow-list
        // entry MUST reject an upper-case inbound tenant ID. This regression locks the
        // Ordinal comparer contract: if a future refactor swaps to OrdinalIgnoreCase the
        // assertion below flips.
        var (auditLogger, middleware) = BuildMiddleware(allowed: "tenant-lower");
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"TENANT-LOWER\"}}}";
        var context = NewBotContext(body);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("UnauthorizedTenantRejected", entry.Action);
        Assert.Equal("TENANT-LOWER", entry.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_ChannelDataTenantIdAsObject_FallsBackToConversationTenantId()
    {
        // Malformed channelData.tenant.id (object instead of string) must NOT throw — the
        // parser checks ValueKind == String and otherwise falls through to the conversation
        // fallback, preserving the "fail closed, not crash" invariant for the host pipeline.
        var (auditLogger, middleware) = BuildMiddleware(allowed: AllowedTenant);
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":{\"nested\":1}}},\"conversation\":{\"tenantId\":\"" + AllowedTenant + "\"}}";
        var context = NewBotContext(body);
        var called = false;

        await middleware.InvokeAsync(context, _ => { called = true; return Task.CompletedTask; });

        Assert.True(called);
        Assert.Empty(auditLogger.Entries);
    }

    [Fact]
    public async Task InvokeAsync_AllowListContainingWhitespaceOnlyEntry_DoesNotMatchWhitespaceTenantId()
    {
        // Defence-in-depth: even if the operator configures a pathological allow-list entry
        // containing only whitespace, a whitespace-only inbound tenant ID must still reject
        // because InvokeAsync now short-circuits on IsNullOrWhiteSpace before consulting the
        // allow-list. This locks the iter-10 fail-closed contract.
        var (auditLogger, middleware) = BuildMiddleware(allowed: "   ");
        var body = "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"   \"}}}";
        var context = NewBotContext(body);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal("UnauthorizedTenantRejected", entry.Action);
    }

    [Fact]
    public void Constructor_NullArgs_Throw()
    {
        var options = WrapInMonitor(new TeamsMessagingOptions());
        Assert.Throws<ArgumentNullException>(() => new TenantValidationMiddleware(null!, new RecordingAuditLogger(), NullLogger<TenantValidationMiddleware>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TenantValidationMiddleware(options, null!, NullLogger<TenantValidationMiddleware>.Instance));
        Assert.Throws<ArgumentNullException>(() => new TenantValidationMiddleware(options, new RecordingAuditLogger(), null!));
    }

    private static (RecordingAuditLogger Audit, TenantValidationMiddleware Middleware) BuildMiddleware(string allowed)
        => BuildMiddleware(new[] { allowed });

    private static (RecordingAuditLogger Audit, TenantValidationMiddleware Middleware) BuildMiddleware(IEnumerable<string> allowed)
    {
        var messaging = new TeamsMessagingOptions
        {
            AllowedTenantIds = allowed.ToList(),
        };
        var auditLogger = new RecordingAuditLogger();
        var middleware = new TenantValidationMiddleware(
            WrapInMonitor(messaging),
            auditLogger,
            NullLogger<TenantValidationMiddleware>.Instance);
        return (auditLogger, middleware);
    }

    private static string BuildBody(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return "{\"type\":\"message\"}";
        }

        return "{\"type\":\"message\",\"channelData\":{\"tenant\":{\"id\":\"" + tenantId + "\"}}}";
    }

    private static HttpContext NewBotContext(string body, string path = TenantValidationMiddleware.DefaultBotEndpointPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
