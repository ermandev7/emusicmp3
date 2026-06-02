# ── Etapa 1: Build ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiar archivos del proyecto y restaurar dependencias
COPY *.csproj ./
RUN dotnet restore

# Copiar todo y compilar en Release
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Etapa 2: Runtime (imagen mínima ARM64) ────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Instalar Python3 + yt-dlp como extractor nativo de emergencia
RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 python3-pip curl && \
    pip3 install yt-dlp --break-system-packages && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Copiar la app compilada
COPY --from=build /app/publish ./

# Puerto interno de la API
EXPOSE 5050

# Variables de entorno por defecto
ENV ASPNETCORE_URLS=http://+:5050
ENV ASPNETCORE_ENVIRONMENT=Production
# URL del Piped backend INTERNO en Docker (red piped-docker_default)
ENV PIPED_API_URL=http://nginx:80

ENTRYPOINT ["dotnet", "eMusicApi.dll"]
