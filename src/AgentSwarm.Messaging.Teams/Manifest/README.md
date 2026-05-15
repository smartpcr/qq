# AgentSwarm Teams App Manifest

This folder holds the Microsoft Teams app manifest template and the two icons
used to sideload (or admin-deploy) the AgentSwarm bot into a Microsoft Teams
tenant.

## Files

| File            | Purpose                                                                                                                                                                                              |
|-----------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `manifest.json` | Teams app manifest, schema **v1.16**. Ships with placeholder GUID `00000000-0000-0000-0000-000000000000` and placeholder host `bot.example.com` — substituted at package time by the build script.   |
| `color.png`     | 192×192 full-colour app icon. Placeholder; replace with your tenant-branded icon before distribution.                                                                                                |
| `outline.png`   | 32×32 transparent outline icon. Placeholder; replace with a tenant-branded outline before distribution.                                                                                              |

The packaging script lives at the repository root: [`scripts/package-teams-app.ps1`](../../../../scripts/package-teams-app.ps1).

## Building the sideload package

Run the script under `pwsh` (PowerShell 7+). The script fails loudly on
invalid GUIDs, missing version, or placeholder values.

```pwsh
pwsh ./scripts/package-teams-app.ps1 `
    -AppId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f `
    -BotId 9f8e7d6c-aaaa-bbbb-cccc-9f8e7d6c5b4a `
    -Version 1.0.0 `
    -OutputPath ./artifacts/teams-app.zip `
    -BotDomain bots.contoso.com
```

`-BotDomain` is optional: when omitted, the placeholder host
`bot.example.com` remains in `validDomains`, `developer.*Url`, and
`webApplicationInfo.resource`. The script emits a warning in that case —
that mode is only appropriate for local smoke tests, never for tenant
deployment.

The resulting zip contains the substituted `manifest.json` plus the two
icons. Upload it via:

* **Personal sideloading** — Teams *Apps* → *Manage your apps* → *Upload an app*.
* **Tenant-wide deployment** — Teams Admin Center → *Teams apps* → *Manage apps* → *Upload new app*.

## Substitution semantics

| Placeholder                              | Replaced into                                                                                                          |
|------------------------------------------|------------------------------------------------------------------------------------------------------------------------|
| `00000000-0000-0000-0000-000000000000`   | `id` ← `-AppId`; `bots[0].botId` and `composeExtensions[0].botId` ← `-BotId`; `webApplicationInfo.id` ← `-AppId`; the GUID component of `webApplicationInfo.resource` ← `-AppId`. |
| `bot.example.com`                        | `validDomains[*]`, `developer.websiteUrl` / `privacyUrl` / `termsOfUseUrl`, host of `webApplicationInfo.resource` ← `-BotDomain` (optional). |

Two GUIDs are configurable so that Teams app identity (`id`,
`webApplicationInfo.id`) and bot-framework identity (`bots[*].botId`,
`composeExtensions[*].botId`) can come from different Entra registrations
when required. In typical single-registration deployments, pass the same
GUID for both `-AppId` and `-BotId`.

## Manifest design choices

* **Bot scopes** — `personal` and `team`, per `implementation-plan.md`
  §Stage 2.4 and `tech-spec.md` §2.1 ("Interaction scopes").
* **Compose extension command** — `id: "forwardToAgent"`, `type: "action"`,
  `context: ["message", "commandBox"]`, `fetchTask: false`. Aligned with
  `architecture.md` §2.15 (`MessageExtensionHandler`) and
  `e2e-scenarios.md` §Message Actions. The server-side handler is
  implemented in **Stage 3.4**; this manifest declares the command so
  Teams routes `composeExtension/submitAction` invocations to the bot
  endpoint immediately upon sideloading.
* **`supportsFiles: false`** — file upload/download is out of scope
  (`tech-spec.md` §2.2 out-of-scope).
* **`webApplicationInfo`** — present to support Entra ID SSO for future
  tab or message-extension auth flows.
