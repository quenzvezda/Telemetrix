FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Telemetrix.Contracts/Telemetrix.Contracts.csproj src/Telemetrix.Contracts/
COPY src/Telemetrix.Simulation/Telemetrix.Simulation.csproj src/Telemetrix.Simulation/
COPY src/Telemetrix.MqttSim/Telemetrix.MqttSim.csproj src/Telemetrix.MqttSim/
RUN dotnet restore src/Telemetrix.MqttSim/Telemetrix.MqttSim.csproj
COPY src/Telemetrix.Contracts/ src/Telemetrix.Contracts/
COPY src/Telemetrix.Simulation/ src/Telemetrix.Simulation/
COPY src/Telemetrix.MqttSim/ src/Telemetrix.MqttSim/
RUN dotnet publish src/Telemetrix.MqttSim/Telemetrix.MqttSim.csproj -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Telemetrix.MqttSim.dll"]
