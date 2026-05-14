# Iter notes — Stage 2.1 Telegram Bot Client Wrapper (iter 8, first impl)

## Files touched this iter
- `src/AgentSwarm.Messaging.Telegram/TelegramOptions.cs` (NEW) — POCO
  with `BotToken`, `WebhookUrl`, `UsePolling`, `AllowedUserIds`
  (`List<long>`), `SecretToken`; `ToString()` emits `[REDACTED]` for both
  secret fields, `[NOT SET]` when empty, null-safe `AllowedUserIds.Count`.
- `src/AgentSwarm.Messaging.Telegram/TelegramOptionsValidator.cs` (NEW)
  — `internal sealed IValidateOptions<TelegramOptions>`; rejects
  null/empty/whitespace `BotToken` with explanatory message naming
  Key Vault / env / user-secrets.
- `src/AgentSwarm.Messaging.Telegram/TelegramBotClientFactory.cs` (NEW)
  — uses `IOptions<TelegramOptions>` (not `IOptionsMonitor`, per rubber-
  duck #2 — singleton client cannot honour hot reload). Pulls named
  `HttpClient` via `IHttpClientFactory` so later stages can chain Polly.
  Defensive `InvalidOperationException` if token missing.
- `src/AgentSwarm.Messaging.Telegram/TelegramServiceCollectionExtensions.cs`
  (NEW) — `AddTelegram(IServiceCollection, IConfiguration)` registers
  options + `ValidateOnStart()`, validator, `AddHttpClient`, factory
  singleton, `ITelegramBotClient` singleton via factory; null-guards on
  both args.
- `src/AgentSwarm.Messaging.Telegram/AgentSwarm.Messaging.Telegram.csproj`
  — added `Microsoft.Extensions.Hosting`, `Http`,
  `Options.ConfigurationExtensions`, plus `InternalsVisibleTo` for the
  test project (validator stays internal).
- `src/AgentSwarm.Messaging.Worker/Program.cs` — call `AddTelegram`.
- `src/AgentSwarm.Messaging.Worker/appsettings.json` — empty
  `Telegram` section (intentionally empty `BotToken` so dev startup
  fails fast unless token is supplied via secrets/env, per brief).
- `src/AgentSwarm.Messaging.Worker/appsettings.Development.json` —
  `UsePolling: true` for local dev.
- `tests/AgentSwarm.Messaging.Tests/TelegramOptionsTests.cs` (NEW) —
  14 tests covering ToString redaction (incl. null-safe path), validator
  pass/fail (incl. whitespace `[Theory]`), `Host.StartAsync` fail-fast,
  config-binding coverage of all five fields, DI registration shape,
  singleton lifetime, ctor null-guards on both args.
- `src/AgentSwarm.Messaging.Core/OutboundMessage.cs` — STUBBED to remove
  duplicate types. This file was a leftover from Stage 1.4's "relocate to
  Abstractions" (per implementation-plan §1.2 line 48 + Abstractions
  XML doc). The duplicate definitions of `OutboundMessage` +
  `OutboundSourceType` + `OutboundMessageStatus` caused CS0104 in
  `tests/...OutboundContractTests.cs:462`, breaking the build gate on
  arrival. File reduced to one-line `namespace AgentSwarm.Messaging.Core;`
  with a NOTE comment explaining the relocation; no source file removed.

## Decisions made this iter
- **Fixed inherited build break rather than blocking on it.** The
  duplicate `OutboundMessage` was an unambiguous leftover (docs +
  XML comment both point at Abstractions as canonical) and the build
  gate would have rejected my workstream regardless. Stubbing the Core
  file (no deletion) keeps the change reversible and surgical, and the
  rubber-duck agreed the file-stub approach is correct.
- **`IOptions<TelegramOptions>` in factory, not `IOptionsMonitor`** —
  rubber-duck #2: an `IOptionsMonitor` would be misleading because the
  `ITelegramBotClient` is a singleton, so a hot-rotated token never
  reaches a new client instance. Stage 2.1 brief asks for "configured
  client", not rotation.
- **`List<long>` for `AllowedUserIds`** (rubber-duck #3) — concrete
  collection binds predictably; downstream stages copy to `HashSet`
  where lookup speed matters.
- **`InternalsVisibleTo` for validator** — keeps it `internal sealed`
  (it's an implementation detail) while letting tests exercise the
  validator's behavior directly via `[Theory]`. The DI surface is also
  asserted (`IValidateOptions<TelegramOptions>` resolves to the
  concrete type), pinning the wiring as well.
- **Validator rejects whitespace, not just null/empty.** Brief says
  "rejects empty/null tokens"; whitespace is operationally equivalent
  to empty (Telegram refuses the request with the same auth error).
  Defensive widening is cheap and the test theory covers all four
  shapes.
- **`Host.StartAsync()` assertion** (rubber-duck #5) — earlier draft
  considered `IOptions<T>.Value` accessor, which only proves lazy
  validation; the brief says "host startup fails". `StartAsync()` is
  what `ValidateOnStart()` actually wires into.
- **`HttpClient` via `IHttpClientFactory`, not directly constructed**
  — gives Stage 2.3 a clean hook (`AddHttpClient(name).AddPolicyHandler(…)`)
  without touching this factory.

## Dead ends tried this iter
- First test build failed with CS0122 — validator was `internal` but I
  wrote unit-test `new TelegramOptionsValidator()` directly. Fixed by
  adding `InternalsVisibleTo` to the Telegram csproj rather than
  weakening the validator visibility.

## Open questions surfaced this iter
- None blocking. The brief's "target files" list mentions
  `tests/AgentSwarm.Messaging.IntegrationTests/...csproj` and
  `docker-compose.yml`, but neither exists in the repo and neither is
  needed by the Stage 2.1 implementation-plan steps. Treated them as
  forward-looking placeholders (rubber-duck confirmed).

## What's still left (Stage 2.2+)
- Stage 2.2: `TelegramUpdatePipeline` implementing
  `ITelegramUpdatePipeline` with dedup/auth/parse/route stages.
- Stage 2.3: `TelegramMessageSender` (text + question rendering, inline
  keyboards, rate limiter, message splitting).
- Stage 2.4: Webhook endpoint + `InboundUpdate` persistence +
  `InboundRecoverySweep`.
- Stage 2.5: Polling service + UsePolling/WebhookUrl mutual-exclusion
  validator.
- Stage 2.6: `TelegramMessengerConnector` glue implementing
  `IMessengerConnector`.
