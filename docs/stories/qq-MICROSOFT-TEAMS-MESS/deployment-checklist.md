# Deployment Checklist — Microsoft Teams Messenger

> **Stage 5.1 / Step 9 deliverable.** Operator-facing checklist covering Entra ID
> registration, Azure Bot Service provisioning, Teams admin policy configuration,
> and runtime enforcement so the bot can satisfy the story requirement
> *"Integrate with Entra ID / Teams app installation policies."*
>
> Cross-references:
> - `tech-spec.md` §4.2 (Identity & Authorization), §5.1 R-5 (No Graph permissions required)
> - `architecture.md` §3.2 (Security architecture)
> - `implementation-plan.md` §5.1 Step 9 (this document)
> - `AgentSwarm.Messaging.Teams.Security.TeamsAppPolicyOptions`
> - `AgentSwarm.Messaging.Teams.Security.TeamsAppPolicyHealthCheck`

---

## Pre-flight: gather identifiers

Before starting, collect the following from the target environment so each
step can be completed without context-switching:

- [ ] Target Entra ID tenant ID(s) the bot is authorized to serve
      (matches `TeamsMessagingOptions.AllowedTenantIds`).
- [ ] Production-grade Key Vault for storing the `MicrosoftAppPassword`
      (never commit the secret to source control or pipeline variables).
- [ ] Azure subscription with permission to create an Azure Bot resource.
- [ ] Microsoft 365 admin (or delegated Teams admin) account with rights
      to publish a custom Teams app and configure app setup policies.

---

## 1. Entra ID app registration

Register the bot identity in Microsoft Entra ID. **No Microsoft Graph API
permissions are required** — proactive messaging uses
`BotAdapter.ContinueConversationAsync` with the bot's own `MicrosoftAppId`
(per `tech-spec.md` §5.1 R-5). Installation state is tracked locally via
`InstallationUpdate` activities captured by the bot handler (Stage 2.2),
not by querying Graph.

- [ ] **Create the app registration.**
  - Entra ID → *App registrations* → *New registration*.
  - Name: `agent-swarm-teams-bot` (suggested).
  - Supported account types:
    - **Single-tenant** (default, recommended for enterprise deployments
      that only serve one Entra tenant).
    - **Multi-tenant** only if the bot must serve multiple tenants. If
      multi-tenant is chosen, populate every served tenant in
      `TeamsMessagingOptions.AllowedTenantIds` so the
      `TenantValidationMiddleware` rejects activities from un-listed
      tenants with HTTP 403.
  - Redirect URI: leave blank (the bot does not perform interactive
    OAuth sign-in for its own service principal).
- [ ] **Create a client secret** under *Certificates & secrets*.
  - Set expiration ≤ 24 months and add a calendar reminder for rotation
    (rotation runbook lives in `docs/runbooks/secret-rotation.md` once
    available).
  - Copy the secret value to the deployment Key Vault as
    `MicrosoftAppPassword`. The Key Vault reference is bound to the
    Bot Service configuration in step 2.
- [ ] **Record** the `Application (client) ID` — this becomes
      `MicrosoftAppId` in `TeamsMessagingOptions` and the Bot Service
      configuration.
- [ ] **Verify no Graph permissions are granted.**
  - *API permissions* should list **only** `Microsoft Graph → User.Read`
    (delegated, the default created with every app registration) **or
    be empty**. Any `Application` permissions in the list are out of
    scope for this bot — remove them.
  - Rationale: the bot must not be able to read Teams metadata,
    chat history, or user profiles via Graph; all interaction happens
    through the Bot Framework channel.

## 2. Azure Bot Service + Teams channel

- [ ] **Create an Azure Bot resource** in the same subscription/region as
      the bot's compute (App Service / Container Apps / AKS).
  - *Microsoft App ID* → use the Application ID from step 1
    (`Type: User-Assigned Managed Identity` is supported but the
    classic *Single Tenant* / *Multi Tenant* mode is the
    recommended path; pick the mode matching the app registration's
    supported-account-types setting).
  - *Messaging endpoint* → `https://<bot-host>/api/messages`.
- [ ] **Bind the client secret** from Key Vault to the Bot Service
      configuration (or to the host's App Service / Container Apps
      configuration as the `MicrosoftAppPassword` environment variable).
- [ ] **Enable the Microsoft Teams channel** under *Channels*.
      No other channels (Web Chat, Direct Line, Slack, etc.) should be
      enabled in production unless explicitly approved — additional
      channels expand the attack surface and bypass the Teams identity
      assertions on which `EntraIdentityResolver` depends.
- [ ] **Health check (post-deploy).** After the host is running, hit
      `GET /health` and confirm the `teams-app-policy` health-check
      registration returns `Healthy`. The `TeamsAppPolicyHealthCheck`
      probes:
      1. `MicrosoftAppId` is configured.
      2. `BotFrameworkAuthentication` can mint a connector token for
         the configured `MicrosoftAppId` / channel service URL.
      3. `IConversationReferenceStore` is reachable (DB or in-memory).
      A `Degraded` result names the failing component in `Description`.

## 3. Teams admin center — app setup policy

The bot must be **pre-installed** (or at minimum allowed) for the
authorized user/team scope so that proactive messaging works without
each user manually side-loading the package. Proactive
`ContinueConversationAsync` only succeeds when a conversation reference
exists, and references are only captured when the user has installed
the app and produced a first activity (per `tech-spec.md` §5.1 R-5).

- [ ] **Package the Teams app manifest** (`manifest.json` + icons) and
      upload to *Teams admin center → Teams apps → Manage apps*.
      The manifest `id` should match the bot's `MicrosoftAppId`.
- [ ] **Create or edit an app setup policy** under
      *Teams apps → Setup policies*.
  - Add the agent-swarm bot to *Installed apps* for the target
    user group (or "Global (Org-wide default)" if rolling out
    tenant-wide).
  - Optionally add the bot to *Pinned apps* to surface it on the
    Teams sidebar.
- [ ] **Assign the policy** to the target users/groups under
      *Users → Manage users → Policies → App setup policy*.
- [ ] **Block side-loading** in production: set
      `TeamsAppPolicyOptions.BlockSideloading = true` (default in
      production configs) and disable *Upload custom apps* in
      *Teams apps → App permission policies* for non-admin users.
      This guarantees only the admin-deployed version of the bot
      is trusted.

## 4. Runtime configuration enforcement

These settings live in `appsettings.json` / Key Vault and are loaded
by the host's `Program.cs` into the options objects exposed by
`AgentSwarm.Messaging.Teams.Security`.

- [ ] **`TeamsAppPolicyOptions.RequireAdminConsent = true`** (production
      default). When true, the runtime only trusts installations that
      were produced via an admin-driven app setup policy. The flag is
      validated at startup; mis-set values trip the
      `TeamsAppPolicyHealthCheck` `Degraded` path with a clear
      description.
- [ ] **`TeamsAppPolicyOptions.AllowedAppCatalogScopes`** — set to
      `["organization"]` to only trust the organization-published
      catalog entry. Add `"personal"` only if individual user
      side-loading is an approved deployment model (it is not, by
      default — see step 3 `BlockSideloading` above).
- [ ] **`TeamsMessagingOptions.AllowedTenantIds`** populated with the
      production tenant ID(s). `TenantValidationMiddleware` short-circuits
      the HTTP pipeline with HTTP 403 + `IAuditLogger` `SecurityRejection`
      record whenever an inbound activity's `channelData.tenant.id` does
      not match.
- [ ] **`IIdentityResolver` and `IUserAuthorizationService`** are
      registered by `services.AddTeamsSecurity()`. This call:
      - replaces the Stage 2.1 `DefaultDenyIdentityResolver` with
        `EntraIdentityResolver` (maps Teams `Activity.From.AadObjectId`
        to the internal user directory entry);
      - replaces the Stage 2.1 `DefaultDenyAuthorizationService` with
        `RbacAuthorizationService` (role-scoped command check driven
        by `RbacOptions`).
- [ ] **`RbacOptions` role matrix** defaults to:
      | Role     | Allowed commands                                       |
      |----------|--------------------------------------------------------|
      | Operator | `agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume` |
      | Approver | `approve`, `reject`, `agent status`                    |
      | Viewer   | `agent status`                                         |
      Override per-deployment via configuration binding if necessary,
      but keep the canonical role names (`Operator`, `Approver`,
      `Viewer`) so audit records remain comparable across deployments.

## 5. Installation state tracking (local, no Graph)

Installation state is tracked locally — there is no Graph call. The
flow is:

1. User installs the Teams app → Teams sends an `InstallationUpdate`
   activity → `OnInstallationUpdateActivityAsync` (Stage 2.2) captures
   the `ConversationReference` and stores it via
   `IConversationReferenceStore.AddOrUpdateAsync`.
2. User uninstalls the app → Teams sends an `InstallationUpdate`
   activity with `action = "remove"` → handler calls
   `IConversationReferenceStore.MarkInactiveAsync` (user-scoped) or
   `MarkInactiveByChannelAsync` (channel-scoped).
3. Before any proactive send, `InstallationStateGate` calls
   `IsActiveByInternalUserIdAsync` / `IsActiveByChannelAsync` (per
   target type). If inactive, the gate **skips the Bot Framework call**,
   dead-letters the outbound message via `IMessageOutbox.DeadLetterAsync`,
   and writes an `IAuditLogger` record with `EventType = "Error"` and
   `Outcome = "Failed"`.
4. Stale references (valid install, but the reference became invalid —
   e.g. the user was removed from the tenant) are detected reactively
   via HTTP 403/404 on the proactive send and surface as
   `Outcome = "Failed"` audit records (per `tech-spec.md` §4.2 R-2).

- [ ] Confirm `IConversationReferenceStore` has a durable backing store
      in production (the EF Core implementation from Stage 4.1, not the
      in-memory stub) so installation state survives restarts.

## 6. Verification — go/no-go gate

Run the following checks against the deployed host before declaring
the rollout complete. Each check maps to an acceptance criterion in
the story brief.

- [ ] `GET /health` returns `Healthy` and the `teams-app-policy`
      registration is present and healthy.
- [ ] Inbound activity from an un-listed tenant returns **HTTP 403**
      and writes a `SecurityRejection` audit record with
      `Action = "UnauthorizedTenantRejected"` and
      `Outcome = "Rejected"`.
- [ ] An unmapped user (no entry in `IUserDirectory`) receives the
      access-denied Adaptive Card and a `SecurityRejection` audit
      record with `Action = "UnmappedUserRejected"`,
      `Outcome = "Rejected"`.
- [ ] A user with `Viewer` role attempting `approve` receives the
      RBAC-rejection card and a `SecurityRejection` audit record with
      `Action = "InsufficientRoleRejected"`, `Outcome = "Rejected"`.
- [ ] A user with `Operator` role sending `agent ask ...` is dispatched
      successfully (`CommandReceived` audit, `Outcome = "Success"`).
- [ ] Uninstall-and-retry: uninstall the bot for a test user, attempt
      a proactive send, confirm the message is dead-lettered and an
      `Error` / `Failed` audit record names
      `Action = "InstallationGateRejected"`.

When every box in §6 is checked, the deployment satisfies the Stage 5.1
"Tenant and Identity Validation" acceptance bar.
