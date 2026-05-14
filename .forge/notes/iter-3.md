# Iter notes — Stage 2.1 (ASP.NET Core Bot Host) — iter 3

## Iteration Summary
Iter 2 was scored 92 with two remaining items: (1) `RateLimitMiddleware`
not registered as an explicit singleton in DI, and (2)
`TelemetryMiddlewareOptions.SensitiveActivityTypes` exposed as
`IList<string>` instead of the canonical `string[]`. The system also
flagged that the previous chat reply did not surface the prior `[ ]`
checkboxes as `[x]` FIXED markers, so this iter's reply will list ALL
seven prior numbered items (5 from iter 1 + 2 from iter 2) with their
resolution state.

The structural change driving item 1 was converting both ASP.NET Core
middleware (`RateLimitMiddleware`, `TenantValidationMiddleware`) from
the convention-based `(RequestDelegate next, …)` constructor pattern to
the factory-based `Microsoft.AspNetCore.Http.IMiddleware` pattern. The
factory pattern (a) allows the framework to resolve each middleware
instance from DI on every request via `IMiddlewareFactory`, and (b)
honors the registered service lifetime — so a singleton registration
yields a true singleton, instead of relying on ASP.NET Core's
convention-based "instantiated once per pipeline build" behavior. The
implementation-plan note "ASP.NET Core middleware (not Bot Framework
IMiddleware)" still holds: `Microsoft.AspNetCore.Http.IMiddleware` is
the ASP.NET Core hosting abstraction, distinct from
`Microsoft.Bot.Builder.IMiddleware`.

### Prior feedback resolution

**From iter 1 (re-confirmed; already FIXED in iter 2):**
- [x] 1. FIXED — `TeamsMessagingPostConfigure.cs:44` maps
  `RetryCount = MaxRetryAttempts` 1:1 (no stale `- 1`). Pinned by
  `PostConfigure_MapsRetryCount_OneToOneWith_MaxRetryAttempts`.
  Independently verified again this iter: `grep -rnF "MaxRetryAttempts - 1" src/ tests/`
  returns no matches.
- [x] 2. FIXED — `TeamsMessagingOptionsValidator.cs:45-53` validates
  `BotEndpoint` non-empty + absolute http(s) URI. Pinned by three test
  layers including the startup-integration test
  `MissingBotEndpoint_ThrowsOptionsValidationException` that overrides
  appsettings.json's default via `settings[key] = string.Empty`.
- [x] 3. FIXED — `TelemetryMiddleware.cs:92-133` extracts `tenant.id`
  from `Conversation.TenantId` first, then falls back to
  `TeamsChannelData.Tenant.Id`, then to a JObject/anonymous-typed
  reader. Both priority and fallback covered by named tests.
- [x] 4. FIXED — `WorkerStartupTests.cs:73`
  `PostMessages_Returns200_ForValidActivityFromAllowedTenant` uses
  `AnonymousAuthWebApplicationFactory` (empty MicrosoftAppId/Password
  = Bot Framework's documented auth bypass) to drive a valid activity
  end-to-end through every middleware to the controller and yields
  HTTP 200.
- [x] 5. FIXED — `IConversationReferenceStore.cs` interface declares
  `MarkInactiveAsync` (line 75) and `MarkInactiveByChannelAsync`
  (line 81) BEFORE the three `IsActive*` methods (lines 87, 98, 105),
  matching the Stage 2.1 canonical sequence.

**From iter 2 (newly fixed this iter):**
- [x] 1. FIXED — `RateLimitMiddleware` and `TenantValidationMiddleware`
  converted from convention-based to factory-based ASP.NET Core
  middleware (both now implement `Microsoft.AspNetCore.Http.IMiddleware`)
  and explicitly registered as singletons in
  `src/AgentSwarm.Messaging.Worker/Program.cs:48-49`:
  ```csharp
  builder.Services.AddSingleton<TenantValidationMiddleware>();
  builder.Services.AddSingleton<RateLimitMiddleware>();
  ```
  This brings the DI registration symmetry with `TelemetryMiddleware`
  and `ActivityDeduplicationMiddleware` (lines 40-41). The HTTP
  pipeline wiring (`app.UseMiddleware<…>()` at lines 147-148) is
  unchanged — the framework resolves IMiddleware instances from DI
  on every request via `IMiddlewareFactory`.
- [x] 2. FIXED — `TelemetryMiddlewareOptions.cs:31` changed
  `IList<string> SensitiveActivityTypes` to
  `string[] SensitiveActivityTypes` with default `Array.Empty<string>()`.
  `TelemetryMiddleware.cs:71` consumed it via `.Any(…)` which works
  identically with the new type; configuration binding handles
  `"SensitiveActivityTypes": []` in appsettings.json for arrays too.

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/Middleware/RateLimitMiddleware.cs` —
  converted to `: IMiddleware`. Removed `private readonly RequestDelegate _next`
  field; constructor no longer takes `RequestDelegate next`;
  `InvokeAsync(HttpContext)` → `InvokeAsync(HttpContext, RequestDelegate)`;
  all `_next(context)` call sites replaced with `next(context)`; added
  `if (next is null) throw new ArgumentNullException(nameof(next))` guard.
  Updated XML doc on `InvokeAsync` to document the `next` parameter.
- `src/AgentSwarm.Messaging.Teams/Middleware/TenantValidationMiddleware.cs` —
  same IMiddleware conversion as `RateLimitMiddleware`.
- `src/AgentSwarm.Messaging.Teams/Middleware/TelemetryMiddlewareOptions.cs` —
  `SensitiveActivityTypes` type changed from `IList<string>` to
  `string[]`; default initializer changed from `new List<string>()` to
  `Array.Empty<string>()`; doc-comment remark added noting the
  canonical contract surface.
- `src/AgentSwarm.Messaging.Worker/Program.cs` — added explicit
  `AddSingleton<TenantValidationMiddleware>()` and
  `AddSingleton<RateLimitMiddleware>()` near the existing
  `AddSingleton<TelemetryMiddleware>()` / `AddSingleton<ActivityDeduplicationMiddleware>()`
  registrations. Multi-line comment documents why both lifetimes are
  singleton (per Stage 2.1) and how `IMiddlewareFactory` resolves them
  per-request.
- `tests/AgentSwarm.Messaging.Teams.Tests/RateLimitMiddlewareTests.cs` —
  `CreateMiddleware` helper return tuple changed to
  `(middleware, next, invoked)`; middleware ctor invocation now
  `new RateLimitMiddleware(monitor, logger)` (no `next` arg); every
  `middleware.InvokeAsync(ctx)` call site updated to
  `middleware.InvokeAsync(ctx, next)` (5 fact methods × multiple call
  sites).
- `tests/AgentSwarm.Messaging.Teams.Tests/TenantValidationMiddlewareTests.cs` —
  same test-shape update; the `next` element of the return tuple was
  already present (third position) but previously unused; ctor
  invocation dropped the `next` argument; all 7 `InvokeAsync` call
  sites updated.
- `.forge/iter-notes.md` (this file).

## Decisions made this iter

- Converted to `Microsoft.AspNetCore.Http.IMiddleware` (factory
  pattern) rather than reverse-engineering a way to keep the
  convention-based pattern and still register the type as a DI
  singleton. The convention-based pattern intentionally instantiates
  the middleware once per pipeline build, with `next` baked into the
  closure — there is no clean way to inject it via DI without the
  framework's `IMiddlewareFactory` machinery. The conversion is a
  small, targeted structural change with a localized test-shape
  impact.
- Kept `TelemetryMiddleware` and `ActivityDeduplicationMiddleware` on
  the Bot Framework middleware path unchanged. Those are
  `Microsoft.Bot.Builder.IMiddleware` (registered inside
  `CloudAdapter`), not ASP.NET Core HTTP middleware; the evaluator's
  item 1 specifically called out HTTP middleware singleton
  registration.
- Did not refactor `TelemetryMiddlewareOptions` further (e.g., into a
  records-based type) — the evaluator's item 2 was strictly about the
  `SensitiveActivityTypes` member type. Keeping the change surgical
  avoids regressing the otherwise passing options-binding tests.

## Dead ends tried this iter

- Initially considered using `[FromKeyedServices]` or a custom
  `IMiddlewareFactory` to give each instance its own per-app-id
  configuration. Discarded — the standard
  `MiddlewareFactory.Create(Type)` from
  `Microsoft.AspNetCore.Http.MiddlewareFactory` (auto-registered by
  the framework) resolves from `IServiceProvider`, which honors the
  singleton lifetime registered in `Program.cs`. No custom factory
  needed.

## Open questions surfaced this iter

- None. The two evaluator items were specific and actionable.

## What's still left

- Nothing for Stage 2.1. `dotnet build AgentSwarm.Messaging.sln`
  exits 0 with 0 warnings and 0 errors. `dotnet test` passes 222/222
  across the four test projects (Abstractions 82 + Persistence 62 +
  Teams 71 + Worker 7). Stage 2.2 (TeamsActivityHandler hardening)
  and Stage 5.1 (Audit integration) build on top of the now-correct
  middleware DI contract.
