# Mehrstufiger Build: Web-Oberfläche (Node) + Backend (.NET) → schlankes ASP.NET-Image.
# Web und .NET werden auf der Plattform des Build-Rechners gebaut (Ergebnis ist architekturunabhängig);
# nur das End-Image gibt es je Zielarchitektur (linux/amd64, linux/arm64).
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build -- --outDir /out/wwwroot --emptyOutDir

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG VERSION=0.1.0-dev
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Ylm.Contracts/Ylm.Contracts.csproj src/Ylm.Contracts/
COPY src/Ylm.Core/Ylm.Core.csproj src/Ylm.Core/
COPY src/Ylm.Server/Ylm.Server.csproj src/Ylm.Server/
RUN dotnet restore src/Ylm.Server/Ylm.Server.csproj
COPY src/Ylm.Contracts/ src/Ylm.Contracts/
COPY src/Ylm.Core/ src/Ylm.Core/
COPY src/Ylm.Server/ src/Ylm.Server/
RUN dotnet publish src/Ylm.Server/Ylm.Server.csproj -c Release -o /out/app --no-restore -p:Version=${VERSION} \
    && mkdir -p /out/data

# Laufzeit: "chiseled" Ubuntu-Image (nur .NET-Laufzeit, ICU und Zeitzonen; keine Shell, kein Paketmanager),
# dadurch kaum Pakete mit Schwachstellen. Neu gebaut wöchentlich bzw. bei Schwachstellen (rebuild.yml).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out/app ./
COPY --from=web /out/wwwroot ./wwwroot
# Agent-MSI zum Download und für das Auto-Update (nur im Release-Build vorhanden).
COPY agent-package/ ./agent/
COPY --from=build --chown=$APP_UID:$APP_UID /out/data /data
ENV ASPNETCORE_URLS=http://+:8080 \
    Ylm__DataDirectory=/data
VOLUME /data
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD ["dotnet", "Ylm.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Ylm.Server.dll"]
