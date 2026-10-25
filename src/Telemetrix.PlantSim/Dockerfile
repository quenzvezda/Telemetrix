FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Telemetrix.PlantSim/Telemetrix.PlantSim.csproj src/Telemetrix.PlantSim/
COPY src/Telemetrix.Simulation/Telemetrix.Simulation.csproj src/Telemetrix.Simulation/
RUN dotnet restore src/Telemetrix.PlantSim/Telemetrix.PlantSim.csproj
COPY src/Telemetrix.PlantSim/ src/Telemetrix.PlantSim/
COPY src/Telemetrix.Simulation/ src/Telemetrix.Simulation/
RUN dotnet publish src/Telemetrix.PlantSim/Telemetrix.PlantSim.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/pki && chown -R app:app /app/pki
USER app
ENV PlantSim__PkiPath=/app/pki
EXPOSE 4840 5103
ENTRYPOINT ["dotnet", "Telemetrix.PlantSim.dll"]
