# Discord Bot AI - Docker Image (Zintegrowane Grafiki)
# Multi-stage build for optimized image size

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["Discord Bot AI/Discord Bot AI.csproj", "Discord Bot AI/"]
RUN dotnet restore "Discord Bot AI/Discord Bot AI.csproj"

# Copy source code and build
COPY . .
WORKDIR "/src/Discord Bot AI"
RUN dotnet build -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime (Final)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Utworzenie użytkownika i struktury folderów
RUN groupadd -r botuser && useradd -r -g botuser botuser
RUN mkdir -p /app/data /app/cache/champions /app/cache/items /app/logs


# 1. Kopiowanie opublikowanej aplikacji
COPY --from=publish /app/publish .

# 2. Kopiowanie czcionek
COPY ["Discord Bot AI/Assets/Fonts", "/app/Assets/Fonts"]

# 3. Kopiowanie pobranych grafik Riotu do cache'u kontenera
COPY ["StaticAssets/champions/", "/app/cache/champions/"]
COPY ["StaticAssets/items/", "/app/cache/items/"]

# 4. Nadanie uprawnień botuserowi do wszystkich plików
RUN chown -R botuser:botuser /app

# -------------------------------------------

# Przełączenie na bezpiecznego użytkownika
USER botuser

# Zmienne środowiskowe
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DATA_PATH=/app/data
ENV CACHE_PATH=/app/cache
ENV LOG_PATH=/app/logs
ENV RIOT_VERSION=16.3.1

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD pgrep -f "Discord Bot AI" || exit 1

# Sygnał do bezpiecznego wyłączenia
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "Discord Bot AI.dll"]