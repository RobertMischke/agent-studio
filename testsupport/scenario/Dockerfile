# Hermetic Compose scenario driver and fake coding/review CLI.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /src
COPY . ./
RUN dotnet build task-server/TaskServer.csproj -c Release --nologo \
 && dotnet build studio-bff/StudioBff.csproj -c Release --nologo \
 && dotnet build runner/AgentRunner.csproj -c Release --nologo \
 && dotnet build testsupport/scenario/DeploymentScenario.csproj -c Release --nologo
ENTRYPOINT ["dotnet", "/src/testsupport/scenario/bin/Release/net10.0/DeploymentScenario.dll"]
