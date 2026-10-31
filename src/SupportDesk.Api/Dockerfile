# Built from the repository root: docker build -f src/SupportDesk.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/SupportDesk.Api/SupportDesk.Api.csproj src/SupportDesk.Api/
COPY src/SupportDesk.Core/SupportDesk.Core.csproj src/SupportDesk.Core/
COPY src/SupportDesk.Data/SupportDesk.Data.csproj src/SupportDesk.Data/
RUN dotnet restore src/SupportDesk.Api/SupportDesk.Api.csproj
COPY src/ src/
# Copied into the app by SupportDesk.Data.csproj; seeded at startup in Development
COPY seed/ seed/
RUN dotnet publish src/SupportDesk.Api/SupportDesk.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080
USER app
ENTRYPOINT ["dotnet", "SupportDesk.Api.dll"]
