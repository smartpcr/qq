#!/usr/bin/env python3
"""Fix implementation-plan.md for iteration 28 — addresses all 5 evaluator feedback items."""

import sys

FILE = r"E:\forge\qq\.worktree\spawn-story-qq-MICROSOFT-TEAMS-MESS-plan\docs\stories\qq-MICROSOFT-TEAMS-MESS\implementation-plan.md"

with open(FILE, 'r', encoding='utf-8') as f:
    content = f.read()

original_len = len(content)
replacements_done = []

def do_replace(old, new, label, count=1):
    global content
    n = content.count(old)
    if n == 0:
        print(f"WARNING: '{label}' — old text NOT FOUND")
        return False
    if n > count:
        print(f"WARNING: '{label}' — found {n} occurrences, expected {count}")
    content = content.replace(old, new, count)
    replacements_done.append(label)
    print(f"OK: {label} (replaced {min(n,count)} of {n})")
    return True

# ============================================================
# ITEM 5: Rename UserAuthorizationService → RbacAuthorizationService
# ============================================================
do_replace(
    "`DefaultDenyAuthorizationService` stub in Stage 2.1; `UserAuthorizationService` in Stage 5.1)",
    "`DefaultDenyAuthorizationService` stub in Stage 2.1; `RbacAuthorizationService` in Stage 5.1)",
    "Item5-line99-rename"
)

do_replace(
    "Implement `UserAuthorizationService : IUserAuthorizationService`",
    "Implement `RbacAuthorizationService : IUserAuthorizationService`",
    "Item5-line307-rename"
)

# ============================================================
# ITEM 2: Move TeamsCardState from Abstractions to Teams
# ============================================================
do_replace(
    "Define `TeamsCardState` record in `AgentSwarm.Messaging.Abstractions` with fields:",
    "Define `TeamsCardState` record in `AgentSwarm.Messaging.Teams` (per `architecture.md` §7 line 1145) with fields:",
    "Item2-TeamsCardState-assembly"
)

do_replace(
    "Although `architecture.md` §7 line 1145 lists `TeamsCardState` under `AgentSwarm.Messaging.Teams`, this plan places the type in `AgentSwarm.Messaging.Abstractions` to co-locate it with the `ICardStateStore` interface that depends on it (per §4.3 line 614: `// Assembly: AgentSwarm.Messaging.Abstractions`), eliminating the reverse assembly dependency that would otherwise require Abstractions to reference Teams. This is a compile-safe single-assembly boundary: the interface and its parameter type both live in Abstractions; the concrete `SqlCardStateStore` implementation lives in Teams (which already references Abstractions).",
    "Aligned with `architecture.md` §7 line 1145 which lists `TeamsCardState` under `AgentSwarm.Messaging.Teams`. The `ICardStateStore` interface (whose methods use `TeamsCardState` as a parameter type per `architecture.md` §4.3 lines 617-618) is also placed in `AgentSwarm.Messaging.Teams` alongside `TeamsCardState` to avoid a circular assembly dependency (Abstractions would need to reference Teams if the interface lived in Abstractions). The concrete `SqlCardStateStore` implementation co-locates with both. **Cross-doc note**: `architecture.md` §4.3 line 614 and §7 line 1142 list `ICardStateStore` under `AgentSwarm.Messaging.Abstractions`, but placing the interface there creates a compile-time dependency from Abstractions → Teams since `TeamsCardState` is in Teams and `ICardStateStore.SaveAsync` takes `TeamsCardState` as a parameter; this plan resolves the conflict by co-locating both in Teams.",
    "Item2-TeamsCardState-rationale"
)

do_replace(
    "Define `ICardStateStore` interface in `AgentSwarm.Messaging.Abstractions` (per `architecture.md` §4.3 lines 611-615 which declares `// Assembly: AgentSwarm.Messaging.Abstractions`, and §7 line 1142 which lists `ICardStateStore (interface)` under `AgentSwarm.Messaging.Abstractions`)",
    "Define `ICardStateStore` interface in `AgentSwarm.Messaging.Teams` (co-located with `TeamsCardState` to avoid a circular assembly dependency — `ICardStateStore.SaveAsync` takes `TeamsCardState` as a parameter per `architecture.md` §4.3 lines 617-618; note: `architecture.md` §4.3 line 614 and §7 line 1142 list `ICardStateStore` under `AgentSwarm.Messaging.Abstractions`, but that creates a compile-time Abstractions → Teams dependency since `TeamsCardState` is in Teams per §7 line 1145; this plan co-locates interface and type in Teams to resolve the conflict)",
    "Item2-ICardStateStore-assembly"
)

do_replace(
    "Both `ICardStateStore` and `TeamsCardState` live in `AgentSwarm.Messaging.Abstractions` (see step above), so no cross-assembly type dependency exists. The concrete SQL-backed `SqlCardStateStore` implementation lives in `AgentSwarm.Messaging.Teams` (per §7 line 1145). The concrete SQL-backed implementation is provided in Stage 3.3.",
    "Both `ICardStateStore` and `TeamsCardState` live in `AgentSwarm.Messaging.Teams` (see step above), so no cross-assembly type dependency exists. The concrete `SqlCardStateStore` co-locates with the interface in Teams. The concrete SQL-backed implementation is provided in Stage 3.3.",
    "Item2-colocation-note"
)

do_replace(
    "`ICardStateStore` interface (contract defined in Stage 1.2 in `AgentSwarm.Messaging.Abstractions` per `architecture.md` §4.3 line 614 and §7 line 1142)",
    "`ICardStateStore` interface (contract defined in Stage 1.2 in `AgentSwarm.Messaging.Teams` — co-located with `TeamsCardState`)",
    "Item2-NoOpCardStateStore-DI-ref"
)

# ============================================================
# ITEM 1: TenantValidationMiddleware — Bot Framework IMiddleware
# ============================================================
do_replace(
    "Implement `TenantValidationMiddleware` as an **ASP.NET Core HTTP middleware** (`Microsoft.AspNetCore.Http.IMiddleware`) in `AgentSwarm.Messaging.Teams.Middleware` — NOT a Bot Framework `IMiddleware`. This is an ASP.NET Core request pipeline middleware that runs BEFORE the request reaches `CloudAdapter.ProcessAsync`, so it can directly set the HTTP response status code. It reads the `AllowedTenantIds` list from `TeamsMessagingOptions`, parses the inbound Bot Framework activity JSON from the request body to extract the tenant ID from `activity.channelData.tenant.id` (or `activity.conversation.tenantId`), and rejects activities from tenants not in the list by setting `context.Response.StatusCode = 403` and short-circuiting — **no bot response, no Adaptive Card, no reply of any kind** is sent to blocked tenants (per `tech-spec.md` §4.2 row 2). The middleware buffers and rewinds the request body (`context.Request.EnableBuffering()`) so `CloudAdapter` can re-read it downstream.",
    "Implement `TenantValidationMiddleware : IMiddleware` (Bot Framework `Microsoft.Bot.Builder.IMiddleware`) in `AgentSwarm.Messaging.Teams.Middleware` — a Bot Framework middleware that runs inside the `CloudAdapter` pipeline at position 2 (after `TelemetryMiddleware`, before `ActivityDeduplicationMiddleware`), aligned with `architecture.md` §2.3 lines 97-104 which defines the canonical middleware pipeline as `TelemetryMiddleware` → `TenantValidationMiddleware` → `ActivityDeduplicationMiddleware` → `RateLimitMiddleware`. It reads the `AllowedTenantIds` list from `TeamsMessagingOptions`, inspects `turnContext.Activity.ChannelData` to extract `tenant.id` (or `turnContext.Activity.Conversation.TenantId`), and rejects activities from tenants not in the list by NOT calling `next()` (short-circuiting the Bot Framework pipeline) — **no bot response, no Adaptive Card, no reply of any kind** is sent to blocked tenants (per `tech-spec.md` §4.2 row 2).",
    "Item1-TenantValidation-type"
)

# Update Stage 5.1 reference to TenantValidationMiddleware
do_replace(
    "Enhance `TenantValidationMiddleware` (ASP.NET Core HTTP middleware created in Stage 2.1)",
    "Enhance `TenantValidationMiddleware` (Bot Framework middleware created in Stage 2.1)",
    "Item1-Stage5.1-ref"
)

# Update test scenario reference
do_replace(
    "the ASP.NET Core `TenantValidationMiddleware` sets HTTP 403 on the response and short-circuits before `CloudAdapter` processes the activity",
    "the Bot Framework `TenantValidationMiddleware` short-circuits the `CloudAdapter` middleware pipeline (does not call `next()`) and sends no reply activity, preventing the activity handler from processing the request",
    "Item1-test-scenario-ref"
)

# ============================================================
# ITEM 1 (continued) + ITEM 4: Split oversized Program.cs step
# ============================================================
# Find the massive line 84 and replace it with 3 separate steps

old_program_cs_start = "- [ ] Implement `Program.cs` in `AgentSwarm.Messaging.Worker` (the ASP.NET Core host project created earlier in this stage; per `architecture.md` §7 line 1146 and `e2e-scenarios.md` lines 19, 142) with two middleware layers: (A) **ASP.NET Core HTTP pipeline**: register `TenantValidationMiddleware` via `app.UseMiddleware<TenantValidationMiddleware>()` BEFORE `app.MapControllers()` — this runs at the HTTP level before the request reaches `CloudAdapter`, enabling direct HTTP 403 responses for blocked tenants; (B) **Bot Framework adapter middleware pipeline**: register `CloudAdapter` (from `Microsoft.Bot.Builder.Integration.AspNet.Core`) as the bot adapter with Bot Framework `IMiddleware` pipeline in this exact order: `TelemetryMiddleware` → `ActivityDeduplicationMiddleware` → `RateLimitMiddleware`. This is the canonical three-middleware Bot Framework pipeline — `TenantValidationMiddleware` is NOT in this pipeline because it operates at the ASP.NET Core HTTP level (see layer A above). `architecture.md` §2.3 diagram (line 39) shows `Telemetry → TenantValidation → Dedup → Rate` as an abbreviated logical flow; in implementation, tenant validation runs as ASP.NET Core middleware before the Bot Framework adapter, while the other three run as Bot Framework `IMiddleware` inside the adapter. All middleware classes are created in the preceding steps within this stage (in `AgentSwarm.Messaging.Teams`); the Worker project references `AgentSwarm.Messaging.Teams` to access them. Register `IBot`, health check endpoints (`/health`, `/ready`), and OpenTelemetry tracing."

new_program_cs_start = """- [ ] Implement `Program.cs` in `AgentSwarm.Messaging.Worker` (the ASP.NET Core host project created earlier in this stage; per `architecture.md` §7 line 1146 and `e2e-scenarios.md` lines 19, 142) with ASP.NET Core host builder, controller discovery via `builder.Services.AddControllers().AddApplicationPart(typeof(TeamsWebhookController).Assembly)`, health check endpoints (`/health`, `/ready`), and OpenTelemetry tracing configuration. Bind `TeamsMessagingOptions` from configuration (see options registration step below).
- [ ] Register `CloudAdapter` (from `Microsoft.Bot.Builder.Integration.AspNet.Core`) as the bot adapter in `Program.cs` with the Bot Framework `IMiddleware` pipeline in this exact order: `TelemetryMiddleware` → `TenantValidationMiddleware` → `ActivityDeduplicationMiddleware` → `RateLimitMiddleware`. This is the canonical four-middleware Bot Framework pipeline aligned with `architecture.md` §2.3 lines 97-104. All four middleware classes are Bot Framework `IMiddleware` implementations created in the preceding steps within this stage (in `AgentSwarm.Messaging.Teams`); the Worker project references `AgentSwarm.Messaging.Teams` to access them. Register `IBot` pointing to `TeamsSwarmActivityHandler`."""

if old_program_cs_start in content:
    content = content.replace(old_program_cs_start, new_program_cs_start, 1)
    replacements_done.append("Item1+4-Program.cs-split-part1")
    print("OK: Item1+4-Program.cs-split-part1")
else:
    print("WARNING: Item1+4-Program.cs-split-part1 — old text NOT FOUND")

# Now split the DI registration part. Find the "Register all interface stubs in DI" section
old_di_start = " Register all interface stubs in DI so that `TeamsSwarmActivityHandler` (Stage 2.2) and `TeamsMessengerConnector` (Stage 2.3) can inject them before concrete implementations land in later stages:"
new_di_start = "\n- [ ] Register all interface stubs in DI so that `TeamsSwarmActivityHandler` (Stage 2.2) and `TeamsMessengerConnector` (Stage 2.3) can inject them before concrete implementations land in later stages:"

do_replace(old_di_start, new_di_start, "Item4-split-DI-registration")

# ============================================================
# ITEM 3: Add IInboundEventPublisher interface and wiring
# ============================================================

# Add the interface definition after ICommandDispatcher in Stage 1.2
do_replace(
    "- [ ] Define `ICommandDispatcher` interface with method `DispatchAsync(CommandContext context, CancellationToken ct)` in `AgentSwarm.Messaging.Abstractions`. This minimal contract allows the activity handler (Stage 2.2) to route parsed commands without depending on the concrete `CommandDispatcher` implementation (Stage 3.2).",
    "- [ ] Define `ICommandDispatcher` interface with method `DispatchAsync(CommandContext context, CancellationToken ct)` in `AgentSwarm.Messaging.Abstractions`. This minimal contract allows the activity handler (Stage 2.2) to route parsed commands without depending on the concrete `CommandDispatcher` implementation (Stage 3.2).\n- [ ] Define `IInboundEventPublisher` interface in `AgentSwarm.Messaging.Abstractions` with method `PublishAsync(MessengerEvent messengerEvent, CancellationToken ct)` that writes an inbound event to the shared in-process event channel for consumption by `IMessengerConnector.ReceiveAsync`. This contract decouples event producers (`TeamsSwarmActivityHandler`, `CommandDispatcher`, `CardActionHandler`) from the event consumer (`TeamsMessengerConnector.ReceiveAsync`). The default implementation `ChannelInboundEventPublisher` (created in Stage 2.1) uses `System.Threading.Channels.Channel<MessengerEvent>` as the backing transport — producers call `PublishAsync` to write to the channel writer, and `ReceiveAsync` reads from the channel reader. This explicit publisher contract and DI wiring addresses the inbound event handoff gap: `TeamsSwarmActivityHandler` and `CommandDispatcher` inject `IInboundEventPublisher` to publish `CommandEvent` and `HumanDecisionEvent` records; `CardActionHandler` injects it to publish `DecisionEvent` records; `TeamsMessengerConnector.ReceiveAsync` reads from the channel's reader end.",
    "Item3-IInboundEventPublisher-interface"
)

# Add dependency #9 to TeamsSwarmActivityHandler
do_replace(
    "(8) `ILogger<TeamsSwarmActivityHandler>` — structured operational logging. All eight are required for the handler to compile and function.",
    "(8) `IInboundEventPublisher` — publishes inbound `MessengerEvent` records (including `CommandEvent` and `DecisionEvent` subtypes) to the shared in-process event channel for consumption by `TeamsMessengerConnector.ReceiveAsync` (interface in Stage 1.2; `ChannelInboundEventPublisher` backed by `System.Threading.Channels.Channel<MessengerEvent>` in Stage 2.1); (9) `ILogger<TeamsSwarmActivityHandler>` — structured operational logging. All nine are required for the handler to compile and function.",
    "Item3-handler-dep9"
)

# Update ReceiveAsync to reference IInboundEventPublisher
do_replace(
    "- [ ] Implement `ReceiveAsync` using an in-memory channel (`System.Threading.Channels.Channel<MessengerEvent>`) fed by the activity handler.",
    "- [ ] Implement `ReceiveAsync` to read from the `System.Threading.Channels.Channel<MessengerEvent>` backing the injected `IInboundEventPublisher` (interface defined in Stage 1.2). The `ChannelInboundEventPublisher` implementation (registered in Stage 2.1) exposes both a `ChannelWriter<MessengerEvent>` (used by `PublishAsync`) and a `ChannelReader<MessengerEvent>` (used by `ReceiveAsync`). Event producers — `TeamsSwarmActivityHandler`, `CommandDispatcher`, `CardActionHandler` — write to the channel via `IInboundEventPublisher.PublishAsync`; `ReceiveAsync` reads from the channel reader, completing the inbound event handoff contract.",
    "Item3-ReceiveAsync-wiring"
)

# Add IInboundEventPublisher to DI registration (after the InMemoryActivityIdStore reference)
do_replace(
    "`InMemoryActivityIdStore` as `IActivityIdStore` (created earlier in this stage), and `NoOpAuditLogger` as `IAuditLogger`",
    "`InMemoryActivityIdStore` as `IActivityIdStore` (created earlier in this stage), `ChannelInboundEventPublisher` as `IInboundEventPublisher` (a `System.Threading.Channels.Channel<MessengerEvent>`-backed implementation created in this step — the channel is unbounded; the single writer/reader pair connects event producers to `TeamsMessengerConnector.ReceiveAsync`), and `NoOpAuditLogger` as `IAuditLogger`",
    "Item3-DI-registration"
)

# Update CommandDispatcher to publish via IInboundEventPublisher
do_replace(
    "Implement unrecognized-input handling: when message text does not match any known command pattern, enqueue a `MessengerEvent` of subtype `TextEvent` with `EventType = \"Text\"` and the raw input as payload",
    "Implement unrecognized-input handling: when message text does not match any known command pattern, publish a `MessengerEvent` of subtype `TextEvent` with `EventType = \"Text\"` and the raw input as payload via `IInboundEventPublisher.PublishAsync` (injected into `CommandDispatcher`)",
    "Item3-CommandDispatcher-publish"
)

# Update CardActionHandler to reference IInboundEventPublisher for publishing
do_replace(
    "Constructor accepts `IAgentQuestionStore`, `ICardStateStore`, `IAuditLogger`, and `ILogger<CardActionHandler>`.",
    "Constructor accepts `IAgentQuestionStore`, `ICardStateStore`, `IInboundEventPublisher`, `IAuditLogger`, and `ILogger<CardActionHandler>`.",
    "Item3-CardActionHandler-dep"
)

do_replace(
    "(6) produce a `HumanDecisionEvent` and publish it to the inbound queue;",
    "(6) produce a `HumanDecisionEvent` and publish it via `IInboundEventPublisher.PublishAsync` to the inbound event channel;",
    "Item3-CardActionHandler-publish"
)

# ============================================================
# Write result
# ============================================================
with open(FILE, 'w', encoding='utf-8') as f:
    f.write(content)

new_len = len(content)
print(f"\n--- Summary ---")
print(f"Original size: {original_len} bytes")
print(f"New size: {new_len} bytes")
print(f"Replacements made: {len(replacements_done)}")
for r in replacements_done:
    print(f"  ✓ {r}")
