FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish CellSimulator.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000/tcp
EXPOSE 7778/udp
# Both ports are >1024, so the game server doesn't need root (APP_UID is set by the aspnet image).
USER $APP_UID
ENTRYPOINT ["dotnet", "CellSimulator.Server.dll"]
