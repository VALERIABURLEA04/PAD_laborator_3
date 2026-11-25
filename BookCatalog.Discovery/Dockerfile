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
COPY ["BookCatalog.Discovery/BookCatalog.Discovery.csproj", "BookCatalog.Discovery/"]
RUN dotnet restore "BookCatalog.Discovery/BookCatalog.Discovery.csproj"
COPY BookCatalog.Discovery/ BookCatalog.Discovery/
WORKDIR "/src/BookCatalog.Discovery"
RUN dotnet publish "./BookCatalog.Discovery.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BookCatalog.Discovery.dll"]
