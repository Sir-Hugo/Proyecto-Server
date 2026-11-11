# syntax=docker/dockerfile:1.7
FROM node:24-alpine AS web-build
WORKDIR /src/web-client
COPY web-client/package.json web-client/package-lock.json ./
RUN npm ci --ignore-scripts
COPY web-client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY Directory.Build.props ProyectoServer.slnx ./
COPY src/Game.Domain/Game.Domain.csproj src/Game.Domain/
COPY src/Game.Infrastructure/Game.Infrastructure.csproj src/Game.Infrastructure/
COPY src/Game.Server/Game.Server.csproj src/Game.Server/
COPY tools/Game.Database/Game.Database.csproj tools/Game.Database/
RUN dotnet restore src/Game.Server/Game.Server.csproj && dotnet restore tools/Game.Database/Game.Database.csproj
COPY src/ src/
COPY tools/ tools/
RUN dotnet publish src/Game.Server/Game.Server.csproj --configuration Release --no-restore --output /out/server \
    && dotnet publish tools/Game.Database/Game.Database.csproj --configuration Release --no-restore --output /out/database
COPY --from=web-build /src/web-client/dist/ /out/server/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
COPY --from=dotnet-build --chown=$APP_UID:$APP_UID /out/server/ ./
COPY --from=dotnet-build --chown=$APP_UID:$APP_UID /out/database/ ./database/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Game.Server.dll"]
