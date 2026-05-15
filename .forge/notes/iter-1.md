# Iter notes — Stage 2.3 Outbound Message Sender (this iter)

## What this iter did
First-pass implementation of Stage 2.3 (Outbound Message Sender) per
`implementation-plan.md` lines 149–174. Prior iters in the archive
(iter-2..7) were about Stage 1.x / 2.1 / 2.2 doc & test alignment, not
Stage 2.3 production code, so this iter is implementation-from-scratch
for the sender + rate limiter + message-id tracker + tests.

## Files touched this iter

### New production files (under `src/AgentSwarm.Messaging.Telegram/`)
- `RateLimitOptions.cs` — POCO bound from `Telegram:RateLimits`
  (GlobalPerSecond=30, GlobalBurstCapacity=30, PerChatPerMinute=20,
  PerChatBurstCapacity=5) per architecture.md §10.4.
- `IDelayProvider.cs` + `TaskDelayProvider` — abstraction over
  `Task.Delay` so the 429 retry path and the rate-limiter refill loop
  are observable in tests (no real sleep).
- `ITelegramRateLimiter.cs` — single-method interface
  `AcquireAsync(chatId, ct)`.
- `TokenBucketRateLimiter.cs` — dual-bucket impl. Global bucket
  capacity = `GlobalBurstCapacity`, refill = `GlobalPerSecond / s`.
  Per-chat bucket capacity = `PerChatBurstCapacity`, refill =
  `PerChatPerMinute / 60` per second. On exhaustion, computes
  time-until-next-token and awaits `IDelayProvider.DelayAsync`. Uses
  `TimeProvider.GetTimestamp` / `GetElapsedTime` for refill math.
- `ITelegramApiClient.cs` (public) + `TelegramBotApiClient.cs` — thin
  wrapper around `ITelegramBotClient.SendMessage(...)` so tests can
  fake the static extension method. Made `public` to satisfy CS0051
  (consumed by public `TelegramMessageSender` ctor).
- `IMessageIdTracker.cs` + `InMemoryMessageIdTracker` — maps
  `CorrelationId → Telegram message id` via `ConcurrentDictionary` for
  Stage 3.x reply correlation.
- `MarkdownV2Escaper.cs` — escapes the 18 Telegram MarkdownV2 special
  chars (`_ * [ ] ( ) ~ \` > # + - = | { } . !`) plus backslash.
- `TelegramMessageSender.cs` — Stage 2.3 centrepiece. Implements
  `IMessageSender`; renders `AgentQuestion` with severity badge,
  expires-in countdown, default-action label, MarkdownV2-escaped body,
  inline keyboard, trace footer. Caches each `HumanAction` to
  `IDistributedCache` keyed `QuestionId:ActionId` with
  AbsoluteExpiration = `ExpiresAt + 5min`. Acquires rate-limiter token
  before each chunk. 429 retry uses `ApiRequestException.Parameters
  .RetryAfter`. Splits payloads > 4096 chars at paragraph/line
  boundaries, footer included per chunk.

### New test files
- `tests/AgentSwarm.Messaging.Tests/TelegramMessageSenderTests.cs`
  — 9 tests pinning all 8 implementation-plan Stage 2.3 scenarios +
  null-guard test. Uses `RecordingApiClient`, `RecordingDelayProvider`,
  `SyntheticDelayProvider`, `RecordingRateLimiter`, `StubTimeProvider`,
  `RecordingDistributedCache`.

### Modified production files
- `src/AgentSwarm.Messaging.Telegram/AgentSwarm.Messaging.Telegram
  .csproj` — added `Microsoft.Extensions.Caching.Abstractions` and
  `Microsoft.Extensions.Caching.Memory` package refs.
- `src/AgentSwarm.Messaging.Telegram/TelegramServiceCollectionExtensions
  .cs` — Stage 2.3 DI wiring: binds `RateLimitOptions`, adds
  `AddDistributedMemoryCache()`, registers `TimeProvider.System`,
  `IDelayProvider→TaskDelayProvider`,
  `ITelegramRateLimiter→TokenBucketRateLimiter`,
  `ITelegramApiClient→TelegramBotApiClient`,
  `IMessageIdTracker→InMemoryMessageIdTracker`, and the sender as
  both `TelegramMessageSender` + `IMessageSender` (same singleton).
- `src/AgentSwarm.Messaging.Telegram/TelegramBotClientFactory.cs` —
  fixed PRE-EXISTING build break: added `public const string
  HttpClientName = "Telegram.Bot"` (referenced by the DI extension at
  line 52 but never defined), changed ctor to
  `(IOptions<TelegramOptions>, IHttpClientFactory)` to match
  `TelegramOptionsTests` expectations, error message now reads
  `"Telegram:BotToken is not configured."` to satisfy the
  `*Telegram:BotToken*` wildcard in the existing test.
- `src/AgentSwarm.Messaging.Worker/appsettings.json` — added
  `Telegram:RateLimits` section with default values.

## Decisions made this iter
- **ITelegramApiClient is `public`.** Telegram.Bot exposes
  `SendMessage` as a static extension on `ITelegramBotClient`, which
  is unmockable. I introduced a public interface wrapper instead of
  internal. Internal would have raised CS0051 (inconsistent
  accessibility) once the public sender ctor took an `ITelegramApiClient`
  parameter. Making the interface public is the smaller blast radius.
- **Two delay-provider fakes (`RecordingDelayProvider`,
  `SyntheticDelayProvider`).** The 429 retry test needs delays to
  complete instantly so the test step doesn't block; the rate-limiter
  test needs delays to *advance the clock* so the bucket refills and
  the limiter loop can terminate. One fake can't satisfy both
  observable behaviours without a state-machine, so I split them.
- **`RecordingDistributedCache` for the cache-expiry test, not
  `MemoryDistributedCache`.** `MemoryDistributedCache` uses real wall
  clock for AbsoluteExpiration; my `StubTimeProvider` only drives the
  sender's "now" reading, not the cache's. A recording fake captures
  the `DistributedCacheEntryOptions` passed in and lets me assert
  `AbsoluteExpiration == ExpiresAt + 5min` directly, which is what
  the implementation-plan asks the test to verify.
- **`SendTextAsync(chatId, text, correlationId, ct)` overload only
  on the concrete sender, not on `IMessageSender`.** `IMessageSender`
  is fixed by Stage 1.4 and has the two-arg `SendTextAsync(chatId,
  text, ct)` signature. The connector + the chunk-splitter need to
  carry a `CorrelationId` through to the footer + message-id tracker,
  so the concrete class gets an extra overload while the cross-stage
  contract stays untouched.
- **Cache key format `QuestionId:ActionId`.** Stage 1.3's
  `CallbackDataValidation` already pins this format with no colon in
  either component; reusing the colon as the cache-key separator
  matches the wire callback_data format exactly.

## Dead ends tried this iter
- First try at the cache-expiry test used a real
  `MemoryDistributedCache` plus `StubTimeProvider.Advance(34min)`,
  expecting the cache to observe the advance. It did not — the cache
  uses its own wall clock. Switched to `RecordingDistributedCache`
  that captures the options object instead.
- First try had `RecordingDelayProvider` use a TCS to block the
  caller (so the rate-limiter test could await the limiter's
  block-wait). But the 429 retry test then hung waiting on the same
  TCS. Split into two fakes (above).
- First test build hit `BeLessOrEqualTo` (does not exist on
  `NumericAssertions<int>` in FluentAssertions 6.x) — switched to
  `BeLessThanOrEqualTo`.

## Open questions surfaced this iter
- None. The brief was specific about the 9 impl steps + 8 scenarios
  and the architecture refs (§5.2 cache grace, §10.3 timeout,
  §10.4 rate limit) covered all the math.

## Verification
- `dotnet build --nologo --verbosity minimal` → 0 errors, 0 warnings.
- `dotnet test --no-build --nologo --verbosity minimal` →
  185 passed / 0 failed (all suites, including the 9 new Stage 2.3
  tests and the 5 pre-existing `TelegramOptionsTests` that the
  factory-fixup unblocked).

## What's still left (Stage 2.4+)
- Stage 2.4: webhook endpoint + `InboundUpdate` persistence +
  `InboundRecoverySweep`.
- Stage 2.5: polling service + UsePolling/WebhookUrl mutual-exclusion
  validator.
- Stage 2.6: `TelegramMessengerConnector` glue implementing
  `IMessengerConnector` (binds the sender to the connector surface).
- Stage 3.3: `CallbackQueryHandler` reads the cached `HumanAction`
  from `IDistributedCache` keyed `QuestionId:ActionId` — the writes
  added this iter are the producer half of that contract.
- Stage 3.5: `PendingQuestionRecord.DefaultActionId` should be
  denormalised by the sender into the persistence record. The
  sender already reads `AgentQuestionEnvelope.ProposedDefaultActionId`
  and includes it in the rendered body; persisting it into
  `PendingQuestionRecord` is a Stage 3.5 concern (mapper) per the
  brief's "denormalize ... into PendingQuestionRecord.DefaultActionId
  (Stage 3.5) for efficient timeout polling" wording.
