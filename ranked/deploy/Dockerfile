# Ranked backend + everything needed to launch dedicated OpenRA game servers for matches.
# Build context: a staging dir created by deploy.sh (backend/ source + sdk/ game files).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/ ./backend/
RUN dotnet publish backend/Ranked.Backend.csproj -c Release -o /out

# The OpenRA engine targets .NET 6; borrow that runtime for the game server processes.
FROM mcr.microsoft.com/dotnet/runtime:6.0 AS runtime6

FROM mcr.microsoft.com/dotnet/aspnet:8.0
COPY --from=runtime6 /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY sdk/ /app/sdk/
WORKDIR /app/backend
COPY --from=build /out ./
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Ranked__GameServers__SdkDir=/app/sdk \
    Ranked__GameServers__DotnetPath=dotnet
ENTRYPOINT ["dotnet", "Ranked.Backend.dll"]
