FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/BusinessCard.Api/BusinessCard.Api.csproj src/BusinessCard.Api/
RUN dotnet restore src/BusinessCard.Api/BusinessCard.Api.csproj
COPY src/ src/
RUN dotnet publish src/BusinessCard.Api/BusinessCard.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=UTC
# Uploaded images and private originals must live on a persistent volume.
RUN mkdir -p /app/wwwroot/uploads /app/storage/originals && chown -R $APP_UID /app/wwwroot /app/storage
VOLUME ["/app/wwwroot/uploads", "/app/storage"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BusinessCard.Api.dll"]
