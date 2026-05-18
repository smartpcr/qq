# Local Development Setup — Telegram Messenger Worker

> **Scope.** This document is the implementation-artifact / operator
> setup guide for Stage 5.1 (Secret Management Integration) of
> `qq-TELEGRAM-MESSENGER-S`. It is **not** a planning document — the
> sole progress source of truth remains `implementation-plan.md`. Use
> this guide when running the Worker on a developer laptop, in CI, or
> in any environment that does not have Azure Key Vault available.

## Audience

A developer cloning the repo and bringing up the
`AgentSwarm.Messaging.Worker` project for the first time, or an
operator validating a new deployment slot's secret pipeline.

---

## 1  How the Worker resolves the bot token

The Worker reads the Telegram bot token from the configuration key
`Telegram:BotToken`. Stage 5.1 wires four candidate sources (highest
precedence first):

| # | Source                                                | When it is active                                                                           | How to populate                                                                                                            |
| - | ----------------------------------------------------- | ------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| 1 | **Azure Key Vault** secret `TelegramBotToken`         | Only when `KeyVault:Uri` is set to a vault URI in `appsettings.json` or env var.            | `az keyvault secret set --vault-name <vault> --name TelegramBotToken --value <token>`                                       |
| 2 | **Environment variable** `Telegram__BotToken`          | Always (.NET configuration loads env vars unconditionally).                                  | `setx Telegram__BotToken "<token>"` (Windows) or `export Telegram__BotToken="<token>"` (POSIX).                             |
| 3 | **.NET User Secrets** key `Telegram:BotToken`         | Only when `ASPNETCORE_ENVIRONMENT=Development` (auto-registered by `WebApplication.CreateBuilder`). | `dotnet user-secrets set "Telegram:BotToken" "<token>"` from the `src/AgentSwarm.Messaging.Worker/` directory.             |
| 4 | **`appsettings.json` / `appsettings.Development.json`** | Always loaded.                                                                              | **Do not** use for the bot token — the JSON file is checked into source control. The default `BotToken` field is blank.    |

The brief (`implementation-plan.md` Stage 5.1) mandates that the
production source is Key Vault and the local-development source is
User Secrets. The environment-variable route exists because Docker
Compose and CI pipelines often inject secrets that way; the
appsettings JSON entry exists only so the configuration shape is
discoverable by tooling.

> **Never** commit a real bot token to source control, an
> `appsettings.json`, a `.env` file that is not in `.gitignore`, or
> a screenshot in a chat/PR description. The
> `TelegramOptions.ToString()` redaction and the audit-log redaction
> are last-line defences, not substitutes for keeping the token out
> of source control.

---

## 2  Local-dev quickstart with .NET User Secrets

### 2.1  Prerequisites

* .NET SDK 8.0.x (matches `Directory.Build.props` / project TFM).
* A Telegram bot created via [@BotFather](https://t.me/BotFather);
  the token looks like `1234567890:AAAA-BBBB-CCCC...`.

### 2.2  Initialise User Secrets

The Worker project already declares a `UserSecretsId` in
`AgentSwarm.Messaging.Worker.csproj` (added by Stage 1.1), so no
`dotnet user-secrets init` is required. From the repo root:

```powershell
cd src/AgentSwarm.Messaging.Worker
dotnet user-secrets set "Telegram:BotToken" "<paste-bot-token-here>"
```

You can also seed the webhook secret token at the same time (Stage
2.4 requires this when running in webhook mode):

```powershell
dotnet user-secrets set "Telegram:SecretToken" "<random-32-char-string>"
```

Generate the random string with:

```powershell
[System.Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
```

Verify the secrets are set (values are shown verbatim — run this in
a private terminal only):

```powershell
dotnet user-secrets list
```

### 2.3  Run the Worker in long-polling mode

`appsettings.Development.json` already sets `Telegram:UsePolling=true`
and `Telegram:WebhookUrl=null`, so the Worker boots in long-polling
mode against `api.telegram.org` without needing a public HTTPS URL.

```powershell
cd src/AgentSwarm.Messaging.Worker
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run
```

Expected first-startup log lines:

```text
info: AgentSwarm.Messaging.Worker.SecretManagement[0]
      Telegram:BotToken loaded from .NET User Secrets (secrets.json). Token value is never logged.
info: Microsoft.Hosting.Lifetime[0]
      Now listening on: http://localhost:5000
info: AgentSwarm.Messaging.Telegram.Polling.TelegramPollingService[0]
      Long-polling loop started; timeout=30s.
```

The "loaded from" provider name reflects which **approved** source
actually supplied the value: `.NET User Secrets (secrets.json)` when
sourced from `dotnet user-secrets`,
`EnvironmentVariablesConfigurationProvider` when sourced from
`$env:Telegram__BotToken`, `AzureKeyVaultConfigurationProvider` when
sourced from Key Vault. The validator **rejects** values supplied by
plaintext file sources (such as `appsettings.json`) with an
`InvalidOperationException` — see §6 Troubleshooting for the exact
error and remediation. The validator never logs the token value
itself.

### 2.4  Verify your bot answers `/start`

In Telegram, search for your bot's `@username` and send `/start`. You
should see an authorization denial unless your Telegram user id has
been added to either:

* `Telegram:AllowedUserIds` (Tier 1 onboarding allowlist), AND
* `Telegram:UserTenantMappings` with at least one workspace entry.

See Stage 5.2 + Stage 3.4 in `implementation-plan.md` for the
allowlist + onboarding flow. To grant yourself developer access, look
up your numeric user id by sending `/start` to
[@userinfobot](https://t.me/userinfobot) and add a User-Secrets entry:

```powershell
dotnet user-secrets set "Telegram:AllowedUserIds:0" "12345678"
dotnet user-secrets set "Telegram:UserTenantMappings:12345678:0:TenantId" "dev"
dotnet user-secrets set "Telegram:UserTenantMappings:12345678:0:WorkspaceId" "dev"
dotnet user-secrets set "Telegram:UserTenantMappings:12345678:0:OperatorAlias" "@me"
```

---

## 3  Environment-variable fallback (Docker Compose / CI)

The repo's `docker-compose.yml` reads `TELEGRAM_BOT_TOKEN` from your
shell or a `.env` file at the repo root and projects it into the
container as `Telegram__BotToken`:

```yaml
environment:
  Telegram__BotToken: ${TELEGRAM_BOT_TOKEN:-}
```

Create `.env` at the repo root (already in `.gitignore`):

```dotenv
TELEGRAM_BOT_TOKEN=1234567890:AAAA-BBBB-CCCC...
TELEGRAM_SECRET_TOKEN=<random-string>
```

Then:

```powershell
docker compose up worker
```

The Worker still passes its Stage 5.1 secret-source validation
because the `EnvironmentVariablesConfigurationProvider` supplies a
non-blank `Telegram:BotToken`.

---

## 4  Azure Key Vault for production

In production, set `KeyVault:Uri` to your vault's URI (for example
`https://my-prod-vault.vault.azure.net/`) via an environment variable
or a non-committed configuration source:

```powershell
$env:KeyVault__Uri = "https://my-prod-vault.vault.azure.net/"
```

Create the vault secret with the **exact** name expected by
`TelegramKeyVaultSecretManager`:

```powershell
az keyvault secret set `
  --vault-name my-prod-vault `
  --name TelegramBotToken `
  --value "<paste-bot-token-here>"
```

The Worker's host identity (a Managed Identity on AKS / App Service,
a Workload Identity Federation principal on GitHub Actions, etc.)
must hold a Key Vault access policy or RBAC role assignment that
includes `secrets/get` and `secrets/list` permissions on the vault.

When the Worker starts, you should see:

```text
info: AgentSwarm.Messaging.Worker.SecretManagement[0]
      Telegram:BotToken loaded from AzureKeyVaultConfigurationProvider. Token value is never logged.
```

If the credential cannot reach the vault, the
`AzureKeyVaultConfigurationProvider` throws during configuration
load and the host fails to start — the Stage 5.1 validator will
not get a chance to run because configuration build itself fails.

---

## 5  What happens when no source provides the token

The Stage 5.1 validator emits a `Warning`-level log line listing
every source it inspected and the verdict for each, then throws an
`InvalidOperationException` so `Program.Main` exits with a non-zero
status. Example:

```text
warn: AgentSwarm.Messaging.Worker.SecretManagement[0]
      Telegram:BotToken is not set by any configured secret source. Inspected sources:
        - Azure Key Vault (KeyVault:Uri): Not configured (set 'KeyVault:Uri' to your vault's URI to enable)
        - .NET User Secrets: Inactive (User Secrets is only auto-registered when ASPNETCORE_ENVIRONMENT=Development; current environment is 'Production')
        - Environment variable 'Telegram__BotToken': Not set
        - appsettings.json / appsettings.{Environment}.json: Inspected; key 'Telegram:BotToken' is absent or empty

Unhandled exception. System.InvalidOperationException: Telegram bot token is not configured. ...
```

The Worker exits within ~3 seconds on a fresh start (well under the
brief's 5-second budget) so failed deployments surface immediately
in container-orchestrator dashboards instead of degrading silently.

---

## 6  Troubleshooting

| Symptom                                                                  | Likely cause                                                                                                                                            | Fix                                                                                                                                  |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `Telegram:BotToken is not set by any configured secret source.`          | None of Key Vault / User Secrets / env vars provided the value.                                                                                          | Follow §2.2 (User Secrets) or §3 (env var) or §4 (Key Vault) depending on environment.                                                |
| `Telegram bot token was supplied by an unapproved source: JsonConfigurationProvider (appsettings.json)` | A non-blank value is committed to `appsettings.json` (or its `Development` sibling). **Treat as a security incident.** The Worker exits at startup. | Remove the value from JSON, rotate the bot token in BotFather, scrub git history. Re-populate via User Secrets / env var / Key Vault. |
| `KeyVault:Uri is set but is not a valid absolute URI`                    | Typo in the vault URI; it must start with `https://`.                                                                                                    | Correct the URI in your environment variable or configuration source.                                                                |
| `Azure.Identity.AuthenticationFailedException`                            | The host's identity cannot acquire a token (no Managed Identity / Workload Identity, or the local `az login` cache is stale).                            | `az login` locally, or ensure Managed Identity is assigned to the App Service / AKS pod and granted `secrets/get` on the vault.       |
| `Azure.RequestFailedException: The user, group or application ... does not have secrets get permission on key vault`. | The credential is authenticated but lacks RBAC / access policy permission on the vault.                                                                  | Grant `Key Vault Secrets User` role (RBAC) or `Get` + `List` permission (access policy) on the vault to the principal.               |

---

## 7  References

* `implementation-plan.md` — Stage 5.1 (this stage), Stage 5.2 (allowlist), Stage 3.4 (onboarding).
* `architecture.md` §7.1 — secret routing table.
* `tech-spec.md` rows S-8, HC-6, R-5 — secret management requirements.
* `docker-compose.yml` — local dev runtime.
