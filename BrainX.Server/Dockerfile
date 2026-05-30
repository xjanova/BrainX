# brainx-node — standalone BrainX.Server image (Track A, Phase 0).
# Build from the REPO ROOT so both BrainX.Server and its BrainX.Core
# ProjectReference are in context:
#   docker build -f BrainX.Server/Dockerfile -t brainx-node:latest .
# BrainX.Server targets net10.0; BrainX.Core targets net9.0 — the net10 SDK
# restores/builds both.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# csproj first → restore layer is cached unless dependencies change.
COPY BrainX.Core/BrainX.Core.csproj BrainX.Core/
COPY BrainX.Server/BrainX.Server.csproj BrainX.Server/
RUN dotnet restore BrainX.Server/BrainX.Server.csproj

# then the sources.
COPY BrainX.Core/ BrainX.Core/
COPY BrainX.Server/ BrainX.Server/
# UseAppHost=false → no native launcher; we start via `dotnet BrainX.Server.dll`.
RUN dotnet publish BrainX.Server/BrainX.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl is only here so the /health HEALTHCHECK below works in plain Docker.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./

# Secure-by-default for a standalone node: not embedded, auth required. The
# operator MUST supply BrainX__BearerToken (else write endpoints fail closed).
# Vault is mounted at /vault; TLS terminates at a reverse proxy in front.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0 \
    BrainX__EmbeddedMode=false \
    BrainX__RequireAuth=true \
    BrainX__VaultPath=/vault

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "BrainX.Server.dll"]
