# Mehrstufiger Build: Web-Oberfläche (Node) + Backend (.NET) → schlankes ASP.NET-Image.
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build -- --outDir /out/wwwroot --emptyOutDir

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
RUN dotnet publish src/Ylm.Server/Ylm.Server.csproj -c Release -o /out/app --no-restore -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/app ./
COPY --from=web /out/wwwroot ./wwwroot
# Agent-MSI zum Download und für das Auto-Update (nur im Release-Build vorhanden).
COPY agent-package/ ./agent/
ENV ASPNETCORE_URLS=http://+:8080 \
    Ylm__DataDirectory=/data
RUN mkdir -p /data && chown app:app /data
VOLUME /data
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD ["dotnet", "Ylm.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Ylm.Server.dll"]
