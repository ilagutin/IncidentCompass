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
COPY src/IncidentCompass.Api/IncidentCompass.Api.csproj src/IncidentCompass.Api/
COPY src/IncidentCompass.Api/packages.lock.json src/IncidentCompass.Api/
RUN dotnet restore src/IncidentCompass.Api/IncidentCompass.Api.csproj --locked-mode

COPY . .
RUN dotnet publish src/IncidentCompass.Api/IncidentCompass.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    groupadd --system incidentcompass && \
    useradd --system --gid incidentcompass --home-dir /app incidentcompass && \
    chown -R incidentcompass:incidentcompass /app

COPY --chown=incidentcompass:incidentcompass --from=build /app/publish ./
COPY --chown=incidentcompass:incidentcompass config ./config

ENV ASPNETCORE_URLS=http://+:8080 \
    IncidentCompass__ConfigSource__Path=/app/config/incidentcompass.config.json

EXPOSE 8080
USER incidentcompass
ENTRYPOINT ["dotnet", "IncidentCompass.Api.dll"]