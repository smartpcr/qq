# AgentSwarm Teams App Manifest

This folder holds the Microsoft Teams app manifest and packaging script used to
sideload (or admin-deploy) the AgentSwarm bot into a Microsoft Teams tenant.

## Files

| File                | Purpose                                                          |
|---------------------|------------------------------------------------------------------|
| `manifest.json`     | Teams app manifest, schema **v1.16**. Ships with placeholder GUID `00000000-0000-0000-0000-000000000000` and placeholder domain `bot.example.com` — the build script substitutes both at package time. |
| `color.png`         | 192×192 full-colour app icon. Placeholder; replace with your tenant-branded icon before distribution. |
| `outline.png`       | 32×32 transparent outline icon. Placeholder; replace with a tenant-branded outline before distribution. |
| `build-manifest.ps1`| Cross-platform PowerShell (`pwsh`) packaging script. Produces `bin/manifest.zip` for sideloading. |

## Building the sideload package

The bot's Entra (AAD) application id is required at packaging time. The script
fails loudly if either the all-zero placeholder GUID or the placeholder domain
remain in the substituted manifest.

```pwsh
pwsh ./build-manifest.ps1 `
    -AppId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f `
    -BotDomain bots.contoso.com
```

The resulting `bin/manifest.zip` contains the substituted `manifest.json` plus
the two icons. Upload it via:

* **Personal sideloading** — Teams *Apps* → *Manage your apps* → *Upload an app*.
* **Tenant-wide deployment** — Teams Admin Center → *Teams apps* → *Manage apps* →
  *Upload new app*.

## Substitution semantics

| Placeholder                              | Replaced where                                                              |
|------------------------------------------|-----------------------------------------------------------------------------|
| `00000000-0000-0000-0000-000000000000`   | `id`, `bots[0].botId`, `composeExtensions[0].botId`, `webApplicationInfo.id`, and the GUID component of `webApplicationInfo.resource`. |
| `bot.example.com`                        | `validDomains[0]`, `developer.websiteUrl`/`privacyUrl`/`termsOfUseUrl`, the host of `webApplicationInfo.resource`. |

The schema requires `id`, `bots[0].botId`, `composeExtensions[0].botId`, and
`webApplicationInfo.id` to all be the **same** GUID (the bot's AAD app id).
Substituting a single placeholder GUID across all four sites preserves that
invariant.

## Manifest design choices

* **Bot scopes** — `personal` and `team`, per `implementation-plan.md` §Stage 2.4
  and `tech-spec.md` §2.1 "Interaction scopes".
* **Compose extension command** — `id: "forwardToAgent"`, `type: "action"`,
  `context: ["message", "commandBox"]`, `fetchTask: false`. Aligned with
  `architecture.md` §2.15 (`MessageExtensionHandler`) and `e2e-scenarios.md`
  §Message Actions. The server-side handler is implemented in **Stage 3.4**;
  this manifest declares the command so Teams routes
  `composeExtension/submitAction` invocations to the bot endpoint.
* **`supportsFiles: false`** — file upload/download not in scope
  (`tech-spec.md` §2.2 out-of-scope).
* **`webApplicationInfo`** — present to support Entra ID SSO for future tab or
  message-extension auth flows.
