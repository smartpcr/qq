using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Polling;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.5 — locks the DI registration shape of the long-polling
/// receiver:
///   1. <see cref="ITelegramUpdatePoller"/> is always registered.
///   2. <see cref="TelegramPollingService"/> is registered as an
///      <see cref="IHostedService"/> ONLY when
///      <see cref="TelegramOptions.UsePolling"/> is <c>true</c>.
///   3. The mutual-exclusion guard rejects polling + webhook at host
///      startup with <see cref="OptionsValidationException"/>.
/// </summary>
public class TelegramPollingRegistrationTests
{
    private const string SampleToken = "1234567890:AAH9hyTeleGramSecRetToken_test_value_only";

    [Fact]
    public void AddTelegram_AlwaysRegistersUpdatePoller()
    {
        var services = new ServiceCollection();
        services.AddTelegram(BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken,
        }));

        services.Should().Contain(d => d.ServiceType == typeof(ITelegramUpdatePoller),
            "the poller seam is registered regardless of UsePolling so tests/integration callers can resolve it");

        using var provider = services.BuildServiceProvider();
        provider.GetService<ITelegramUpdatePoller>().Should()
            .BeOfType<TelegramBotClientUpdatePoller>();
    }

    [Fact]
    public void AddTelegram_RegistersPollingService_WhenUsePollingTrue()
    {
        var services = new ServiceCollection();
        // IUserAuthorizationService is a Phase 4 concern intentionally not
        // registered by AddTelegram; resolving the IHostedService set
        // activates TelegramPollingService → ITelegramUpdatePipeline →
        // IUserAuthorizationService, so we wire a stub here.
        services.AddSingleton<AgentSwarm.Messaging.Core.IUserAuthorizationService, StubAuthorizationService>();
        services.AddTelegram(BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken,
            ["Telegram:UsePolling"] = "true",
        }));

        // Hosted services may be resolved via IEnumerable<IHostedService>.
        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.Should().Contain(h => h is TelegramPollingService,
            "Stage 2.5: TelegramPollingService must be added to the IHostedService set when UsePolling=true");
    }

    [Fact]
    public void AddTelegram_DoesNotRegisterPollingService_WhenUsePollingFalse()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AgentSwarm.Messaging.Core.IUserAuthorizationService, StubAuthorizationService>();
        services.AddTelegram(BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken,
            ["Telegram:UsePolling"] = "false",
        }));

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.Should().NotContain(h => h is TelegramPollingService,
            "TelegramPollingService must NOT be added when UsePolling=false (webhook mode)");
    }

    [Fact]
    public void AddTelegram_DoesNotRegisterPollingService_WhenSectionMissing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AgentSwarm.Messaging.Core.IUserAuthorizationService, StubAuthorizationService>();
        services.AddTelegram(BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken,
        }));

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.Should().NotContain(h => h is TelegramPollingService,
            "Default UsePolling is false — polling service must not be registered without explicit opt-in");
    }

    [Fact]
    public void AddTelegram_ThrowsAtRegistration_WhenPollingAndWebhookConflict()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken,
            ["Telegram:UsePolling"] = "true",
            ["Telegram:WebhookUrl"] = "https://bot.example.com/webhook",
        });

        var act = () => services.AddTelegram(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UsePolling*WebhookUrl*",
                "the DI extension refuses to register the polling service when WebhookUrl is set; the validator surface enforces the same guard at startup");
    }

    [Fact]
    public async Task HostStartAsync_Throws_WhenPollingAndWebhookConflict()
    {
        // The DI extension throws at registration; the validator path
        // surfaces the same conflict at host startup if the conditional
        // registration was somehow skipped. Both paths matter.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            WebhookUrl = "https://bot.example.com/webhook",
        };

        var result = validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("UsePolling");
        result.FailureMessage.Should().Contain("WebhookUrl");

        // End-to-end: a host built with the explicit conflict must fail
        // at StartAsync via OptionsValidationException. We bypass
        // AddTelegram (which itself throws) and exercise the options
        // validator directly via AddOptions/.ValidateOnStart.
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddOptions<TelegramOptions>()
            .Configure(o =>
            {
                o.BotToken = SampleToken;
                o.UsePolling = true;
                o.WebhookUrl = "https://bot.example.com/webhook";
            })
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();
        using var host = builder.Build();

        var act = async () => await host.StartAsync();

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.OptionsType.Should().Be(typeof(TelegramOptions));
        ex.Which.Message.Should().Contain("UsePolling");
        ex.Which.Message.Should().Contain("WebhookUrl");
    }

    [Fact]
    public void Validator_AcceptsPollingWithoutWebhook()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            WebhookUrl = null,
            PollingTimeoutSeconds = 30,
        };

        validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_AcceptsWebhookWithoutPolling()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = false,
            WebhookUrl = "https://example.com/wh",
            // SecretToken is required whenever WebhookUrl is set —
            // architecture.md §11.3: the X-Telegram-Bot-Api-Secret-Token
            // header is the only authentication on the public webhook
            // endpoint, so the validator rejects webhook mode without
            // it. This test pins the "polling-disabled does not block
            // webhook acceptance" decision, so we satisfy the secret
            // requirement explicitly rather than weaken the validator.
            SecretToken = "valid-webhook-secret-token-value",
        };

        validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options)
            .Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(100)]
    public void Validator_RejectsInvalidPollingTimeout(int timeout)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            PollingTimeoutSeconds = timeout,
        };

        var result = validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("PollingTimeoutSeconds");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(50)]
    public void Validator_AcceptsValidPollingTimeout(int timeout)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            PollingTimeoutSeconds = timeout,
        };

        validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_IgnoresPollingTimeout_WhenPollingDisabled()
    {
        // Misconfigured PollingTimeoutSeconds is benign when UsePolling=false
        // because the polling service never starts; we don't want webhook-only
        // deployments to fail to start because of an unused field.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = false,
            PollingTimeoutSeconds = 0,
        };

        validator.Validate(Microsoft.Extensions.Options.Options.DefaultName, options)
            .Succeeded.Should().BeTrue();
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class StubAuthorizationService : AgentSwarm.Messaging.Core.IUserAuthorizationService
    {
        public Task<AgentSwarm.Messaging.Core.AuthorizationResult> AuthorizeAsync(
            string externalUserId,
            string chatId,
            string? commandName,
            CancellationToken ct)
        {
            return Task.FromResult(new AgentSwarm.Messaging.Core.AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "registration test stub",
            });
        }
    }
}
