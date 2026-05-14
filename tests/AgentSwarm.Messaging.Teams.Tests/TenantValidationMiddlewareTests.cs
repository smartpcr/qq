using System.Text;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TenantValidationMiddlewareTests
{
    [Fact]
    public async Task NonWebhookPath_BypassesValidation()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var context = NewContext(path: "/health", method: HttpMethods.Get, body: null);

        await middleware.InvokeAsync(context, next);

        Assert.True(nextInvoked());
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task EmptyAllowList_RejectsWith403()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: Array.Empty<string>());
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: "{}");

        await middleware.InvokeAsync(context, next);

        Assert.False(nextInvoked());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task AllowedTenant_FromChannelData_PassesThrough()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}}}";
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: body);

        await middleware.InvokeAsync(context, next);

        Assert.True(nextInvoked());
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task AllowedTenant_FromConversation_PassesThrough()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var body = "{\"conversation\":{\"tenantId\":\"tenant-a\"}}";
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: body);

        await middleware.InvokeAsync(context, next);

        Assert.True(nextInvoked());
    }

    [Fact]
    public async Task DisallowedTenant_RejectsWith403()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-evil\"}}}";
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: body);

        await middleware.InvokeAsync(context, next);

        Assert.False(nextInvoked());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task MissingTenant_RejectsWith403()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: "{}");

        await middleware.InvokeAsync(context, next);

        Assert.False(nextInvoked());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestBody_IsRewound_ForDownstreamReaders()
    {
        var (middleware, _, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var body = "{\"channelData\":{\"tenant\":{\"id\":\"tenant-a\"}},\"text\":\"hello\"}";
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: body);

        await middleware.InvokeAsync(context, next);

        Assert.Equal(0, context.Request.Body.Position);
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var roundtrip = await reader.ReadToEndAsync();
        Assert.Equal(body, roundtrip);
    }

    [Fact]
    public async Task MalformedJson_RejectsWith403()
    {
        var (middleware, nextInvoked, next) = CreateMiddleware(allowList: new[] { "tenant-a" });
        var context = NewContext(path: TenantValidationMiddleware.WebhookPath, method: HttpMethods.Post, body: "{not-json");

        await middleware.InvokeAsync(context, next);

        Assert.False(nextInvoked());
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static (TenantValidationMiddleware middleware, Func<bool> wasNextInvoked, RequestDelegate next) CreateMiddleware(IEnumerable<string> allowList)
    {
        var invoked = false;
        RequestDelegate next = ctx =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = "app",
            MicrosoftAppPassword = "p",
            MicrosoftAppTenantId = "tenant-a",
            AllowedTenantIds = allowList.ToList(),
        };
        var monitor = new TestOptionsMonitor<TeamsMessagingOptions>(options);
        var middleware = new TenantValidationMiddleware(monitor, NullLogger<TenantValidationMiddleware>.Instance);
        return (middleware, () => invoked, next);
    }

    internal static HttpContext NewContext(string path, string method, string? body)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var ms = new MemoryStream(bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;
            context.Request.Body = ms;
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        context.Response.Body = new MemoryStream();
        return context;
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    private readonly T _value;
    public TestOptionsMonitor(T value) => _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
