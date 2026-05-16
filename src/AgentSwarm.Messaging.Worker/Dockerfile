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
