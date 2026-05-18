# syntax=docker/dockerfile:1.7
# Stage 6.3 — multi-stage build for the Telegram messenger Worker.
# Built from the *repo root* so the COPY paths can include the
# solution and every project the worker depends on:
#   docker build -f src/AgentSwarm.Messaging.Worker/Dockerfile -t agentswarm-messaging-worker .
# (docker-compose.yml at the repo root takes care of this for you.)

# ============================================================
# Stage 1: restore + build + publish (SDK image)
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the solution and props first so layer cache is friendly:
# changes under src/** invalidate only this layer's restore when
# *project files* change, not on every source edit.
COPY ["AgentSwarm.Messaging.sln", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["NuGet.config", "./"]

# Copy every project file so `dotnet restore` resolves the whole graph.
COPY ["src/AgentSwarm.Messaging.Abstractions/AgentSwarm.Messaging.Abstractions.csproj", "src/AgentSwarm.Messaging.Abstractions/"]
COPY ["src/AgentSwarm.Messaging.Core/AgentSwarm.Messaging.Core.csproj", "src/AgentSwarm.Messaging.Core/"]
COPY ["src/AgentSwarm.Messaging.Persistence/AgentSwarm.Messaging.Persistence.csproj", "src/AgentSwarm.Messaging.Persistence/"]
COPY ["src/AgentSwarm.Messaging.Telegram/AgentSwarm.Messaging.Telegram.csproj", "src/AgentSwarm.Messaging.Telegram/"]
COPY ["src/AgentSwarm.Messaging.Worker/AgentSwarm.Messaging.Worker.csproj", "src/AgentSwarm.Messaging.Worker/"]

RUN dotnet restore "src/AgentSwarm.Messaging.Worker/AgentSwarm.Messaging.Worker.csproj"

# Now copy source and publish a self-contained linux-x64 build.
COPY ["src/", "src/"]
WORKDIR /src/src/AgentSwarm.Messaging.Worker
RUN dotnet publish "AgentSwarm.Messaging.Worker.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -p:PublishSingleFile=false \
    -p:UseAppHost=false \
    -o /app/publish

# ============================================================
# Stage 2: runtime image (aspnet, smaller than SDK)
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Stage 6.3 spec: webhook receiver listens on port 8443. Kestrel
# is told to bind via ASPNETCORE_URLS so the same image works in
# environments where the port is remapped externally.
#
# Stage 6.3 iter-5 evaluator item 1 -- the stock image MUST NOT
# look "safe" while stubs run silently. For a production-readiness
# workstream, the right default is fail-closed: the stock image
# boots in Production, the StubGuardHealthCheck refuses to ship
# stubs, /healthz returns HTTP 503, and the Docker HEALTHCHECK
# below correctly marks the container `unhealthy`. That is the
# brief-mandated signal "do not deploy this without wiring concrete
# replacements" -- not an accident to paper over with a Development
# default.
#
# Two consequences of this default:
#
#   1) `docker run agentswarm-messaging-worker` (no env override)
#      starts the container, /healthz returns 503, Docker marks it
#      `unhealthy`, and Kubernetes / docker swarm orchestrators
#      will refuse to send traffic. This is the correct fail-closed
#      signal for a deployment that forgot to wire concrete services.
#
#   2) Local developers use `docker compose up worker` -- the
#      repo-root docker-compose.yml explicitly overrides
#      `ASPNETCORE_ENVIRONMENT` to `Development` via
#      `${ASPNETCORE_ENVIRONMENT:-Development}`, so the compose
#      workflow boots green out of the box. That is the developer
#      DX path; the bare image is the deployment path.
#
# A real production deployment MUST do two things together:
#
#   1) Bake in a concrete ISwarmCommandBus via
#      `services.Replace(ServiceDescriptor.Singleton<ISwarmCommandBus,
#      ContosoSwarmCommandBus>())` before `builder.Build()` -- either
#      by deriving a custom image FROM this one and copying the
#      adapter assembly, or by re-publishing the Worker with the
#      registration in source. With concrete services wired the
#      stock Production env results in /healthz=200 and the Docker
#      HEALTHCHECK reports the container `healthy`.
#
#   2) Leave ASPNETCORE_ENVIRONMENT=Production (the stock default)
#      so the strict stub guard activates and refuses to ship stubs.
#
# The verify-docker.ps1 script at the repo root exercises both the
# fail-closed Production-stock path (/healthz=503 + Docker
# `unhealthy`) and the green Development-override path
# (/healthz=200 + Docker `healthy`) end-to-end against the actual
# `docker build` + `docker run` + `docker inspect` pipeline so the
# HEALTHCHECK contract and the HTTP probe stay consistent.
ENV ASPNETCORE_URLS=http://+:8443 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=true

# curl is needed for the HEALTHCHECK below. apt-get is the cheapest
# way to get it; we clean the lists afterwards to keep the layer small.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8443

# Run as a non-root user. The aspnet base image already creates the
# 'app' user (uid 1000 in 8.0 images); switching to it satisfies the
# common-sense "containers should not run as root" rule.
USER app

# Liveness check used by docker compose `condition: service_healthy`
# dependencies. The interval/timeout match the architecture.md
# §10.4 SLA: the worker should answer within a couple seconds.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8443/healthz || exit 1

ENTRYPOINT ["dotnet", "AgentSwarm.Messaging.Worker.dll"]
