FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=0.1.0
WORKDIR /src
COPY . .
RUN dotnet publish src/Zwijg.Gateway -c Release -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
# Texterkennung für eingescannte PDFs und Fotos, mit deutschen Sprachdaten
RUN apt-get update && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-deu tesseract-ocr-eng \
    && rm -rf /var/lib/apt/lists/*
LABEL org.opencontainers.image.source="https://github.com/kschnieders/zwijg"
LABEL org.opencontainers.image.licenses="AGPL-3.0-or-later"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Nicht als root laufen. APP_UID (1654) kommt aus dem Basis Image. Der Ordner muss vor VOLUME angelegt sein,
# damit ein neues Volume ihm gehört. Bestehende Volumes gehören root, siehe docs/betrieb.md.
RUN mkdir -p /app/data && chown $APP_UID /app/data
VOLUME /app/data
USER $APP_UID
ENTRYPOINT ["dotnet", "Zwijg.Gateway.dll"]
