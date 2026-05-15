# Iter notes — Stage 2.4 (Teams App Manifest)

## Files touched this iter
- `src/AgentSwarm.Messaging.Teams/Manifest/manifest.json` — NEW. Teams app manifest v1.16. Placeholder GUID `00000000-...` at four required sites (`id`, `bots[0].botId`, `composeExtensions[0].botId`, `webApplicationInfo.id`); placeholder domain `bot.example.com`. Bots: scopes=["personal","team"], supportsFiles=false. ComposeExtensions action command `forwardToAgent` (type=action, fetchTask=false, context=[message,commandBox]). Includes accentColor, developer URLs, validDomains, webApplicationInfo for SSO.
- `src/AgentSwarm.Messaging.Teams/Manifest/color.png` — NEW. 192×192 PNG (PowerShell+System.Drawing AS-monogram placeholder).
- `src/AgentSwarm.Messaging.Teams/Manifest/outline.png` — NEW. 32×32 transparent outline PNG.
- `src/AgentSwarm.Messaging.Teams/Manifest/build-manifest.ps1` — NEW. Cross-platform pwsh packaging script. Mandatory `-AppId` (GUID) + `-BotDomain`; rejects placeholder values; re-validates substituted JSON parses; writes `bin/manifest.zip` (gitignored). Optional `-OutputPath` override.
- `src/AgentSwarm.Messaging.Teams/Manifest/README.md` — NEW. Operator deployment + substitution semantics doc.
- `tests/AgentSwarm.Messaging.Teams.Manifest.Tests/*` — NEW xunit project (added to sln). 22 tests across 4 files: `ManifestSchemaValidationTests` (full v1.16 schema validation), `BotScopesTests`, `ComposeExtensionTests`, `PackagingScriptSmokeTests`. Embedded official schema as resource.
- `AgentSwarm.Messaging.sln` — added new test project via `dotnet sln add`.
- `.gitignore` — added `src/AgentSwarm.Messaging.Teams/Manifest/bin/`.

## Decisions made this iter
- **Manifest home**: `src/AgentSwarm.Messaging.Teams/Manifest/` (forward-compat with Stage 2.1 which will create the Teams .csproj). Did NOT create a csproj for it — manifest is non-source artifact.
- **Placeholder GUID** = `00000000-0000-0000-0000-000000000000` at all four required sites. Schema-valid GUID shape so the file validates out of the box; `build-manifest.ps1` substitutes and fails loudly if placeholder remains. Avoided `{{TOKEN}}` form (would break schema validation).
- **No preemptive `parameters` on the action command**: v1.16 schema only requires `id` + `title` at command level. Rubber-duck flagged preemptive `parameters` as risky for Stage 3.4 UX. Stage 3.4 will add if its handler needs them.
- **Single pwsh script** (no separate `.sh`): pwsh is cross-platform.
- **Schema validation via JsonSchema.Net 7.0.4** (not hand-rolled structural assertions): rubber-duck flagged "schema validation" as the actual ask. Two surprises:
  1. The v1.16 schema declares the legacy draft-04 dialect; JsonSchema.Net 7.x dropped Draft 4 support. Workaround: `ManifestFixture.LoadSchemaTextForEvaluation()` rewrites the `$schema` declaration to draft-07 before parsing. Draft-07 is a strict superset of the keywords this schema actually uses (`type`, `required`, `properties`, `additionalProperties`, `items`, `enum`, `const`, `pattern`, `$ref`, `definitions`, `minItems`/`maxItems`, `minLength`/`maxLength`) — semantically equivalent for this schema.
  2. Schema only validates that each GUID-shaped field is a GUID; it does NOT enforce that `id == bots[0].botId == composeExtensions[0].botId == webApplicationInfo.id`. Added explicit cross-site consistency tests (`Manifest_AppIdIsConsistentAcrossAllRequiredSites`, `ComposeExtension_BotIdMatchesTopLevelId`) — these catch hand-edit drift that schema alone cannot.
- **`PackagingScriptSmokeTests` via `Xunit.SkippableFact`**: end-to-end exercises the real `pwsh` script (substitution, zip layout, refusal paths, re-validates output against schema). Auto-skips when pwsh missing so non-Windows CI passes regardless.

## Dead ends tried this iter
- First test draft used `Assert.Equal(1, count)` → xunit analyzer rejected (xUnit2013). Switched to `Assert.Single`.
- First test run failed with `RefResolutionException: Could not resolve 'http://json-schema.org/draft-04/schema#'`. JsonSchema.Net 7.x and 6.x both lack Draft 4. Solved with dialect-rewrite at load time (see decision above) rather than downgrading or registering a stub meta-schema.

## Open questions surfaced this iter
- None blocking.
- Pre-existing orphan state observed (Persistence/Core projects exist on disk but aren't in the sln — must have been left behind by a recent sibling merge). Out of my workstream's scope; left untouched.

## What's still left
- Nothing for Stage 2.4. Build green (0 warn / 0 err). 104/104 tests pass (82 existing Abstractions + 22 new Manifest, including 7 pwsh smoke tests).
- Stage 3.4 (Message Extension Handler) will implement `OnTeamsMessagingExtensionSubmitActionAsync` to handle `forwardToAgent` invocations this manifest declares. The plan explicitly notes Stage 3.4 may add manifest fields (likely `parameters`) if the handler needs them.
