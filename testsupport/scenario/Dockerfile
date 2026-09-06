# Deterministic deployment regression driver. It contains only the local fixture
# tools used by the fake coding and review CLIs and makes no network calls while
# the scenario is running.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish testsupport/scenario/ScenarioRunner.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git nodejs \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "/app/ScenarioRunner.dll"]
