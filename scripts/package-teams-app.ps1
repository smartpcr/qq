#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build the Microsoft Teams sideloading package (teams-app.zip) for AgentSwarm
    Messaging.

.DESCRIPTION
    Reads the canonical manifest template from
    src/AgentSwarm.Messaging.Teams/Manifest/manifest.json, substitutes the bot AAD
    application id (-AppId), bot id (-BotId), and version (-Version) into the
    canonical placeholder slots, bundles the substituted manifest with the color
    and outline icons from the same directory, and emits a Teams-sideloadable
    zip at the requested -OutputPath.

    Both -AppId and -BotId are validated as GUID strings. The script exits with
    a non-zero code and a clear error message containing the offending parameter
    name when either fails GUID validation — this lets the smoke test suite
    in tests/AgentSwarm.Messaging.Teams.Manifest.Tests/PackagingScriptSmokeTests.cs
    distinguish AppId vs BotId rejections by message content.

    The manifest template uses placeholder GUID
    00000000-0000-0000-0000-000000000000 for the top-level "id" (AppId), the
    bots[0].botId, the composeExtensions[0].botId, and the webApplicationInfo.id.
    Per the Teams v1.16 schema, the top-level id, webApplicationInfo.id, and
    composeExtensions[0].botId fields are the AAD app id; the bots[0].botId is
    the Bot Framework registration id (which in single-resource deployments is
    the same value but may differ). This script substitutes AppId into the
    top-level id (and webApplicationInfo.id), and BotId into bots[0].botId and
    composeExtensions[0].botId — matching the contract asserted by the
    PackagingScriptSmokeTests fixture (`SubstitutesAppIdBotIdAndVersionIntoManifest`).

.PARAMETER AppId
    The bot's Entra (AAD) application/client id. Must be a GUID.

.PARAMETER BotId
    The Bot Framework bot registration id. Must be a GUID. In single-resource
    deployments this is typically the same as -AppId.

.PARAMETER Version
    Semantic version string for the manifest (e.g. "1.0.0"). Written verbatim
    into the manifest's top-level "version" field.

.PARAMETER OutputPath
    Path of the output teams-app.zip file. Required.

.EXAMPLE
    pwsh ./scripts/package-teams-app.ps1 `
        -AppId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f `
        -BotId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f `
        -Version 1.0.0 `
        -OutputPath ./out/teams-app.zip
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
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$guidRegex = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

# Validate -AppId. The error message MUST contain the literal token "AppId" so the
# PackagingScript_RejectsInvalidAppIdGuid smoke test can match on substring.
if ([string]::IsNullOrWhiteSpace($AppId) -or $AppId -notmatch $guidRegex) {
    Write-Error "-AppId '$AppId' is not a valid GUID. Provide a 36-character lowercase GUID for the bot's AAD application id."
    exit 64
}

# Validate -BotId. The error message MUST contain the literal token "BotId" so the
# PackagingScript_RejectsInvalidBotIdGuid smoke test can match on substring.
if ([string]::IsNullOrWhiteSpace($BotId) -or $BotId -notmatch $guidRegex) {
    Write-Error "-BotId '$BotId' is not a valid GUID. Provide a 36-character lowercase GUID for the Bot Framework registration id."
    exit 65
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "-Version must be a non-empty semantic-version string (e.g. 1.0.0)."
    exit 66
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Error "-OutputPath is required."
    exit 67
}

# Resolve repo root by walking up from the script directory until the solution
# file is found. This keeps the script invokable from any CWD (the smoke tests
# invoke it with WorkingDirectory = RepoRoot but real users may run it from
# anywhere).
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = $scriptDir
while ($repoRoot -and -not (Test-Path -LiteralPath (Join-Path $repoRoot 'AgentSwarm.Messaging.sln'))) {
    $parent = Split-Path -Parent $repoRoot
    if ($parent -eq $repoRoot) {
        Write-Error "Could not locate AgentSwarm.Messaging.sln walking up from '$scriptDir'."
        exit 68
    }
    $repoRoot = $parent
}

$manifestDir = Join-Path $repoRoot 'src/AgentSwarm.Messaging.Teams/Manifest'
$manifestPath = Join-Path $manifestDir 'manifest.json'
$colorIconPath = Join-Path $manifestDir 'color.png'
$outlineIconPath = Join-Path $manifestDir 'outline.png'

foreach ($required in @($manifestPath, $colorIconPath, $outlineIconPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Error "Required manifest asset not found: $required"
        exit 69
    }
}

# Ensure the output directory exists.
$outputDir = Split-Path -Parent $OutputPath
if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Read the manifest template as text, parse as a JSON object, and rewrite the
# canonical id / botId / version slots in place. Working at the object level
# (rather than via regex string substitution) keeps the substitution unambiguous
# even when multiple fields share the same placeholder GUID value.
$normalizedAppId = $AppId.ToLowerInvariant()
$normalizedBotId = $BotId.ToLowerInvariant()
$raw = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = $raw | ConvertFrom-Json

$manifest.version = $Version
$manifest.id = $normalizedAppId

if ($manifest.bots -and $manifest.bots.Count -gt 0) {
    $manifest.bots[0].botId = $normalizedBotId
}

if ($manifest.composeExtensions -and $manifest.composeExtensions.Count -gt 0) {
    $manifest.composeExtensions[0].botId = $normalizedBotId
}

if ($manifest.PSObject.Properties.Name -contains 'webApplicationInfo') {
    $manifest.webApplicationInfo.id = $normalizedAppId
    if ($manifest.webApplicationInfo.PSObject.Properties.Name -contains 'resource') {
        $manifest.webApplicationInfo.resource = `
            $manifest.webApplicationInfo.resource -replace '00000000-0000-0000-0000-000000000000', $normalizedAppId
    }
}

$substituted = $manifest | ConvertTo-Json -Depth 32

# Stage the package contents into a fresh temp directory, then zip. The
# manifest.json AT THE ROOT of the zip plus color.png and outline.png AT THE
# ROOT is the canonical Teams sideloading layout (asserted by
# PackagingScript_ZipContainsManifestAndIcons).
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("teams-app-pkg-" + [Guid]::NewGuid().ToString('N'))
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
