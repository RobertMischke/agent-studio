FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish testsupport/scenario/DeploymentScenario.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "DeploymentScenario.dll"]
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "DeploymentScenario.dll"]
