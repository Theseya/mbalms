# Build context: ./backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MbaLms.Api/MbaLms.Api.csproj src/MbaLms.Api/
RUN dotnet restore src/MbaLms.Api/MbaLms.Api.csproj
COPY src/ src/
RUN dotnet publish src/MbaLms.Api/MbaLms.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeysPath=/keys
COPY --from=build /app .
RUN mkdir -p /keys && chown $APP_UID /keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MbaLms.Api.dll"]
