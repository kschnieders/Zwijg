FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG VERSION=0.1.0
WORKDIR /src
COPY . .
RUN dotnet publish src/Zwijg.Gateway -c Release -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.source="https://github.com/kschnieders/zwijg"
LABEL org.opencontainers.image.licenses="AGPL-3.0-or-later"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /app/data
ENTRYPOINT ["dotnet", "Zwijg.Gateway.dll"]
