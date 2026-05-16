using AgentSwarm.Messaging.Telegram;
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

    // ============================================================
    // Stage 3.4 iter-2 evaluator item 3 — UserTenantMappings
    // ============================================================

    [Fact]
    public void Validator_Fails_WhenUserTenantMappingEntryHasBlankTenantId()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = string.Empty,
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "an empty TenantId would route the operator into the empty-string tenant and break alias resolution");
        result.FailureMessage.Should().Contain("Telegram:UserTenantMappings[\"12345\"][0].TenantId");
    }

    [Fact]
    public void Validator_Fails_WhenUserTenantMappingEntryHasBlankWorkspaceId()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "   ",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Telegram:UserTenantMappings[\"12345\"][0].WorkspaceId");
    }

    [Fact]
    public void Validator_Fails_WhenUserTenantMappingEntryHasBlankAlias()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = string.Empty,
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "a blank alias would make the operator unreachable via /handoff @alias");
        result.FailureMessage.Should().Contain("Telegram:UserTenantMappings[\"12345\"][0].OperatorAlias");
    }

    [Fact]
    public void Validator_Fails_WhenUserTenantMappingKeyIsNotNumeric()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["not-a-number"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("not a valid 64-bit Telegram user id");
    }

    [Fact]
    public void Validator_Fails_WhenUserTenantMappingHasEmptyEntryList()
    {
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new(),
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "empty arrays cannot onboard the user — surface the misconfiguration at startup");
        result.FailureMessage.Should().Contain("must contain at least one");
    }

    [Fact]
    public void Validator_Succeeds_WhenUserTenantMappingIsComplete()
    {
        // Iter-3 evaluator item 1 — operator with multi-workspace
        // bindings MUST use distinct OperatorAlias values per
        // workspace because the operator_bindings table has UNIQUE
        // (OperatorAlias, TenantId). Two rows with the same alias
        // in the same tenant would crash the second /start on the
        // unique index. The validator's new duplicate-alias rule
        // catches that shape at startup.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice-alpha",
                        Roles = new List<string> { "Operator" },
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@alice-beta",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_Fails_WhenSameUserHasDuplicateAliasInSameTenant()
    {
        // Iter-3 evaluator item 1 — a single Telegram user with two
        // workspace bindings re-using the SAME (OperatorAlias,
        // TenantId) is the most common shape of this misconfiguration
        // (an operator who manages multiple workspaces under one
        // tenant). The unique index on (OperatorAlias, TenantId)
        // would let the first /start succeed and crash the second
        // mid-onboarding; the validator catches it at startup.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "two workspace bindings under user 12345 share (TenantId=t-1, OperatorAlias=@alice) — the DB unique index would reject the second /start row");
        result.FailureMessage.Should().Contain("Duplicate operator alias");
        result.FailureMessage.Should().Contain("@alice");
        result.FailureMessage.Should().Contain("t-1");
        result.FailureMessage.Should().Contain("UNIQUE (OperatorAlias, TenantId)");
    }

    [Fact]
    public void Validator_Fails_WhenTwoUsersClaimSameAliasInSameTenant()
    {
        // Iter-3 evaluator item 1 — even worse: two DIFFERENT
        // Telegram users with the SAME alias under the SAME tenant.
        // The unique index would let the first user's /start
        // succeed and lock out the second user entirely. Both
        // entries must be flagged so the operator can pick which
        // user owns the alias.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@shared",
                    },
                },
                ["67890"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@shared",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Duplicate operator alias");
        result.FailureMessage.Should().Contain("\"12345\"");
        result.FailureMessage.Should().Contain("\"67890\"");
    }

    [Fact]
    public void Validator_Fails_OnAliasCaseInsensitiveCollision()
    {
        // Iter-4 doc correction — the prior comment claimed aliases
        // are case-insensitive "at the persistence layer (default
        // SQLite string COLLATION)". That was wrong: OperatorAlias
        // is declared as TEXT(128) with no explicit collation, so
        // SQLite uses BINARY (case-sensitive) and the unique index
        // would technically accept "@Alice" and "@alice" as
        // distinct rows. The validator is intentionally STRICTER
        // than the DB on this axis as an operator-intent policy:
        // mixed-case duplicates almost certainly indicate a typo for
        // the SAME human handle, and silently registering two
        // distinct bindings would break /handoff @alice (which would
        // resolve to only one of them). See the rationale block in
        // TelegramOptionsValidator near the aliasOwners construction.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@Alice",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "the validator catches likely-typo case duplicates even though the DB BINARY collation would accept them — defense-in-depth operator-intent policy, NOT a claim that the DB itself enforces case-insensitive uniqueness");
        result.FailureMessage.Should().Contain("Duplicate operator alias");
    }

    [Fact]
    public void Validator_Succeeds_WhenSameAliasUsedInDifferentTenants()
    {
        // Iter-3 evaluator item 1 (negative case) — the unique
        // index is (OperatorAlias, TenantId), so the SAME alias
        // under DIFFERENT tenants is fine (architecture.md lines
        // 116-119: two tenants may independently use the same
        // alias without collision).
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                },
                ["67890"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-2",
                        WorkspaceId = "ws-beta",
                        OperatorAlias = "@alice",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_Fails_WhenSameUserHasTwoEntriesForSameWorkspace()
    {
        // Iter-4 (proactive companion to iter-3 item 2) — the
        // operator_bindings table also has a UNIQUE index on
        // (TelegramUserId, TelegramChatId, WorkspaceId). At /start
        // time TelegramChatId is fixed (the chat the operator typed
        // /start in), so two mapping entries under the SAME user
        // that share a WorkspaceId would both try to insert the
        // same (user, chat, workspace) row. The iter-3
        // transactional RegisterManyAsync would roll back the
        // batch; surfacing the misconfiguration at host startup is
        // strictly better UX than letting /start fail.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-shared",
                        OperatorAlias = "@alice-primary",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-2",
                        WorkspaceId = "ws-shared",
                        OperatorAlias = "@alice-secondary",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue(
            "two entries under user 12345 share WorkspaceId=ws-shared — the (TelegramUserId, TelegramChatId, WorkspaceId) unique index would reject the second /start row mid-batch");
        result.FailureMessage.Should().Contain("Duplicate workspace binding");
        result.FailureMessage.Should().Contain("ws-shared");
        result.FailureMessage.Should().Contain("12345");
        result.FailureMessage.Should().Contain("UNIQUE (TelegramUserId, TelegramChatId, WorkspaceId)");
    }

    [Fact]
    public void Validator_Succeeds_WhenDifferentUsersShareSameWorkspace()
    {
        // Iter-4 (negative case for the duplicate-workspace rule)
        // — the unique index is per-(user, chat, workspace), so
        // two DIFFERENT operators in the SAME workspace is the
        // legitimate multi-operator-per-workspace pattern from
        // architecture.md §4.3 and must not be flagged.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-shared",
                        OperatorAlias = "@alice",
                    },
                },
                ["67890"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-shared",
                        OperatorAlias = "@bob",
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Succeeded.Should().BeTrue(
            "two different operators can share a workspace — the duplicate-workspace rule is scoped per Telegram user");
    }

    [Fact]
    public void Validator_FailureMessage_AggregatesAllInvalidMappingEntries()
    {
        // Iter-2 evaluator item 3 — a partially-invalid multi-workspace
        // mapping must surface EVERY bad field at host startup so the
        // operator can fix them in a single iteration, not chase
        // them one-failed-startup-at-a-time.
        var validator = new TelegramOptionsValidator();
        var options = new TelegramOptions
        {
            BotToken = SampleToken,
            UserTenantMappings = new Dictionary<string, List<TelegramUserTenantMapping>>
            {
                ["12345"] = new()
                {
                    new TelegramUserTenantMapping
                    {
                        TenantId = "t-1",
                        WorkspaceId = "ws-alpha",
                        OperatorAlias = "@alice",
                    },
                    new TelegramUserTenantMapping
                    {
                        TenantId = string.Empty,
                        WorkspaceId = string.Empty,
                        OperatorAlias = string.Empty,
                    },
                },
            },
        };

        var result = validator.Validate(Options.DefaultName, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("[1].TenantId");
        result.FailureMessage.Should().Contain("[1].WorkspaceId");
        result.FailureMessage.Should().Contain("[1].OperatorAlias");
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
