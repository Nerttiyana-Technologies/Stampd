# syntax=docker/dockerfile:1
# Multi-stage Dockerfile for Stampd.WebApi.
#
# Build context: the repo root (so the build can see all project references).
# Build command: docker build -f src/Stampd.WebApi/Dockerfile -t stampd/webapi .

# ----- Build stage -----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + props first so layer cache stays warm when only source changes.
COPY global.json Directory.Build.props Stampd.slnx ./

# Copy every project's csproj (preserves directory structure) for restore.
COPY src/Stampd.Core/Stampd.Core.csproj                                   src/Stampd.Core/
COPY src/Stampd.Engine/Stampd.Engine.csproj                               src/Stampd.Engine/
COPY src/Stampd.Infrastructure/Stampd.Infrastructure.csproj               src/Stampd.Infrastructure/
COPY src/Stampd.Infrastructure.Sqlite/Stampd.Infrastructure.Sqlite.csproj src/Stampd.Infrastructure.Sqlite/
COPY src/Stampd.Crypto.LocalCertificate/Stampd.Crypto.LocalCertificate.csproj src/Stampd.Crypto.LocalCertificate/
COPY src/Stampd.Crypto.Vault/Stampd.Crypto.Vault.csproj                   src/Stampd.Crypto.Vault/
COPY src/Stampd.Timestamp.FreeTsa/Stampd.Timestamp.FreeTsa.csproj         src/Stampd.Timestamp.FreeTsa/
COPY src/Stampd.Storage.FileSystem/Stampd.Storage.FileSystem.csproj       src/Stampd.Storage.FileSystem/
COPY src/Stampd.WebApi/Stampd.WebApi.csproj                               src/Stampd.WebApi/

RUN dotnet restore src/Stampd.WebApi/Stampd.WebApi.csproj

# Now copy the actual source.
COPY src/ src/

RUN dotnet publish src/Stampd.WebApi/Stampd.WebApi.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ----- Runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install fonts so PDFsharp's text rendering doesn't error out at runtime
# (the PlatformFontResolver expects /usr/share/fonts/.../DejaVuSans.ttf on Linux).
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu fonts-liberation \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Persistent state directories — mount volumes here.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Stampd__Database__SqlitePath=/data/stampd.db \
    Stampd__Storage__FileSystemRoot=/data/documents \
    Stampd__Logging__Directory=/data/logs

RUN mkdir -p /data /data/documents /data/logs

EXPOSE 8080

ENTRYPOINT ["dotnet", "Stampd.WebApi.dll"]
