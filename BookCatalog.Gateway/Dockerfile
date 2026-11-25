FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["BookCatalog.Gateway/BookCatalog.Gateway.csproj", "BookCatalog.Gateway/"]
RUN dotnet restore "BookCatalog.Gateway/BookCatalog.Gateway.csproj"
COPY BookCatalog.Gateway/ BookCatalog.Gateway/
WORKDIR "/src/BookCatalog.Gateway"
RUN dotnet publish "./BookCatalog.Gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BookCatalog.Gateway.dll"]
