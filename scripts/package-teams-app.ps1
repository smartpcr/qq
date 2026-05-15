#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build a Microsoft Teams app sideload package (teams-app.zip) for AgentSwarm.

.DESCRIPTION
    Reads the canonical manifest template at
    `src/AgentSwarm.Messaging.Teams/Manifest/manifest.json`, substitutes the
    Teams-app id, the bot framework MicrosoftAppId, and the app version into the
    manifest, bundles the result with the color/outline icons, and produces a
    sideloadable zip at the requested output path.

    Substitution map:
        AppId       -> top-level `id`
                       `webApplicationInfo.id`
                       GUID component of `webApplicationInfo.resource`
        BotId       -> `bots[0].botId`
                       `composeExtensions[0].botId`
        Version     -> top-level `version`
        BotDomain   -> (optional) `validDomains[*]`
                       developer.websiteUrl / privacyUrl / termsOfUseUrl
                       host portion of `webApplicationInfo.resource`

    The script fails (non-zero exit code) on invalid input GUIDs or empty
    version strings, so CI can rely on exit status to gate the package.

.PARAMETER AppId
    The Teams app's Microsoft Entra (AAD) application id. Used for the
    top-level `id` and the SSO `webApplicationInfo.id`. Must be a GUID and
    must not be the all-zero placeholder GUID.

.PARAMETER BotId
    The bot framework registration id (MicrosoftAppId). Used for
    `bots[0].botId` and `composeExtensions[0].botId`. Must be a GUID and
    must not be the all-zero placeholder GUID.

.PARAMETER Version
    The app version emitted into the manifest's top-level `version` field.
    Required; should be a SemVer `MAJOR.MINOR.PATCH` string.

.PARAMETER OutputPath
    Path of the output zip file (typically `teams-app.zip`). Parent directories
    are created if missing. An existing file at this path is overwritten.

.PARAMETER BotDomain
    Optional fully-qualified domain of the bot endpoint. When supplied, the
    placeholder host `bot.example.com` is rewritten across `validDomains`,
    `developer.*Url`, and `webApplicationInfo.resource`. When omitted, the
    placeholder remains and the script writes a warning to stderr — useful
    for local smoke tests, but never appropriate for tenant deployment.

.EXAMPLE
    pwsh ./scripts/package-teams-app.ps1 `
        -AppId 11111111-2222-3333-4444-555555555555 `
        -BotId 66666666-7777-8888-9999-aaaaaaaaaaaa `
        -Version 1.0.0 `
        -OutputPath ./artifacts/teams-app.zip

.EXAMPLE
    pwsh ./scripts/package-teams-app.ps1 `
        -AppId 11111111-2222-3333-4444-555555555555 `
        -BotId 66666666-7777-8888-9999-aaaaaaaaaaaa `
        -Version 1.0.0 `
        -OutputPath ./artifacts/teams-app.zip `
        -BotDomain bots.contoso.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppId,

    [Parameter(Mandatory = $true)]
    [string]$BotId,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $false)]
    [string]$BotDomain
)

$ErrorActionPreference = 'Stop'

$placeholderGuid = '00000000-0000-0000-0000-000000000000'
$placeholderDomain = 'bot.example.com'
$guidRegex = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$semverRegex = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?(?:\+[0-9A-Za-z.\-]+)?$'
$anyGuidRegex = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

# --- Input validation ----------------------------------------------------
if ($AppId -notmatch $guidRegex) {
    throw "-AppId '$AppId' is not a valid GUID."
}
if ($AppId -eq $placeholderGuid) {
    throw "-AppId must not be the placeholder GUID '$placeholderGuid'."
}
if ($BotId -notmatch $guidRegex) {
    throw "-BotId '$BotId' is not a valid GUID."
}
if ($BotId -eq $placeholderGuid) {
    throw "-BotId must not be the placeholder GUID '$placeholderGuid'."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "-Version must be a non-empty version string."
}
if ($Version -notmatch $semverRegex) {
    throw "-Version '$Version' is not a valid SemVer (MAJOR.MINOR.PATCH)."
}
if ($PSBoundParameters.ContainsKey('BotDomain') -and (
        [string]::IsNullOrWhiteSpace($BotDomain) -or $BotDomain -eq $placeholderDomain)) {
    throw "-BotDomain must be a non-placeholder FQDN (got '$BotDomain')."
}

# Normalise GUIDs to lowercase to match Microsoft Teams conventions
$AppId = $AppId.ToLowerInvariant()
$BotId = $BotId.ToLowerInvariant()

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
$manifest.bots[0].botId = $BotId

if ($manifest.PSObject.Properties.Name -contains 'composeExtensions') {
    foreach ($ext in $manifest.composeExtensions) {
        if ($ext.PSObject.Properties.Name -contains 'botId') {
            $ext.botId = $BotId
        }
    }
}

if ($manifest.PSObject.Properties.Name -contains 'webApplicationInfo') {
    $manifest.webApplicationInfo.id = $AppId
    if ($manifest.webApplicationInfo.PSObject.Properties.Name -contains 'resource') {
        $resource = [string]$manifest.webApplicationInfo.resource
        $resource = [regex]::Replace($resource, $anyGuidRegex, $AppId)
        if ($PSBoundParameters.ContainsKey('BotDomain')) {
            $resource = $resource -replace [regex]::Escape($placeholderDomain), $BotDomain
        }
        $manifest.webApplicationInfo.resource = $resource
    }
}

if ($PSBoundParameters.ContainsKey('BotDomain')) {
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
}
else {
    Write-Warning "BotDomain not supplied: placeholder '$placeholderDomain' will remain in validDomains/developer URLs/webApplicationInfo.resource. Do not deploy this package to a production tenant."
}

# Serialize with generous depth — the Teams manifest only nests a few levels
# but ConvertTo-Json's default depth of 2 would truncate composeExtensions.
$substituted = $manifest | ConvertTo-Json -Depth 32

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
