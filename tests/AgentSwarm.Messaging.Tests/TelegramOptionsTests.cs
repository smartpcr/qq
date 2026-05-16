using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.1 — Telegram Bot Client Wrapper.
///
/// Locks the externally observable behavior of <see cref="TelegramOptions"/>,
/// <see cref="TelegramOptionsValidator"/>,
/// <see cref="TelegramBotClientFactory"/>, and the
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> wiring.
/// The two scenarios called out by the implementation-plan brief
/// (missing token fails fast at host start; <c>ToString</c> never reveals
/// the token) are covered as named tests; additional coverage pins the
/// configuration binding surface and the DI registration shape that
/// later stages (2.2 pipeline, 2.3 sender, 2.4 webhook) depend on.
/// </summary>
public class TelegramOptionsTests
{
    private const string SampleToken = "1234567890:AAH9hyTeleGramSecRetToken_test_value_only";

    // ============================================================
    // ToString() redaction — story-brief scenario
    // ============================================================

    [Fact]
    public void ToString_RedactsBotToken_AndDoesNotLeakTheValue()
    {
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/webhook",
            UsePolling = false,
            AllowedUserIds = new List<long> { 1, 2, 3 },
            SecretToken = "shared-secret"
        };

        var text = options.ToString();

        text.Should().Contain("[REDACTED]", "BotToken must be redacted");
        text.Should().NotContain(SampleToken, "the raw bot token must never appear in ToString output");
        text.Should().NotContain("shared-secret", "SecretToken is also a credential and must be redacted");
        text.Should().Contain("WebhookUrl = https://example.com/webhook");
        text.Should().Contain("UsePolling = False");
        text.Should().Contain("3 ids");
    }

    [Fact]
    public void ToString_MarksMissingBotTokenAsNotSet_NotRedacted()
    {
        var options = new TelegramOptions { BotToken = string.Empty };

        var text = options.ToString();

        text.Should().Contain("BotToken = [NOT SET]");
        text.Should().NotContain("BotToken = [REDACTED]");
    }

    [Fact]
    public void ToString_IsSafe_WhenAllowedUserIdsIsNull()
    {
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            AllowedUserIds = null!
        };

        var act = () => options.ToString();

        act.Should().NotThrow();
        options.ToString().Should().Contain("0 ids");
    }

    [Fact]
    public void ToString_IncludesOperatorBindingCount_ButNotBindingPayload()
    {
        // Iter-7 — operator startup-log fidelity. The previous ToString
        // omitted OperatorBindings entirely, so a misconfigured host
        // (zero bindings) was indistinguishable from a correctly-bound
        // one in the startup banner. The count surfaces the binding-
        // shape WITHOUT echoing the per-binding chat IDs (which, while
        // not credentials, are operator PII we deliberately keep out
        // of every log sink).
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            OperatorBindings = new List<TelegramOperatorBindingOptions>
            {
                new() { TelegramUserId = 11111, TelegramChatId = 22222, TenantId = "tenant-a", WorkspaceId = "ws-1" },
                new() { TelegramUserId = 33333, TelegramChatId = 44444, TenantId = "tenant-b", WorkspaceId = "ws-2" },
                new() { TelegramUserId = 55555, TelegramChatId = 66666, TenantId = "tenant-c", WorkspaceId = "ws-3" },
            }
        };

        var text = options.ToString();

        text.Should().Contain("OperatorBindings = [3 bindings]",
            "the count of configured (user, chat, tenant, workspace) bindings is the load-bearing fact for the startup banner");
        text.Should().NotContain("11111", "individual user/chat IDs must not leak into ToString output");
        text.Should().NotContain("22222", "individual user/chat IDs must not leak into ToString output");
        text.Should().NotContain("tenant-a", "binding tenant ids must not leak into ToString output");
        text.Should().NotContain("ws-1", "binding workspace ids must not leak into ToString output");
    }

    [Fact]
    public void ToString_IsSafe_WhenOperatorBindingsIsNull()
    {
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            OperatorBindings = null!
        };

        var act = () => options.ToString();

        act.Should().NotThrow();
        options.ToString().Should().Contain("[0 bindings]",
            "a null bindings list must render as zero, not throw, so the startup-log path stays safe even on misconfigured hosts");
    }

    [Fact]
    public void ToString_IncludesRateLimitEnvelope_ForOperatorVisibility()
    {
        // Iter-7 — the dual-bucket rate-limit envelope (architecture
        // §10.4) drives the P95 send-latency SLO. A misconfigured
        // operator who lowers GlobalPerSecond from 30 to 3 silently
        // collapses the throughput ceiling by 10×; surfacing the
        // envelope in the startup log lets the operator catch this
        // BEFORE a burst alert exposes the bottleneck via timeouts.
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            RateLimits = new RateLimitOptions
            {
                GlobalPerSecond = 25,
                GlobalBurstCapacity = 50,
                PerChatPerMinute = 15,
                PerChatBurstCapacity = 7
            }
        };

        var text = options.ToString();

        text.Should().Contain("GlobalPerSecond = 25");
        text.Should().Contain("GlobalBurstCapacity = 50");
        text.Should().Contain("PerChatPerMinute = 15");
        text.Should().Contain("PerChatBurstCapacity = 7");
    }

    [Fact]
    public void ToString_IsSafe_WhenRateLimitsIsNull()
    {
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            RateLimits = null!
        };

        var act = () => options.ToString();

        act.Should().NotThrow();
        // Falls back to defaults so the startup log still shows a
        // sensible envelope rather than "null" / "0" everywhere.
        var text = options.ToString();
        text.Should().Contain("GlobalPerSecond = 30", "the default 30 msg/s envelope must surface when RateLimits is null");
        text.Should().Contain("PerChatPerMinute = 20", "the default 20 msg/min per-chat envelope must surface when RateLimits is null");
    }

    // Hardens the story-brief "token never logged" acceptance criterion
    // against any plausible Telegram bot-token shape: real tokens follow
    // <bot_id>:<35-char-secret> but BotFather has rotated formats over the
    // years (longer secrets, alphanumerics with - and _), and a regression
    // here would re-introduce credential leakage. Each Theory row asserts
    // that ToString() (a) never echoes the verbatim token, (b) never
    // echoes the secret portion after the ':' on its own (except when the
    // secret collides with a legitimate redaction marker — see below),
    // and (c) always emits the [REDACTED] marker so log readers can tell
    // the field was set vs. [NOT SET]. The two adversarial rows guard
    // against the unlikely-but-pathological case where a token's secret
    // portion happens to match a redaction marker string; those rows
    // skip the secret-portion-absent assertion because the marker
    // legitimately appears in the BotToken slot of the output.
    [Theory]
    [InlineData("1234567890:AAH9hyTeleGramSecRetToken_test_value_only")]
    [InlineData("7654321:short_but_valid_secret_99")] // shorter realistic shape
    [InlineData("999999999999:" +
                "AaBbCcDdEeFfGgHhIiJjKkLlMmNnOoPpQqRrSsTtUuVvWwXxYyZz_-1234567890")]
    [InlineData("42:[REDACTED]")] // adversarial: secret portion matches the redaction marker
    [InlineData("42:[NOT SET]")] // adversarial: secret portion matches the not-set marker
    public void ToString_NeverLeaksBotToken_AcrossRealisticTokenFormats(string token)
    {
        var options = new TelegramOptions { BotToken = token };

        var text = options.ToString();

        text.Should().NotContain(token,
            "the verbatim bot token must never appear in ToString output");
        text.Should().Contain("BotToken = [REDACTED]",
            "the redaction marker must be emitted so structured logs show the field was set");

        var colonIndex = token.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < token.Length - 1)
        {
            var secretPortion = token[(colonIndex + 1)..];
            // When the secret itself happens to match a marker, the
            // output legitimately contains that marker in the BotToken
            // slot, so the secret-portion-absent check would false-
            // positive. The verbatim-token-absent assertion above is
            // still enforced, which is the strongest guarantee.
            if (secretPortion is not "[REDACTED]" and not "[NOT SET]")
            {
                text.Should().NotContain(secretPortion,
                    "the secret portion of the token must never appear in ToString output");
            }
        }
    }

    // ============================================================
    // Validator — story-brief scenario (missing token fails fast)
    // ============================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Validator_FailsWhenBotTokenIsMissingOrWhitespace(string? token)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions { BotToken = token! };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Telegram:BotToken");
    }

    [Fact]
    public void Validator_SucceedsWhenBotTokenIsSet()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions { BotToken = SampleToken };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Host_StartAsync_ThrowsOptionsValidationException_WhenBotTokenIsMissing()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = string.Empty
        });

        var act = async () => await host.StartAsync();

        var ex = await act.Should().ThrowAsync<OptionsValidationException>();
        ex.Which.OptionsType.Should().Be(typeof(TelegramOptions));
        ex.Which.Message.Should().Contain("Telegram:BotToken");
    }

    [Fact]
    public async Task Host_StartAsync_Succeeds_WhenBotTokenIsConfigured()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken
        });

        await host.StartAsync();
        await host.StopAsync();
    }

    // ============================================================
    // Configuration binding — covers WebhookUrl, UsePolling,
    // AllowedUserIds, SecretToken
    // ============================================================

    [Fact]
    public void AddTelegram_BindsAllConfiguredFields()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = SampleToken,
                ["Telegram:WebhookUrl"] = "https://bot.example.com/api/telegram/webhook",
                ["Telegram:UsePolling"] = "false",
                ["Telegram:SecretToken"] = "webhook-shared-secret",
                ["Telegram:AllowedUserIds:0"] = "1001",
                ["Telegram:AllowedUserIds:1"] = "1002",
                ["Telegram:AllowedUserIds:2"] = "1003"
            })
            .Build();

        services.AddTelegram(config);
        using var provider = services.BuildServiceProvider();

        var bound = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;

        bound.BotToken.Should().Be(SampleToken);
        bound.WebhookUrl.Should().Be("https://bot.example.com/api/telegram/webhook");
        bound.UsePolling.Should().BeFalse();
        bound.SecretToken.Should().Be("webhook-shared-secret");
        bound.AllowedUserIds.Should().BeEquivalentTo(new[] { 1001L, 1002L, 1003L });
    }

    // ============================================================
    // DI registration shape — pins what Stage 2.2/2.3/2.4 mock
    // ============================================================

    [Fact]
    public void AddTelegram_RegistersExpectedServices()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken
        });

        services.AddTelegram(config);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IOptions<TelegramOptions>>().Should().NotBeNull();
        provider.GetService<IValidateOptions<TelegramOptions>>().Should().BeOfType<TelegramOptionsValidator>();
        provider.GetService<TelegramBotClientFactory>().Should().NotBeNull();
        provider.GetService<IHttpClientFactory>().Should().NotBeNull();
        provider.GetService<ITelegramBotClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddTelegram_RegistersTelegramBotClientAsSingleton()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken
        });

        services.AddTelegram(config);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ITelegramBotClient>();
        var second = provider.GetRequiredService<ITelegramBotClient>();

        first.Should().BeSameAs(second, "ITelegramBotClient must be singleton — the underlying HttpClient is pooled");
    }

    [Fact]
    public void AddTelegram_ThrowsWhenServicesIsNull()
    {
        IServiceCollection services = null!;
        var config = BuildConfig(new Dictionary<string, string?>());

        var act = () => services.AddTelegram(config);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddTelegram_ThrowsWhenConfigurationIsNull()
    {
        var services = new ServiceCollection();

        var act = () => services.AddTelegram(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ============================================================
    // TelegramBotClientFactory
    // ============================================================

    [Fact]
    public void Factory_CreatesNonNullClient_WhenTokenIsSet()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = SampleToken
        });
        services.AddTelegram(config);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<TelegramBotClientFactory>();
        var client = factory.Create();

        client.Should().NotBeNull();
        client.Should().BeAssignableTo<ITelegramBotClient>();
    }

    [Fact]
    public void Factory_ThrowsInvalidOperation_WhenTokenIsMissing()
    {
        // Bypass the AddTelegram validator wiring to exercise the
        // factory's defensive guard directly.
        var monitor = BuildOptionsMonitor(new TelegramOptions { BotToken = string.Empty });
        var httpClientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = httpClientServices.GetRequiredService<IHttpClientFactory>();

        var factory = new TelegramBotClientFactory(
            monitor,
            NullLogger<TelegramBotClientFactory>.Instance,
            httpClientFactory);

        var act = () => factory.Create();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BotToken*");
    }

    [Fact]
    public void Factory_Constructor_GuardsAgainstNullOptions()
    {
        var httpClientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = httpClientServices.GetRequiredService<IHttpClientFactory>();

        var act = () => new TelegramBotClientFactory(
            null!,
            NullLogger<TelegramBotClientFactory>.Instance,
            httpClientFactory);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Factory_Constructor_GuardsAgainstNullLogger()
    {
        var monitor = BuildOptionsMonitor(new TelegramOptions { BotToken = SampleToken });

        var act = () => new TelegramBotClientFactory(monitor, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHost BuildHost(Dictionary<string, string?> telegramSection)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(telegramSection);
        builder.Services.AddTelegram(builder.Configuration);
        return builder.Build();
    }

    private static IOptionsMonitor<TelegramOptions> BuildOptionsMonitor(TelegramOptions current)
    {
        var services = new ServiceCollection();
        services.AddOptions<TelegramOptions>().Configure(o =>
        {
            o.BotToken = current.BotToken;
            o.WebhookUrl = current.WebhookUrl;
            o.UsePolling = current.UsePolling;
            o.SecretToken = current.SecretToken;
            o.AllowedUserIds = current.AllowedUserIds;
        });
        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<TelegramOptions>>();
    }
}
