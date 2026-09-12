FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY CeleCamIp.Gateway/CeleCamIp.Gateway.csproj CeleCamIp.Gateway/
COPY CeleCamIp.Shared/CeleCamIp.Shared.csproj CeleCamIp.Shared/

RUN dotnet restore CeleCamIp.Gateway/CeleCamIp.Gateway.csproj

COPY CeleCamIp.Gateway/ CeleCamIp.Gateway/
COPY CeleCamIp.Shared/ CeleCamIp.Shared/

RUN dotnet publish CeleCamIp.Gateway/CeleCamIp.Gateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
ARG TARGETARCH
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production

# ----------------------------------------------------------------
# FFmpeg 8.1 (librerias compartidas / .so + binario ffmpeg)
#
# CONFIRMADO: el proyecto resuelve FFmpeg.AutoGen 8.1.0 exacto
# (ver CeleCamIp.Gateway/obj/project.assets.json). Ese binding busca
# los simbolos con los numeros de version de FFmpeg 8.1:
# avcodec.so.62, avformat.so.62, avutil.so.60, avdevice.so.62,
# avfilter.so.11, swscale.so.9, swresample.so.6. Instalar FFmpeg 7.1
# (via apt/trixie) NO sirve: los numeros no coinciden y
# EnsureBinariesRegistered() devuelve false sin excepcion.
#
# Se usa el build "shared" de BtbN/FFmpeg-Builds, serie 8.1.
#
# OJO - dos consumidores DISTINTOS de FFmpeg en este proyecto, cada uno
# necesita algo diferente del mismo paquete descargado:
#   1) SIPSorceryMedia.FFmpeg (binding P/Invoke, solo diagnostico/health
#      check) necesita los .so en una ruta que se le pase explicito por
#      libPath (ver Program.cs: FFmpegInit.Initialise(..., "/usr/lib", ...)
#      en Linux) - NO alcanza con que ldconfig los conozca.
#   2) FFmpegProcessSource (el pipeline de video REAL, ver DECISIONES.md D5)
#      invoca el binario "ffmpeg" como proceso de sistema. Su FindFFmpeg()
#      ya busca en /usr/bin/ffmpeg y /usr/local/bin/ffmpeg en Linux, pero
#      ese binario nunca se copiaba al contenedor - solo se copiaban los
#      .so de lib/. Sin el binario, StartVideo() tira
#      FileNotFoundException("No se encontro ffmpeg.exe en el PATH") aun
#      con las libs perfectamente instaladas (ver DECISIONES.md D18).
# ----------------------------------------------------------------

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl xz-utils ca-certificates \
    && case "$TARGETARCH" in \
         amd64) FFMPEG_ARCH=linux64 ;; \
         arm64) FFMPEG_ARCH=linuxarm64 ;; \
         *) echo "Arquitectura no soportada para FFmpeg: $TARGETARCH" && exit 1 ;; \
       esac \
    && curl -fSL -o /tmp/ffmpeg.tar.xz \
         "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-${FFMPEG_ARCH}-gpl-shared-8.1.tar.xz" \
    && mkdir -p /tmp/ffmpeg \
    && tar -xf /tmp/ffmpeg.tar.xz -C /tmp/ffmpeg --strip-components=1 \
    && cp -P /tmp/ffmpeg/lib/*.so* /usr/lib/ \
    && ldconfig \
    && echo "=== Verificando dependencias de las libs FFmpeg 8.1 ===" \
    && ( ldd /usr/lib/libavcodec.so.62 | grep -i "not found" && echo "!!! FALTAN DEPENDENCIAS (ver arriba) !!!" && exit 1 || echo "OK: sin dependencias faltantes" ) \
    && cp -P /tmp/ffmpeg/bin/ffmpeg /usr/local/bin/ffmpeg \
    && cp -P /tmp/ffmpeg/bin/ffprobe /usr/local/bin/ffprobe \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && echo "=== Verificando binario ffmpeg ===" \
    && /usr/local/bin/ffmpeg -version | head -1 \
    && rm -rf /tmp/ffmpeg /tmp/ffmpeg.tar.xz \
    && apt-get purge -y curl xz-utils \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CeleCamIp.Gateway.dll"]
