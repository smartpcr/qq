using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — webhook-mode validation rules added to
/// <see cref="TelegramOptionsValidator"/>. The Stage 2.1 tests in
/// <see cref="TelegramOptionsTests"/> cover the original BotToken
/// guard; this file pins the two new rules required by the
/// "Authentication" and "Security" rows of the story brief and by
/// architecture.md §7.1:
///   * Webhook mode requires <c>Telegram:SecretToken</c>.
///   * Webhook and polling are mutually exclusive.
/// </summary>
public class TelegramOptionsValidatorWebhookTests
{
    private const string SampleToken = "1234567890:AAH9hyTeleGramSecRetToken_test_value_only";

    [Fact]
    public void WebhookMode_WithoutSecretToken_Fails()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = false,
            SecretToken = string.Empty,
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Telegram:SecretToken");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WebhookMode_WithBlankSecretToken_Fails(string? secret)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = false,
            SecretToken = secret!,
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Telegram:SecretToken");
    }

    [Fact]
    public void WebhookMode_WithSecretToken_Succeeds()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = false,
            SecretToken = "webhook-shared-secret-32-chars-min",
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void PollingMode_NeverRequiresSecretToken()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = string.Empty,
            UsePolling = true,
            SecretToken = string.Empty,
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void WebhookAndPolling_BothEnabled_Fails()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = true,
            SecretToken = "any-secret",
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("mutually exclusive");
    }

    [Fact]
    public void NoReceiveMode_IsAllowed_ForUnitTestsAndCi()
    {
        // It is legitimate to register Telegram services with neither
        // webhook URL nor polling enabled — that's the shape used by
        // integration tests and CI smoke runs that only need the bot
        // client + pipeline without an actual receive loop. The
        // validator must not reject this shape.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = string.Empty,
            UsePolling = false,
            SecretToken = string.Empty,
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    // ============================================================
    // Iter-5 evaluator feedback item 2 — Telegram Bot API webhooks
    // are HTTPS-only. The validator must reject non-https schemes
    // and non-absolute URIs at host startup so the operator gets a
    // clear OptionsValidationException at boot rather than a
    // confusing 4xx from setWebhook later.
    // ============================================================

    [Theory]
    [InlineData("http://example.com/api/telegram/webhook")]
    [InlineData("HTTP://example.com/api/telegram/webhook")]
    [InlineData("ftp://example.com/api/telegram/webhook")]
    [InlineData("ws://example.com/api/telegram/webhook")]
    public void WebhookUrl_NonHttpsScheme_Fails(string nonHttpsUrl)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = nonHttpsUrl,
            UsePolling = false,
            SecretToken = "any-secret",
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "story brief Protocol row mandates HTTPS for Telegram Bot API; non-https webhook URLs must be rejected at startup");
        result.FailureMessage.Should().Contain("https");
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/api/telegram/webhook")]
    [InlineData("example.com/api/telegram/webhook")]
    public void WebhookUrl_NonAbsoluteOrInvalid_Fails(string badUrl)
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = badUrl,
            UsePolling = false,
            SecretToken = "any-secret",
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "Telegram requires an absolute callback URL; relative paths and non-URI strings must be rejected at startup");
        result.FailureMessage.Should().Contain("absolute");
    }

    [Fact]
    public void WebhookUrl_WithHttpsAbsoluteUri_Succeeds()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = false,
            SecretToken = "any-secret",
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void OperatorBindings_BlankTenantId_Fails()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            OperatorBindings = new List<TelegramOperatorBindingOptions>
            {
                new()
                {
                    TelegramUserId = 1,
                    TelegramChatId = 2,
                    TenantId = "",
                    WorkspaceId = "ws1",
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("TenantId");
    }

    [Fact]
    public void OperatorBindings_BlankWorkspaceId_Fails()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            OperatorBindings = new List<TelegramOperatorBindingOptions>
            {
                new()
                {
                    TelegramUserId = 1,
                    TelegramChatId = 2,
                    TenantId = "acme",
                    WorkspaceId = "  ",
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("WorkspaceId");
    }

    [Fact]
    public void OperatorBindings_AllValid_Succeeds()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UsePolling = true,
            OperatorBindings = new List<TelegramOperatorBindingOptions>
            {
                new()
                {
                    TelegramUserId = 1,
                    TelegramChatId = 2,
                    TenantId = "acme",
                    WorkspaceId = "ws1",
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void MultipleFailures_AllAppearInFailureMessage()
    {
        // Catch regressions where the validator short-circuits on the
        // first failure and hides downstream issues — the operator
        // should see every problem at once.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = string.Empty,
            WebhookUrl = "https://example.com/api/telegram/webhook",
            UsePolling = true,
            SecretToken = string.Empty,
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Telegram:BotToken");
        result.FailureMessage.Should().Contain("mutually exclusive");
    }
}
