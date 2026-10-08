FROM mcr.microsoft.com/dotnet/sdk:10.0

RUN dotnet tool install StockSharp.Odysseus.Mcp --version 1.0.1 --tool-path /opt/mcp \
    && mkdir -p /data/projects /data/market-data

ENV ODYSSEUS_PROJECTS_ROOT=/data/projects \
    ODYSSEUS_MARKET_DATA=/data/market-data \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

WORKDIR /data
ENTRYPOINT ["/opt/mcp/odysseus-mcp"]
