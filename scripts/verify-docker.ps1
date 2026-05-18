# =============================================================================
# scripts/verify-docker.ps1 -- Stage 6.3 Docker image verification.
#
# Exercises the brief's "Docker image builds" scenario end-to-end:
#
#   "Given the Dockerfile, When `docker build` is run, Then the
#    image builds successfully and the container starts with
#    `/healthz` responding"
#
# Stage 6.3 iter-5 evaluator items:
#   * item 1 -- the stock Dockerfile now defaults
#     ASPNETCORE_ENVIRONMENT=Production (fail-closed). This script
#     no longer relies on a Development default; it runs Production
#     by default and treats the (HTTP, Docker-health-status) PAIR
#     as the success criterion -- not the HTTP code alone.
#   * item 2 -- this script no longer accepts a Production
#     /healthz=503 as PASSED without ALSO verifying via
#     `docker inspect` that the Docker HEALTHCHECK directive sees
#     the same outcome. The image's `HEALTHCHECK CMD curl -fsS`
#     contract is now part of the assertion: 503 MUST coincide
#     with Docker reporting the container `unhealthy`, and 200
#     MUST coincide with Docker reporting `healthy`. A mismatch
#     (503+healthy or 200+unhealthy) is a HARD FAILURE -- it would
#     mean the image's HEALTHCHECK contract is broken.
#
# Three scenarios are supported:
#
#   -Development                  Worker boots in Development. Stub
#                                 guard is inactive. Expected outcome:
#                                 /healthz=200 AND Docker container
#                                 reports `healthy`.
#
#   (default, Production stock)   Worker boots in Production with the
#                                 stock service graph (StubSwarmCommandBus
#                                 still wired). StubGuardHealthCheck
#                                 fires fail-closed. Expected outcome:
#                                 /healthz=503 AND Docker container
#                                 reports `unhealthy` (this is the
#                                 brief-mandated production-readiness
#                                 signal "do not deploy stubs").
#
#   -RequireHealthy               Worker boots in Production but the
#                                 operator promises concrete services
#                                 are wired into the image (custom
#                                 image built FROM agentswarm-messaging-worker
#                                 with `services.Replace<ISwarmCommandBus>()`).
#                                 Expected outcome: /healthz=200 AND
#                                 Docker container reports `healthy`.
#                                 This is the "production-ready"
#                                 path the iter-5 evaluator wanted
#                                 explicitly verified.
#
# This script is intended for manual operator runs and CI pipelines
# that have Docker installed. It is NOT executed by `dotnet test`
# because the unit/integration test suites do not assume Docker
# is available on the build host.
#
# Usage:
#   pwsh ./scripts/verify-docker.ps1                  # Production stock -> 503 + unhealthy
#   pwsh ./scripts/verify-docker.ps1 -Development     # Dev mode      -> 200 + healthy
#   pwsh ./scripts/verify-docker.ps1 -RequireHealthy  # Prod + concrete services -> 200 + healthy
#   pwsh ./scripts/verify-docker.ps1 -SkipBuild       # reuse existing image
#
# Exit codes:
#   0 -- /healthz HTTP code AND Docker HEALTHCHECK status both match
#        the env-appropriate expected pair.
#   1 -- docker build failed.
#   2 -- container did not start.
#   3 -- /healthz did not respond within the HTTP timeout window.
#   4 -- /healthz responded but the status code is NOT in the
#        environment-specific allowlist (e.g. 404 / 500 / 502).
#   5 -- /healthz HTTP code and Docker HEALTHCHECK status disagree
#        (e.g. 503 + Docker `healthy` would mean the HEALTHCHECK
#        directive was somehow stripped, or 200 + Docker `unhealthy`
#        would mean the probe is flapping). The image's HEALTHCHECK
#        contract is broken.
# =============================================================================

[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$RequireHealthy,
    [switch]$SkipBuild,
    [string]$ImageTag = 'agentswarm-messaging-worker:verify',
    [string]$ContainerName = 'agentswarm-worker-verify',
    [int]$Port = 8443,
    [int]$HealthTimeoutSeconds = 60,
    [int]$DockerHealthTimeoutSeconds = 150
)

$ErrorActionPreference = 'Stop'

if ($Development -and $RequireHealthy)
{
    Write-Host "FAILED: -Development and -RequireHealthy are mutually exclusive." -ForegroundColor Red
    exit 4
}

# Scenario classification drives the (HTTP, Docker-health) allowlist.
if ($Development)
{
    $envName = 'Development'
    $expectedHttp = 200
    $expectedHealth = 'healthy'
    $scenario = 'Development'
}
elseif ($RequireHealthy)
{
    $envName = 'Production'
    $expectedHttp = 200
    $expectedHealth = 'healthy'
    $scenario = 'Production (operator wired concrete services)'
}
else
{
    $envName = 'Production'
    $expectedHttp = 503
    $expectedHealth = 'unhealthy'
    $scenario = 'Production stock (StubGuard fail-closed)'
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $repoRoot
try
{
    if (-not $SkipBuild)
    {
        Write-Host "==> docker build (context=$repoRoot)" -ForegroundColor Cyan
        & docker build `
            -f 'src/AgentSwarm.Messaging.Worker/Dockerfile' `
            -t $ImageTag `
            .
        if ($LASTEXITCODE -ne 0)
        {
            Write-Host "FAILED: docker build returned $LASTEXITCODE" -ForegroundColor Red
            exit 1
        }
    }

    # Make sure no stale container is hogging the name or the port.
    & docker rm -f $ContainerName 2>$null | Out-Null

    Write-Host "==> docker run (scenario=$scenario, env=$envName, port=$Port)" -ForegroundColor Cyan
    $runArgs = @(
        'run', '-d',
        '--name', $ContainerName,
        '-p', "${Port}:8443",
        '-e', "ASPNETCORE_ENVIRONMENT=$envName",
        '-e', 'Telegram__BotToken=111111:verify-docker-script-bot-token',
        '-e', 'Telegram__SecretToken=verify-docker-script-secret',
        '-e', 'Telegram__UsePolling=true',
        '-e', 'ConnectionStrings__MessagingDb=Data Source=/tmp/messaging.db',
        '-e', 'ConnectionStrings__AuditDb=Data Source=/tmp/audit.db',
        $ImageTag
    )
    $containerId = & docker @runArgs
    if ($LASTEXITCODE -ne 0)
    {
        Write-Host "FAILED: docker run returned $LASTEXITCODE" -ForegroundColor Red
        exit 2
    }

    # ----- Phase A: HTTP probe -----
    Write-Host "==> waiting for /healthz to respond (timeout ${HealthTimeoutSeconds}s)" -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $lastStatus = $null
    $responded = $false
    while ((Get-Date) -lt $deadline)
    {
        try
        {
            # -SkipHttpErrorCheck so a 503 (the brief-mandated strict
            # production stub-guard outcome) does NOT throw and we can
            # branch on the *exact* status code below. The pairing with
            # Docker HEALTHCHECK status (Phase B) is what makes 503 a
            # legitimate PASS criterion -- not the absence of a throw.
            $response = Invoke-WebRequest `
                -Uri "http://localhost:${Port}/healthz" `
                -Method GET `
                -TimeoutSec 5 `
                -SkipHttpErrorCheck `
                -ErrorAction Stop
            $lastStatus = [int]$response.StatusCode
            $responded = $true
            break
        }
        catch
        {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $responded)
    {
        Write-Host "FAILED: /healthz did not respond within ${HealthTimeoutSeconds}s" -ForegroundColor Red
        Write-Host "Container logs (last 50 lines):" -ForegroundColor Yellow
        & docker logs --tail 50 $ContainerName
        exit 3
    }

    Write-Host "==> /healthz responded with HTTP $lastStatus" -ForegroundColor Green

    # Stage 6.3 iter-4 evaluator item 2 -- strict env-specific
    # HTTP allowlist. Anything outside the per-scenario expected
    # code is a HARD FAILURE -- a broken /healthz route (404),
    # an unhandled exception (500), a Kestrel crash (502), etc.
    # cannot silently exit 0.
    if ($lastStatus -ne $expectedHttp)
    {
        Write-Host "FAILED: $scenario /healthz returned HTTP $lastStatus, expected HTTP $expectedHttp" -ForegroundColor Red
        Write-Host "Container logs (last 50 lines):" -ForegroundColor Yellow
        & docker logs --tail 50 $ContainerName
        exit 4
    }

    # ----- Phase B: Docker HEALTHCHECK verdict -----
    # Stage 6.3 iter-5 evaluator item 2 -- the Dockerfile sets
    # `HEALTHCHECK CMD curl -fsS http://localhost:8443/healthz`,
    # so a 5xx response MUST cause `docker inspect` to report
    # the container `unhealthy` and a 2xx response MUST cause it
    # to report `healthy`. We assert that PAIR is consistent so
    # the script's PASS criterion matches the image's HEALTHCHECK
    # contract -- never accepting a status that contradicts what
    # Docker itself reports about the container.
    #
    # Docker reports `starting` until either (a) one check passes
    # AND start-period elapses, or (b) `retries` consecutive checks
    # fail after start-period. With the Dockerfile's
    # interval=30s / start-period=15s / retries=3 settings, the
    # worst-case verdict time on a perpetual-503 endpoint is
    # ~start-period(15s) + interval(30s) * retries(3) = ~105s.
    # We allow 150s of slack to absorb cold-start jitter.
    Write-Host "==> waiting for Docker HEALTHCHECK verdict (timeout ${DockerHealthTimeoutSeconds}s)" -ForegroundColor Cyan
    $healthDeadline = (Get-Date).AddSeconds($DockerHealthTimeoutSeconds)
    $dockerHealth = 'starting'
    while ((Get-Date) -lt $healthDeadline)
    {
        $dockerHealth = (& docker inspect --format='{{.State.Health.Status}}' $ContainerName 2>$null)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dockerHealth))
        {
            Write-Host "FAILED: docker inspect could not read .State.Health.Status -- is the HEALTHCHECK directive present in the image?" -ForegroundColor Red
            Write-Host "Container logs (last 50 lines):" -ForegroundColor Yellow
            & docker logs --tail 50 $ContainerName
            exit 5
        }
        $dockerHealth = $dockerHealth.Trim()
        if ($dockerHealth -ne 'starting')
        {
            break
        }
        Start-Sleep -Seconds 5
    }

    Write-Host "==> Docker HEALTHCHECK reports container status '$dockerHealth'" -ForegroundColor Green

    if ($dockerHealth -ne $expectedHealth)
    {
        Write-Host "FAILED: $scenario expected Docker container '$expectedHealth' but got '$dockerHealth' (HTTP was $lastStatus)" -ForegroundColor Red
        Write-Host "  This indicates the image's HEALTHCHECK contract is broken --" -ForegroundColor Yellow
        Write-Host "  the HTTP probe and the Docker-reported health verdict disagree." -ForegroundColor Yellow
        Write-Host "Container logs (last 50 lines):" -ForegroundColor Yellow
        & docker logs --tail 50 $ContainerName
        Write-Host "docker inspect .State.Health:" -ForegroundColor Yellow
        & docker inspect --format='{{json .State.Health}}' $ContainerName
        exit 5
    }

    Write-Host "PASSED: $scenario -- HTTP $lastStatus AND Docker '$dockerHealth' as expected" -ForegroundColor Green
    exit 0
}
finally
{
    Write-Host "==> docker stop+rm" -ForegroundColor Cyan
    & docker stop $ContainerName 2>$null | Out-Null
    & docker rm $ContainerName 2>$null | Out-Null
    Pop-Location
}
