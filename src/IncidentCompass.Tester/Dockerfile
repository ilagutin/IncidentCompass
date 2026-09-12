# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props IncidentCompass.slnx ./
COPY src/IncidentCompass.Tester/IncidentCompass.Tester.csproj src/IncidentCompass.Tester/
COPY src/IncidentCompass.Tester/packages.lock.json src/IncidentCompass.Tester/
RUN dotnet restore src/IncidentCompass.Tester/IncidentCompass.Tester.csproj --locked-mode

COPY src/IncidentCompass.Tester src/IncidentCompass.Tester
COPY samples/incidents/tester-ticket-action-injection.json samples/incidents/tester-ticket-action-injection.json
COPY evaluations/triage/corpus-v1.json evaluations/triage/corpus-v1.json
RUN dotnet publish src/IncidentCompass.Tester/IncidentCompass.Tester.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

RUN groupadd --system incidentcompass && \
    useradd --system --gid incidentcompass --home-dir /app incidentcompass && \
    chown -R incidentcompass:incidentcompass /app

COPY --chown=incidentcompass:incidentcompass --from=build /app/publish ./
COPY --chown=incidentcompass:incidentcompass evaluations/triage/corpus-v1.json /app/evaluations/triage/corpus-v1.json

ENV INCIDENTCOMPASS_TESTER_BASE_URL=http://api:8080

USER incidentcompass
ENTRYPOINT ["dotnet", "IncidentCompass.Tester.dll"]
