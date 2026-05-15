# AgentSwarm Teams App Manifest

This folder holds the Microsoft Teams app manifest template and the two icons
used to sideload (or admin-deploy) the AgentSwarm bot into a Microsoft Teams
tenant.

## Files

| File            | Purpose                                                                                                                                                                                                                                       |
|-----------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `manifest.json` | Teams app manifest, schema **v1.16**. Ships with placeholder GUID `00000000-0000-0000-0000-000000000000` (in every id field) and placeholder host `bot.example.com`. Both placeholders are substituted at package time by the build script.   |
| `color.png`     | 192×192 full-colour app icon. Placeholder; replace with your tenant-branded icon before distribution.                                                                                                                                         |
| `outline.png`   | 32×32 transparent outline icon. Placeholder; replace with a tenant-branded outline before distribution.                                                                                                                                       |

The packaging script lives at the repository root:
[`scripts/package-teams-app.ps1`](../../../../scripts/package-teams-app.ps1).

## Building the sideload package

Run the script under `pwsh` (PowerShell 7+). All four parameters are
**required**, and the script fails (non-zero exit) on invalid GUIDs, an
invalid version, a missing or placeholder bot domain, or if any placeholder
value would survive into the generated manifest. A successful exit always
denotes a tenant-deployable artifact.

```pwsh
pwsh ./scripts/package-teams-app.ps1 `
    -AppId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f `
    -Version 1.0.0 `
    -BotDomain bots.contoso.com `
    -OutputPath ./artifacts/teams-app.zip
```

The resulting zip contains the substituted `manifest.json` plus the two
icons. Upload it via:

* **Personal sideloading** — Teams *Apps* → *Manage your apps* → *Upload an app*.
* **Tenant-wide deployment** — Teams Admin Center → *Teams apps* → *Manage apps* → *Upload new app*.

## Identity model and substitution semantics

AgentSwarm uses a **single Microsoft Entra / Bot Framework registration**.
The same `MicrosoftAppId` is configured on the Bot Framework adapter and is
written into every id site in the manifest. This matches the work-item
contract ("`botId` referencing the `MicrosoftAppId`") and the runtime
assumption that Teams will only route `composeExtension/submitAction` and
bot activities to the registered MicrosoftAppId.

| Placeholder                              | Replaced into                                                                                                                                          |
|------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|
| `00000000-0000-0000-0000-000000000000`   | `id`, `bots[0].botId`, `composeExtensions[*].botId`, `webApplicationInfo.id`, and the GUID component of `webApplicationInfo.resource` — all ← `-AppId`. |
| `bot.example.com`                        | `validDomains[*]`, `developer.websiteUrl` / `privacyUrl` / `termsOfUseUrl`, and the host portion of `webApplicationInfo.resource` ← `-BotDomain`.       |

After substitution the script asserts that **no** placeholder GUID and
**no** placeholder host survive in the rendered manifest, throwing if a
future template change adds a new field this script does not yet rewrite.

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
  tab or message-extension auth flows. Its `id` equals the bot's
  MicrosoftAppId; its `resource` follows the `api://<bot-domain>/<app-id>`
  convention.
