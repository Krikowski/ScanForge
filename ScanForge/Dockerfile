# Multi-stage build para ScanForge Worker Service
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["ScanForge.csproj", "."]
RUN dotnet restore "ScanForge.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "ScanForge.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ScanForge.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime com dependências essenciais + SKIASHARP
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# ✅ Instalar dependências SKIASHARP + FFMPEG + Fontconfig
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ffmpeg \
    libfontconfig1 \
    libfreetype6 \
    libfontconfig1-dev \
    libharfbuzz0b \
    libpng16-16 \
    libjpeg62-turbo \
    libgif7 \
    libcairo2 \
    libpango-1.0-0 \
    libpangoft2-1.0-0 \
    libgdk-pixbuf-2.0-0 \
    libx11-6 \
    libxext6 \
    libxrender1 \
    libxtst6 \
    fontconfig \
    libgdiplus \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && ldconfig

# ✅ CRÍTICO: Copiar libSkiaSharp.so do publish (NativeAssets.Linux inclui no publish)
COPY --from=publish /app/publish/runtimes/linux-x64/native/libSkiaSharp.so /app/libSkiaSharp.so

# ✅ Configurar LD_LIBRARY_PATH para incluir /app
ENV LD_LIBRARY_PATH=/app:$LD_LIBRARY_PATH

# ✅ Verificar se libSkiaSharp existe
RUN ls -la /app/libSkiaSharp.so || echo "⚠️ libSkiaSharp não encontrada" && \
    ldconfig -p | grep SkiaSharp || echo "⚠️ ldconfig não encontra SkiaSharp"

# Configurações de ambiente
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV VideoStorage__TempFramesPath=/tmp/scanforge_frames
ENV SKIASHARP_HARFBUZZ_USE_DEFAULT_FONT=true
ENV GDIPLUS_ENABLED=1

EXPOSE 8080
EXPOSE 8081

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD wget --quiet --tries=1 --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ScanForge.dll"]