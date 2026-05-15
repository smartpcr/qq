using System.Text;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Tests for the per-request <see cref="HttpContext.Items"/>-backed tenant-ID cache exposed
/// by <see cref="TenantIdExtractor.GetOrExtractFromBodyAsync"/>. Both
/// <see cref="TenantValidationMiddleware"/> and <see cref="RateLimitMiddleware"/> route
/// through this helper so the second stage in the ASP.NET Core HTTP pipeline never re-parses
/// the inbound JSON body.
/// </summary>
public sealed class TenantIdCacheTests
{
    [Fact]
    public async Task FirstCall_ParsesBody_AndCachesResult()
    {
        const string body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-x\"}}}";
        var context = NewBufferedContext(body);

        var first = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Equal("tenant-x", first);
        // Items should now contain a single cache entry under a non-string key.
        Assert.Single(context.Items.Keys.Where(k => k is not string));
    }

    [Fact]
    public async Task SecondCall_ReturnsCachedValue_WithoutRereadingBody()
    {
        const string body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-cache\"}}}";
        var context = NewBufferedContext(body);

        var first = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        // Mutate the request body to garbage AFTER the first call. If the second call
        // re-parses, it will return null. If it correctly hits the cache, it returns the
        // value from the first call.
        var garbage = Encoding.UTF8.GetBytes("not-json-anymore");
        var ms = new MemoryStream(garbage);
        context.Request.Body = ms;
        context.Request.ContentLength = garbage.Length;

        var second = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Equal("tenant-cache", first);
        Assert.Equal("tenant-cache", second);
    }

    [Fact]
    public async Task NullExtraction_IsCached_SoSecondCallSkipsReparse()
    {
        // No tenant on either path → first call returns null AND caches the negative.
        const string body = "{ \"type\": \"message\", \"id\": \"act-1\" }";
        var context = NewBufferedContext(body);

        var first = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        // Replace the body with a payload that would extract a real tenant if re-parsed.
        // The cached null must short-circuit so the second call returns null too — proving
        // the negative result is genuinely cached, not silently re-derived.
        var richer = Encoding.UTF8.GetBytes("{\"channelData\":{\"tenant\":{\"id\":\"injected\"}}}");
        var ms = new MemoryStream(richer);
        ms.Position = 0;
        context.Request.Body = ms;
        context.Request.ContentLength = richer.Length;

        var second = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task NullContext_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => TenantIdExtractor.GetOrExtractFromBodyAsync(context: null!, CancellationToken.None));
    }

    [Fact]
    public async Task CacheKey_IsObjectInstance_NotStringLiteral()
    {
        // Defensive: the cache must use a private object key so caller-defined string keys
        // in HttpContext.Items cannot collide. Verify by populating Items with a string key
        // that LOOKS like a tenant cache entry and confirming the helper still parses the
        // body normally rather than returning the spurious value.
        const string body = "{\"channelData\":{\"tenant\":{\"id\":\"real-tenant\"}}}";
        var context = NewBufferedContext(body);

        // Caller plants a misleading string-keyed entry — the helper must ignore it.
        context.Items["__agentswarm.teams.tenant.id"] = "spoof-tenant";
        context.Items["TenantId"] = "spoof-tenant";

        var result = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Equal("real-tenant", result);
    }

    [Fact]
    public async Task BothMiddlewareHelperCalls_OnlyParseBodyOnce()
    {
        // End-to-end invariant the optimization exists for: TenantValidationMiddleware (call
        // 1) parses; RateLimitMiddleware (call 2) reads from the cache. We simulate this by
        // invoking the helper twice and verifying that wiping the body between calls does
        // not change the second result.
        const string body = "{\"conversation\":{\"tenantId\":\"tenant-y\"}}";
        var context = NewBufferedContext(body);

        var first = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        // Hard reset the body to empty — second call MUST come from cache.
        context.Request.Body = new MemoryStream();
        context.Request.ContentLength = 0;

        var second = await TenantIdExtractor.GetOrExtractFromBodyAsync(context, CancellationToken.None);

        Assert.Equal("tenant-y", first);
        Assert.Equal("tenant-y", second);
    }

    private static HttpContext NewBufferedContext(string body)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(body))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var ms = new MemoryStream(bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;
            context.Request.Body = ms;
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        else
        {
            context.Request.Body = new MemoryStream();
            context.Request.ContentLength = 0;
            context.Request.ContentType = "application/json";
        }
        return context;
    }
}
