using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Worker.Tests;

/// <summary>
/// Verifies that the worker host fails fast at startup when required
/// <c>TeamsMessagingOptions</c> fields are absent. Aligned with the Stage 2.1 acceptance test
/// "Missing config fails startup".
/// </summary>
public sealed class StartupValidationTests
{
    [Fact]
    public void MissingMicrosoftAppId_ThrowsOptionsValidationException()
    {
        var factory = new MisconfiguredFactory(omitField: "MicrosoftAppId");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        // ValidateOnStart wraps the failure in an OptionsValidationException; the WAF may
        // surface it directly or chain via HostingException — either way the message should
        // mention the missing field.
        Assert.Contains("MicrosoftAppId", FlattenMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBotEndpoint_ThrowsOptionsValidationException()
    {
        // Stage 2.1 requires TeamsMessagingOptions startup validation to fail when any
        // required field is missing. BotEndpoint is explicitly enumerated as required in the
        // implementation plan, so this test pins that the validator wired in Program.cs
        // covers BotEndpoint at startup (not just MicrosoftAppId).
        var factory = new MisconfiguredFactory(omitField: "BotEndpoint");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("BotEndpoint", FlattenMessages(ex), StringComparison.Ordinal);
    }

    private static string FlattenMessages(Exception? ex)
    {
        var parts = new List<string>();
        while (ex is not null)
        {
            parts.Add(ex.Message);
            ex = ex.InnerException;
        }
        return string.Join(" | ", parts);
    }

    private sealed class MisconfiguredFactory : WebApplicationFactory<Program>
    {
        private readonly string _omitField;

        public MisconfiguredFactory(string omitField)
        {
            _omitField = omitField;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                // Fully populated baseline — the test then drops one field at a time so the
                // validator failure message can be asserted to mention the omitted field
                // specifically. Empty strings override any non-empty default from
                // appsettings.json (which ships with BotEndpoint pre-populated).
                var settings = new Dictionary<string, string?>
                {
                    ["Teams:MicrosoftAppId"] = "test-app-id",
                    ["Teams:MicrosoftAppPassword"] = "test-password",
                    ["Teams:MicrosoftAppTenantId"] = "tenant-a",
                    ["Teams:AllowedTenantIds:0"] = "tenant-a",
                    ["Teams:BotEndpoint"] = "https://bot.example/api/messages",
                };
                settings[$"Teams:{_omitField}"] = string.Empty;
                configBuilder.AddInMemoryCollection(settings);
            });
            return base.CreateHost(builder);
        }
    }
}
