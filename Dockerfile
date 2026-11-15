FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Boneiset.Shared/ src/Boneiset.Shared/
COPY src/Boneiset.Server/ src/Boneiset.Server/
RUN dotnet restore src/Boneiset.Server/Boneiset.Server.csproj --locked-mode \
    && dotnet publish src/Boneiset.Server/Boneiset.Server.csproj -c Release -o /app --no-self-contained --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/keys && chown $APP_UID:$APP_UID /app/keys && chmod 700 /app/keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Boneiset.Server.dll"]
