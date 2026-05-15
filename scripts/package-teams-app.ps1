#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build a Microsoft Teams sideload package (teams-app.zip) for AgentSwarm.

.DESCRIPTION
    Renders the canonical manifest template at
    `src/AgentSwarm.Messaging.Teams/Manifest/manifest.json`, substitutes the
    single MicrosoftAppId, the bot endpoint domain, and the app version, and
    bundles the result with the color/outline icons into a sideloadable zip.

    AgentSwarm uses a single Entra/bot-framework registration. The same
    `MicrosoftAppId` is written into every id field in the manifest:

        AppId       -> top-level `id`
                    -> `bots[0].botId`
                    -> `composeExtensions[*].botId`
                    -> `webApplicationInfo.id`
                    -> GUID component of `webApplicationInfo.resource`

        BotDomain   -> `validDomains[*]`
                    -> developer.websiteUrl / privacyUrl / termsOfUseUrl
                    -> host portion of `webApplicationInfo.resource`

        Version     -> top-level `version`

    Both `-AppId` and `-BotDomain` are MANDATORY: the script will not emit a
    package that still contains placeholder values, so a successful exit
    code always denotes a tenant-deployable artifact. Invalid GUIDs, empty
    versions, or placeholder values cause a non-zero exit so CI can gate the
    package on exit status.

.PARAMETER AppId
    The Microsoft Entra (AAD) application id registered for the bot. This is
    the SAME `MicrosoftAppId` configured in the AgentSwarm Bot Framework
    adapter. Must be a GUID and must not be the all-zero placeholder GUID.

.PARAMETER Version
    The app version emitted into the manifest's top-level `version` field.
    Required; must be a SemVer `MAJOR.MINOR.PATCH` string.

.PARAMETER OutputPath
    Path of the output zip file (typically `teams-app.zip`). Parent
    directories are created if missing. An existing file at this path is
    overwritten.

.PARAMETER BotDomain
    Fully-qualified domain of the bot endpoint (e.g. `bots.contoso.com`).
    Required: the placeholder host `bot.example.com` is rewritten across
    `validDomains`, `developer.*Url`, and `webApplicationInfo.resource`.
    Must not be empty and must not be the placeholder host.

.EXAMPLE
    pwsh ./scripts/package-teams-app.ps1 `
        -AppId 11111111-2222-3333-4444-555555555555 `
        -Version 1.0.0 `
        -BotDomain bots.contoso.com `
        -OutputPath ./artifacts/teams-app.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppId,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$BotDomain,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$placeholderGuid = '00000000-0000-0000-0000-000000000000'
$placeholderDomain = 'bot.example.com'
$guidRegex = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$semverRegex = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?(?:\+[0-9A-Za-z.\-]+)?$'
$anyGuidRegex = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
# RFC-1123-ish: dot-separated labels, alnum + hyphen, no leading/trailing hyphen.
$fqdnRegex = '^(?=.{1,253}$)(?:(?!-)[A-Za-z0-9-]{1,63}(?<!-)\.)+(?:(?!-)[A-Za-z0-9-]{1,63}(?<!-))$'

# --- Input validation ----------------------------------------------------
if ($AppId -notmatch $guidRegex) {
    throw "-AppId '$AppId' is not a valid GUID."
}
if ($AppId -eq $placeholderGuid) {
    throw "-AppId must not be the placeholder GUID '$placeholderGuid'."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "-Version must be a non-empty version string."
}
if ($Version -notmatch $semverRegex) {
    throw "-Version '$Version' is not a valid SemVer (MAJOR.MINOR.PATCH)."
}
if ([string]::IsNullOrWhiteSpace($BotDomain)) {
    throw "-BotDomain must be a non-empty fully-qualified domain name."
}
if ($BotDomain -eq $placeholderDomain) {
    throw "-BotDomain must not be the placeholder host '$placeholderDomain'."
}
if ($BotDomain -notmatch $fqdnRegex) {
    throw "-BotDomain '$BotDomain' is not a valid fully-qualified domain name."
}

# Normalise to lowercase to match Microsoft Teams conventions and to keep
# substitution idempotent when contributors paste mixed-case values.
$AppId = $AppId.ToLowerInvariant()
$BotDomain = $BotDomain.ToLowerInvariant()

# --- Locate inputs --------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$manifestDir = Join-Path $repoRoot 'src/AgentSwarm.Messaging.Teams/Manifest'
$manifestPath = Join-Path $manifestDir 'manifest.json'
$colorIconPath = Join-Path $manifestDir 'color.png'
$outlineIconPath = Join-Path $manifestDir 'outline.png'

foreach ($required in @($manifestPath, $colorIconPath, $outlineIconPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required manifest source not found: $required"
    }
}

# --- Substitute -----------------------------------------------------------
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

$manifest.id = $AppId
$manifest.version = $Version

if (-not $manifest.bots -or $manifest.bots.Count -lt 1) {
    throw "Manifest template at '$manifestPath' is missing the 'bots' entry."
}
$manifest.bots[0].botId = $AppId

if ($manifest.PSObject.Properties.Name -contains 'composeExtensions') {
    foreach ($ext in $manifest.composeExtensions) {
        if ($ext.PSObject.Properties.Name -contains 'botId') {
            $ext.botId = $AppId
        }
    }
}

if ($manifest.PSObject.Properties.Name -contains 'webApplicationInfo') {
    $manifest.webApplicationInfo.id = $AppId
    if ($manifest.webApplicationInfo.PSObject.Properties.Name -contains 'resource') {
        $resource = [string]$manifest.webApplicationInfo.resource
        $resource = [regex]::Replace($resource, $anyGuidRegex, $AppId)
        $resource = $resource -replace [regex]::Escape($placeholderDomain), $BotDomain
        $manifest.webApplicationInfo.resource = $resource
    }
}

if ($manifest.PSObject.Properties.Name -contains 'validDomains') {
    for ($i = 0; $i -lt $manifest.validDomains.Count; $i++) {
        $manifest.validDomains[$i] = $manifest.validDomains[$i] -replace [regex]::Escape($placeholderDomain), $BotDomain
    }
}
if ($manifest.PSObject.Properties.Name -contains 'developer') {
    foreach ($prop in @('websiteUrl', 'privacyUrl', 'termsOfUseUrl')) {
        if ($manifest.developer.PSObject.Properties.Name -contains $prop) {
            $manifest.developer.$prop = $manifest.developer.$prop -replace [regex]::Escape($placeholderDomain), $BotDomain
        }
    }
}

# Serialize with generous depth — the Teams manifest only nests a few levels
# but ConvertTo-Json's default depth of 2 would truncate composeExtensions.
$substituted = $manifest | ConvertTo-Json -Depth 32

# --- Post-substitution safety checks --------------------------------------
# A successful exit must never produce a non-deployable package. Fail loudly
# if any placeholder slipped through (e.g. because the template grew a new
# field that this script does not yet know how to rewrite).
if ($substituted -match [regex]::Escape($placeholderGuid)) {
    throw "Generated manifest still contains the placeholder GUID '$placeholderGuid'. Update package-teams-app.ps1 to substitute the new field."
}
if ($substituted -match [regex]::Escape($placeholderDomain)) {
    throw "Generated manifest still contains the placeholder host '$placeholderDomain'. Update package-teams-app.ps1 to substitute the new field."
}

# Validate the substituted JSON parses cleanly before we ship it.
try {
    $null = $substituted | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "Substituted manifest is not valid JSON: $($_.Exception.Message)"
}

# --- Write output ---------------------------------------------------------
$outputDir = Split-Path -Parent $OutputPath
if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("agentswarm-teams-pkg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Value $substituted -Encoding UTF8 -NoNewline
    Copy-Item -LiteralPath $colorIconPath -Destination (Join-Path $staging 'color.png')
    Copy-Item -LiteralPath $outlineIconPath -Destination (Join-Path $staging 'outline.png')

    if (Test-Path -LiteralPath $OutputPath) {
        Remove-Item -LiteralPath $OutputPath -Force
    }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutputPath -Force
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

$resolved = (Resolve-Path -LiteralPath $OutputPath).Path
Write-Output "teams-app.zip -> $resolved"
