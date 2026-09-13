# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props IncidentCompass.slnx ./
COPY src/IncidentCompass.Domain/IncidentCompass.Domain.csproj src/IncidentCompass.Domain/
COPY src/IncidentCompass.Domain/packages.lock.json src/IncidentCompass.Domain/
COPY src/IncidentCompass.Application/IncidentCompass.Application.csproj src/IncidentCompass.Application/
COPY src/IncidentCompass.Application/packages.lock.json src/IncidentCompass.Application/
COPY src/IncidentCompass.Infrastructure/IncidentCompass.Infrastructure.csproj src/IncidentCompass.Infrastructure/
COPY src/IncidentCompass.Infrastructure/packages.lock.json src/IncidentCompass.Infrastructure/
COPY src/IncidentCompass.Worker/IncidentCompass.Worker.csproj src/IncidentCompass.Worker/
COPY src/IncidentCompass.Worker/packages.lock.json src/IncidentCompass.Worker/
RUN dotnet restore src/IncidentCompass.Worker/IncidentCompass.Worker.csproj --locked-mode

COPY . .
RUN dotnet publish src/IncidentCompass.Worker/IncidentCompass.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# /app/models is the local embedding model volume's mount point. Creating it here, owned by the
# runtime user, is what gives a fresh named volume that ownership; nothing is copied into it.
RUN groupadd --system incidentcompass && \
    useradd --system --gid incidentcompass --home-dir /app incidentcompass && \
    mkdir -p /app/models && \
    chown -R incidentcompass:incidentcompass /app

COPY --chown=incidentcompass:incidentcompass --from=build /app/publish ./
COPY --chown=incidentcompass:incidentcompass config ./config
COPY --chown=incidentcompass:incidentcompass samples ./samples

ENV IncidentCompass__ConfigSource__Path=/app/config/incidentcompass.config.json \
    IncidentCompass__Memory__Seed__SourceDirectory=/app/samples

USER incidentcompass
ENTRYPOINT ["dotnet", "IncidentCompass.Worker.dll"]