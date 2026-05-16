# Agent Swarm — Telegram Messenger Support: User Stories

> Operator-uploaded reference brief mirrored into this worktree so the
> Stage 2.3 (Outbound Message Sender) implementation is reproducible
> from the worktree alone. The canonical merged plan artefacts live
> under `docs/stories/qq-TELEGRAM-MESSENGER-S/` — read those for the
> full architecture / tech-spec / implementation-plan / e2e-scenario
> tree.

---

## Story (canonical)

**Title.** Telegram Messenger Support

**As a** mobile operator,
**I want** to interact with the agent swarm through Telegram,
**so that** I can start tasks, answer agent questions, approve
actions, and receive urgent alerts from my phone.

Telegram Bot API is HTTP-based and supports bot messaging through the
official Bot API. For C#, the implementation uses **Telegram.Bot**
(broad practical .NET coverage) with **Telegram.BotAPI** considered as
an alternative if the latest Bot API surface area is needed.

---

## Functional requirements

| Area              | Requirement                                                                                                                                             |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Protocol          | Use Telegram Bot API over HTTPS.                                                                                                                        |
| Receive mode      | Support webhook in production; allow long polling for local/dev environments.                                                                           |
| C# library        | Prefer Telegram.Bot; alternatively Telegram.BotAPI if full/latest Bot API surface is required.                                                          |
| Authentication    | Store bot token in Key Vault or equivalent secret store. Never log token.                                                                               |
| Commands          | Support `/start`, `/status`, `/agents`, `/ask`, `/approve`, `/reject`, `/handoff`, `/pause`, `/resume`.                                                 |
| Agent routing     | Map Telegram chat ID to authorized human operator and tenant/workspace.                                                                                 |
| Question handling | Agent questions must include context, severity, timeout, proposed default action, and buttons where supported.                                          |
| Reliability       | Use durable outbound message queue with retry, deduplication, and dead-letter queue.                                                                    |
| Performance       | P95 send latency under 2 seconds after event is queued; support burst alerts from 100+ agents without message loss.                                     |
| Security          | Validate chat/user allowlist before accepting commands.                                                                                                 |
| Audit             | Persist every human response with message ID, user ID, agent ID, timestamp, and correlation ID.                                                         |

---

## Acceptance criteria (story-wide)

1. Human can send `/ask build release notes for Solution12` and the swarm creates a work item.
2. Agent can ask a blocking question and the Telegram user can answer from mobile.
3. Approval / rejection buttons are converted into strongly typed agent events.
4. Duplicate webhook delivery does not execute the same human command twice.
5. If Telegram send fails, the message is retried and eventually dead-lettered with alert.
6. All messages include a trace / correlation ID.

---

## Workstream focus — Outbound Message Sender (Stage 2.3, 10 steps)

The workstream `ws-qq-telegram-messenger-s-phase-telegram-bot-integration-stage-outbound-message-sender` lands the production
`IMessageSender` implementation for Telegram, plus its supporting
rate limiter, MarkdownV2 escaper, question renderer, and integration
test fixtures. It does **not** own the outbox processor (Stage 4.1)
or the connector that constructs `Alert` / `StatusUpdate` /
`CommandAck` payloads (Stage 3.x). The boundary contract with those
upstream/downstream owners is:

| Boundary                                        | Owner stage           | This workstream's responsibility                                                              |
| ----------------------------------------------- | --------------------- | --------------------------------------------------------------------------------------------- |
| Pre-rendered MarkdownV2 body (Alert / status)   | Connector (Stage 3.x) | Pass through unchanged; **do not re-escape** (would double-escape every backslash).           |
| Question body (rendered from `AgentQuestion`)   | This workstream       | Owns rendering + escape — `TelegramQuestionRenderer.BuildBody` is the sole escape path here.  |
| `HumanAction` callback cache                    | This workstream       | Write before send so a callback arriving in the post-send window resolves.                    |
| Telegram `message_id` → `CorrelationId` mapping | This workstream       | Cache index until Stage 4.1's `OutboundQueueProcessor` lands a durable mapping.               |
| Outbound queue persistence + DLQ                | Stage 4.1             | Out of scope here; this workstream's sender returns `SendResult.TelegramMessageId` upstream.  |
| Trace footer (per-message correlation)          | Shared                | Sender auto-appends from `Activity.Current` if caller's body lacks the prefix (idempotent).   |
| Rate-limit options validation                   | This workstream       | `TelegramOptionsValidator.ValidateRateLimits` rejects ≤ 0 values at host startup (fail-fast). |

---

## 10-step decomposition (matches `docs/.../implementation-plan.md` §2.3)

1. **Implement `TelegramMessageSender : IMessageSender`** — bot-client adapter that posts via `SendMessage` with `parseMode = MarkdownV2`.
2. **Wire `ITelegramRateLimiter`** — dual-layer token bucket (global per second + per-chat per minute) acquired BEFORE the HTTP call.
3. **Implement `TelegramQuestionRenderer.BuildBody`** — MarkdownV2-escaped header, severity badge, timeout phrasing, proposed-default-action line, body, and footer with correlation id.
4. **Implement `TelegramQuestionRenderer.BuildInlineKeyboard`** — one button per `AllowedAction`, `CallbackData = "{questionId}:{actionId}"`, comment-required actions marked.
5. **Cache `HumanAction`s before send** — `CacheActionsAsync` writes `IDistributedCache` keys `{questionId}:{actionId}` so `CallbackQueryHandler` can resolve.
6. **Trace footer on every outbound** — append `🔗 trace: {traceId}` when the body lacks the prefix; idempotent so caller-supplied footers win.
7. **MarkdownV2 escape boundary** — questions escape, non-questions pass through (architecture §4.12); both honour the 4096-char Telegram per-message cap **after** escape.
8. **Long-message split** — `SplitForTelegram` cuts the already-prepared payload at paragraph / line breaks within the 4096-char budget.
9. **429 retry honouring `retry_after`** — `SendWithRateLimitRetry` catches `ApiRequestException(ErrorCode=429)`, awaits `Parameters.RetryAfter` seconds via `TimeProvider`, retries up to `MaxRateLimitRetries` (3).
10. **Persist `message_id` → `CorrelationId`** (step 161 of the implementation plan) — `PersistMessageIdMappingAsync` writes `outbound:msgid:{messageId}` to `IDistributedCache` with a 24 h TTL; Stage 4.1's outbox will replace this with a durable mapping.

---

## Out-of-scope (defer to owning workstreams)

- Outbound queue with retry / dedup / DLQ — Stage 4.1 (`OutboundQueueProcessor`).
- Connector rendering of `Alert` / `StatusUpdate` / `CommandAck` envelopes — Stage 3.x.
- `CallbackQueryHandler` (inbound side of the question round-trip) — Stage 3.3.
- Chat-id allowlist enforcement at receive — Stage 2.4 / 2.5 (webhook & polling receivers).
- Operator audit trail of human responses — Stage 4.2 (inbound audit store).

---

## Non-functional constraints honoured in this workstream

- **P95 send latency ≤ 2 s after queue dispatch** — proactive rate-limit acquisition prevents server-side 429 round-trips on the hot path; the only added latency is the bounded token-bucket wait.
- **Burst from 100+ agents** — global token bucket with configurable burst capacity absorbs synchronous bursts without dropping; the per-chat bucket prevents any one chat from saturating the global pool.
- **All messages include trace/correlation id** — `Activity.Current` fallback in `SendTextAsync` and `TelegramQuestionRenderer.BuildBody` for questions guarantee the footer even if a caller forgets.
- **Never log token** — `TelegramOptions.BotToken` is consumed only by `ITelegramBotClient`; never written to logs or telemetry.

---

## Verification (Stage 7.1 integration tests)

The `tests/AgentSwarm.Messaging.IntegrationTests` project boots the
real Worker host under a WireMock-backed fake Telegram API and
exercises:

- Worker `/healthz` returns 200 (host bootstrap intact).
- `SendTextAsync` reaches the fake API with `parse_mode=MarkdownV2` and the trace footer.
- 6000-char body splits into ≥ 2 chunks, each ≤ 4096 chars.
- 429 with `retry_after=1` triggers a back-off + retry; second attempt succeeds.
- `IMessageSender` resolves to the production `TelegramMessageSender` from DI (regression guard against a fixture override clobbering registration).
