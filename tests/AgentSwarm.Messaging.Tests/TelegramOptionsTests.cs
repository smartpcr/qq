using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        var options = Options.Create(new TelegramOptions { BotToken = string.Empty });
        var httpClientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = httpClientServices.GetRequiredService<IHttpClientFactory>();

        var factory = new TelegramBotClientFactory(options, httpClientFactory);

        var act = () => factory.Create();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telegram:BotToken*");
    }

    [Fact]
    public void Factory_Constructor_GuardsAgainstNullOptions()
    {
        var httpClientServices = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = httpClientServices.GetRequiredService<IHttpClientFactory>();

        var act = () => new TelegramBotClientFactory(null!, httpClientFactory);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Factory_Constructor_GuardsAgainstNullHttpClientFactory()
    {
        var options = Options.Create(new TelegramOptions { BotToken = SampleToken });

        var act = () => new TelegramBotClientFactory(options, null!);

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
}
