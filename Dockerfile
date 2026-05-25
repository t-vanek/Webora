# syntax=docker/dockerfile:1

# Build + publish the Blazor Web App host (pulls in the WASM client and all referenced projects).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the project/props files first so the (slow) restore layer is cached
# independently of source edits. Central package management requires the root props files.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Webora.Domain/Webora.Domain.csproj                 src/Webora.Domain/
COPY src/Webora.Contracts/Webora.Contracts.csproj            src/Webora.Contracts/
COPY src/Webora.Application/Webora.Application.csproj        src/Webora.Application/
COPY src/Webora.Infrastructure/Webora.Infrastructure.csproj src/Webora.Infrastructure/
COPY src/Webora.Web.Client/Webora.Web.Client.csproj         src/Webora.Web.Client/
COPY src/Webora.Web/Webora.Web.csproj                       src/Webora.Web/
RUN dotnet restore src/Webora.Web/Webora.Web.csproj

COPY . .
RUN dotnet publish src/Webora.Web/Webora.Web.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# Runtime image. The Debian-based aspnet image bundles ICU, which the app's localization needs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Webora.Web.dll"]
