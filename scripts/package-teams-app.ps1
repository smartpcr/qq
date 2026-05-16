<#
.SYNOPSIS
  Build the Microsoft Teams sideloading package (teams-app.zip).

.DESCRIPTION
  Renders the canonical manifest.json template at
  `src/AgentSwarm.Messaging.Teams/Manifest/manifest.json` by stamping in the
  supplied AppId, BotId, and Version, then bundles the rendered manifest together
  with `color.png` and `outline.png` into a single sideloading ZIP. The output
  layout is the one Microsoft Teams expects when a tenant administrator uploads a
  custom app: `manifest.json`, `color.png`, and `outline.png` at the archive root.

.PARAMETER AppId
  The Entra ID app-registration GUID. Used as the top-level `id`, as
  `webApplicationInfo.id`, and as the resource identifier baked into
  `webApplicationInfo.resource`. MUST be a syntactically valid GUID; an
  invalid value causes the script to fail with a non-zero exit code and an
  error message that contains the literal string "AppId".

.PARAMETER BotId
  The Bot Framework registration GUID. Used for every `bots[].botId` and every
  `composeExtensions[].botId` entry. MUST be a syntactically valid GUID; an
  invalid value causes the script to fail with a non-zero exit code and an
  error message that contains the literal string "BotId".

.PARAMETER Version
  Semantic version stamp written into the manifest's `version` field. Must be
  non-empty.

.PARAMETER OutputPath
  Absolute path to the destination ZIP file. The parent directory MUST exist.
  If the destination file already exists it is overwritten.

.EXAMPLE
  pwsh -NoProfile -File scripts/package-teams-app.ps1 `
      -AppId '11111111-2222-3333-4444-555555555555' `
      -BotId '66666666-7777-8888-9999-aaaaaaaaaaaa' `
      -Version '1.0.0' `
      -OutputPath C:/temp/teams-app.zip

.NOTES
  Cross-platform — requires PowerShell 7 (`pwsh`). Used by
  `tests/AgentSwarm.Messaging.Teams.Manifest.Tests/PackagingScriptSmokeTests.cs`
  as the build-gate smoke test for the Teams app manifest workstream.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $AppId,
    [Parameter(Mandatory = $true)] [string] $BotId,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

function Assert-GuidParam {
    param(
        [Parameter(Mandatory = $true)] [string] $Value,
        [Parameter(Mandatory = $true)] [string] $ParamName
    )
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($Value, [ref] $parsed)) {
        throw "Invalid $ParamName GUID: '$Value'. The $ParamName parameter must be a syntactically valid GUID (form: 00000000-0000-0000-0000-000000000000)."
    }
    return $parsed.ToString('D')
}

try {
    $normalizedAppId = Assert-GuidParam -Value $AppId -ParamName 'AppId'
    $normalizedBotId = Assert-GuidParam -Value $BotId -ParamName 'BotId'

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw 'Version must be a non-empty string.'
    }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        throw 'OutputPath must be a non-empty string.'
    }

    # Locate source artifacts relative to the script's own directory so the script
    # works from any CWD (CI, local dev, test harness all invoke with different cwd).
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
    $manifestDir = Join-Path $repoRoot 'src/AgentSwarm.Messaging.Teams/Manifest'
    $manifestSource = Join-Path $manifestDir 'manifest.json'
    $colorSource = Join-Path $manifestDir 'color.png'
    $outlineSource = Join-Path $manifestDir 'outline.png'

    foreach ($p in @($manifestSource, $colorSource, $outlineSource)) {
        if (-not (Test-Path -LiteralPath $p)) {
            throw "Required manifest artifact missing: '$p'."
        }
    }

    # Stage rendered manifest + icons in a unique temp directory so concurrent
    # invocations (e.g. parallel xUnit fixtures) do not race on the same files.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("teams-pkg-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        # Parse the template into a mutable graph, stamp the supplied identifiers
        # everywhere they are required, then write it back as JSON. We use
        # PSCustomObject (default ConvertFrom-Json) rather than -AsHashtable because
        # the round-trip via ConvertTo-Json preserves property order, which keeps
        # the rendered manifest diff-friendly when comparing across runs.
        $rawTemplate = Get-Content -LiteralPath $manifestSource -Raw
        $manifest = $rawTemplate | ConvertFrom-Json -Depth 64

        $manifest.id = $normalizedAppId
        $manifest.version = $Version

        if ($manifest.PSObject.Properties['bots']) {
            foreach ($bot in @($manifest.bots)) {
                $bot.botId = $normalizedBotId
            }
        }

        if ($manifest.PSObject.Properties['composeExtensions']) {
            foreach ($ce in @($manifest.composeExtensions)) {
                $ce.botId = $normalizedBotId
            }
        }

        if ($manifest.PSObject.Properties['webApplicationInfo']) {
            $manifest.webApplicationInfo.id = $normalizedAppId
            if ($manifest.webApplicationInfo.PSObject.Properties['resource']) {
                $manifest.webApplicationInfo.resource = "api://bot.example.com/$normalizedAppId"
            }
        }

        $renderedJson = $manifest | ConvertTo-Json -Depth 64

        $stagedManifest = Join-Path $staging 'manifest.json'
        $stagedColor = Join-Path $staging 'color.png'
        $stagedOutline = Join-Path $staging 'outline.png'

        # Set-Content with -Encoding utf8NoBOM keeps the manifest BOM-free; some
        # Teams tooling has historically choked on a BOM at the start of the file.
        Set-Content -LiteralPath $stagedManifest -Value $renderedJson -Encoding utf8NoBOM
        Copy-Item -LiteralPath $colorSource -Destination $stagedColor -Force
        Copy-Item -LiteralPath $outlineSource -Destination $stagedOutline -Force

        if (Test-Path -LiteralPath $OutputPath) {
            Remove-Item -LiteralPath $OutputPath -Force
        }

        # Pass each file as a separate -LiteralPath entry so Compress-Archive places
        # them at the ZIP root rather than nesting them under the staging directory.
        Compress-Archive `
            -LiteralPath @($stagedManifest, $stagedColor, $stagedOutline) `
            -DestinationPath $OutputPath `
            -CompressionLevel Optimal `
            -Force

        Write-Host "Wrote Teams app package to '$OutputPath' (AppId=$normalizedAppId, BotId=$normalizedBotId, Version=$Version)."
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
catch {
    # Funnel every failure through stderr so the .NET smoke test
    # (`PackagingScriptSmokeTests.RunPackaging`) can surface the message in its
    # `PackagingScriptFailedException`. Explicit exit 1 guards against any host that
    # would otherwise mask an uncaught throw as a zero exit code.
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
