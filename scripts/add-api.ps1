param(
    [Parameter(Mandatory = $true)][ValidateRange(4, 99)][int]$Number,
    [switch]$Remove
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$api = "api$Number"
$mongo = "mongo$Number"
$database = "bookcatalog$Number"
$extraDir = Join-Path $root "extra-apis"
$file = Join-Path $extraDir "$api.yaml"

Push-Location $root
try {
    if ($Remove) {
        if (Test-Path $file) {
            docker compose -f compose.yaml -f $file rm -s -f -v $api $mongo | Out-Null
            Remove-Item $file
        } else {
            docker rm -f $api $mongo 2>$null | Out-Null
        }
        docker volume rm "bookcatalog_${mongo}_data" 2>$null | Out-Null
        try { Invoke-RestMethod -Method Delete "http://localhost:18090/services/${api}?permanent=true" | Out-Null } catch { }
        Write-Host "$api și baza lui ($mongo / $database) au fost șterse complet. Dispare din dashboard în câteva secunde."
        return
    }

    if (Test-Path $file) { throw "$api există deja. Șterge-l întâi: .\scripts\add-api.ps1 $Number -Remove" }
    New-Item -ItemType Directory -Force $extraDir | Out-Null

    @"
services:
  ${mongo}:
    image: mongo:8.0
    restart: unless-stopped
    mem_limit: 384m
    command: ["mongod", "--bind_ip_all", "--wiredTigerCacheSizeGB", "0.25"]
    volumes:
      - ${mongo}_data:/data/db
    healthcheck:
      test: ["CMD", "mongosh", "--quiet", "--eval", "db.adminCommand('ping').ok"]
      interval: 5s
      timeout: 5s
      retries: 12

  ${api}:
    image: bookcatalog-api
    restart: unless-stopped
    mem_limit: 320m
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_HTTP_PORTS: 8080
      Redis__ConnectionString: redis:6379
      Sync__Enabled: "true"
      Sync__InstanceId: ${api}
      MongoDb__ConnectionString: mongodb://${mongo}:27017/${database}
      MongoDb__DatabaseName: ${database}
      Discovery__Url: http://discovery:8080
      Discovery__ServiceName: bookcatalog-api
      Discovery__HeartbeatSeconds: "5"
      Discovery__Address: http://${api}:8080
    healthcheck:
      test: ["CMD", "curl", "--fail", "--silent", "--max-time", "5", "http://localhost:8080/health"]
      interval: 20s
      timeout: 6s
      retries: 3
      start_period: 40s
    depends_on:
      ${mongo}:
        condition: service_healthy

volumes:
  ${mongo}_data:
"@ | Set-Content -Encoding utf8 $file

    docker compose -f compose.yaml -f $file up -d $mongo $api
    if ($LASTEXITCODE -ne 0) { throw "Nu am putut porni $api." }
    Write-Host "$api pornește cu baza proprie $database (container $mongo)."
    Write-Host "Se înregistrează singur în Discovery, iar serviciul sync îi copiază toate cărțile în câteva secunde."
} finally {
    Pop-Location
}
