#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build a Microsoft Teams app sideload package (manifest.zip) for AgentSwarm.

.DESCRIPTION
    Reads manifest.json, color.png, and outline.png from this directory, substitutes
    the bot AAD application id and bot endpoint domain placeholders into the manifest,
    and produces a sideloadable manifest.zip archive at the requested output path.

    The placeholder GUID 00000000-0000-0000-0000-000000000000 in manifest.json is
    replaced with the value of -AppId in three locations:
      * top-level "id"
      * bots[0].botId
      * webApplicationInfo.id
      * webApplicationInfo.resource (preserves the api://<domain>/<appId> form)

    The placeholder domain "bot.example.com" is replaced with the value of -BotDomain.

    The script fails (exit non-zero) if either placeholder remains in the substituted
    output, guarding against missed substitutions.

.PARAMETER AppId
    The bot's Entra (AAD) application/client id. Must be a GUID and must not be the
    all-zero placeholder GUID.

.PARAMETER BotDomain
    The fully-qualified domain name (FQDN) of the bot endpoint. Used for validDomains,
    developer URLs, and webApplicationInfo.resource. Must be a non-empty string and
    must not be the placeholder "bot.example.com".

.PARAMETER OutputPath
    Path of the output manifest.zip file. Defaults to "<this-folder>/bin/manifest.zip"
    so the artifact stays under bin/ (which is gitignored).

.EXAMPLE
    pwsh ./build-manifest.ps1 -AppId 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f -BotDomain bots.contoso.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppId,

    [Parameter(Mandatory = $true)]
    [string]$BotDomain,

    [Parameter(Mandatory = $false)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$placeholderAppId = '00000000-0000-0000-0000-000000000000'
$placeholderDomain = 'bot.example.com'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifestPath = Join-Path $scriptDir 'manifest.json'
$colorIconPath = Join-Path $scriptDir 'color.png'
$outlineIconPath = Join-Path $scriptDir 'outline.png'

foreach ($required in @($manifestPath, $colorIconPath, $outlineIconPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required file not found: $required"
    }
}

# Validate inputs
$guidRegex = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
if ($AppId -notmatch $guidRegex) {
    throw "-AppId '$AppId' is not a valid GUID."
}
if ($AppId -eq $placeholderAppId) {
    throw "-AppId must not be the placeholder GUID '$placeholderAppId'."
}
if ([string]::IsNullOrWhiteSpace($BotDomain) -or $BotDomain -eq $placeholderDomain) {
    throw "-BotDomain must be a non-placeholder FQDN (got '$BotDomain')."
}

# Default output path under ./bin (gitignored)
if (-not $OutputPath) {
    $OutputPath = Join-Path $scriptDir 'bin/manifest.zip'
}
$outputDir = Split-Path -Parent $OutputPath
if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Read source manifest and substitute placeholders
$raw = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$substituted = $raw -replace [regex]::Escape($placeholderAppId), $AppId.ToLowerInvariant()
$substituted = $substituted -replace [regex]::Escape($placeholderDomain), $BotDomain

# Guard against missed substitutions
if ($substituted -match [regex]::Escape($placeholderAppId)) {
    throw "Placeholder app id '$placeholderAppId' remained in substituted manifest."
}
if ($substituted -match [regex]::Escape($placeholderDomain)) {
    throw "Placeholder domain '$placeholderDomain' remained in substituted manifest."
}

# Validate the substituted JSON is still parseable
try {
    $null = $substituted | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "Substituted manifest is not valid JSON: $($_.Exception.Message)"
}

# Stage to a temp directory then zip
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("agentswarm-teams-manifest-" + [Guid]::NewGuid().ToString('N'))
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
Write-Output "manifest.zip -> $resolved"
