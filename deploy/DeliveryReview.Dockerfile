FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Telemetrix.Contracts/ src/Telemetrix.Contracts/
COPY src/Telemetrix.Transformer/ src/Telemetrix.Transformer/
COPY src/Telemetrix.Edge.Bridge/ src/Telemetrix.Edge.Bridge/
COPY tools/Telemetrix.DeliveryProbe/ tools/Telemetrix.DeliveryProbe/
RUN dotnet publish src/Telemetrix.Transformer -c Release -o /out/transformer --no-self-contained -p:UseAppHost=false
RUN dotnet publish src/Telemetrix.Edge.Bridge -c Release -o /out/bridge --no-self-contained -p:UseAppHost=false
RUN dotnet publish tools/Telemetrix.DeliveryProbe -c Release -o /out/probe --no-self-contained -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Npgsql's native GSS negotiation dependency on the Linux runtime image.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM runtime AS transformer
# Python 3 runs operator-supplied transformations when they are enabled (ADR-0016).
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/transformer .
ENTRYPOINT ["dotnet", "Telemetrix.Transformer.dll"]

FROM runtime AS probe
COPY --from=build /out/probe .
ENTRYPOINT ["dotnet", "Telemetrix.DeliveryProbe.dll"]

FROM runtime AS bridge
COPY --from=build /out/bridge .
ENTRYPOINT ["dotnet", "Telemetrix.Edge.Bridge.dll"]
