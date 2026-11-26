param([string]$BaseUrl = 'http://localhost:8080')

$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')
$bookUrl = "$base/api/books/671000000000000000000001"

function Get-Headers([string]$Url, [hashtable]$Headers = @{}) {
    $response = Invoke-WebRequest -Uri $Url -Headers $Headers -UseBasicParsing -TimeoutSec 15
    [pscustomobject]@{
        Status = $response.StatusCode
        Cache = "$($response.Headers['X-Cache'])"
        Instance = "$($response.Headers['X-Api-Instance'])"
        Balancing = "$($response.Headers['X-Balancing'])"
        ContentType = "$($response.Headers['Content-Type'])"
    }
}

$stats = Invoke-RestMethod "$base/gateway/stats"
$routable = @($stats.backends | Where-Object routable)
if ($routable.Count -lt 3) { throw "Expected 3 routable backends from service discovery, got $($routable.Count)." }
Write-Host "Discovery + health check: $($routable.id -join ', ') UP"

$databases = @(Invoke-RestMethod "$base/gateway/databases" | Where-Object status -eq 'up')
$counts = @($databases | ForEach-Object { $_.info.books } | Select-Object -Unique)
if ($databases.Count -lt 3 -or $counts.Count -ne 1) {
    throw "Databases are not in sync: $($databases | ForEach-Object { "$($_.info.database)=$($_.info.books)" })"
}
Write-Host "Sync: $(($databases | ForEach-Object { "$($_.info.database)=$($_.info.books)" }) -join ', ')"

Invoke-RestMethod -Method Delete "$base/gateway/cache" | Out-Null
$first = Get-Headers $bookUrl @{ Accept = 'application/json' }
$second = Get-Headers $bookUrl @{ Accept = 'application/json' }
if ($first.Cache -ne 'MISS' -or $second.Cache -ne 'HIT') {
    throw "Cache expected MISS then HIT, got $($first.Cache) then $($second.Cache)."
}
Write-Host "Cache: MISS -> HIT"

$xml = Get-Headers $bookUrl @{ Accept = 'application/xml'; 'Cache-Control' = 'no-cache' }
if ($xml.ContentType -notlike '*xml*') { throw "Expected XML, got $($xml.ContentType)." }
Write-Host "Content negotiation: $($xml.ContentType)"

$rr = 1..6 | ForEach-Object { (Get-Headers $bookUrl @{ 'Cache-Control' = 'no-cache' }).Instance }
if (@($rr | Select-Object -Unique).Count -lt 3) { throw "Round robin did not reach all instances: $($rr -join ', ')" }
Write-Host "Round robin: $($rr -join ', ')"

$sticky = 1..6 | ForEach-Object { (Get-Headers $bookUrl @{ 'Cache-Control' = 'no-cache'; 'X-Client-Id' = 'demo-client' }).Instance }
if (@($sticky | Select-Object -Unique).Count -ne 1) { throw "Sticky routing moved between instances: $($sticky -join ', ')" }
Write-Host "Sticky (X-Client-Id): $($sticky[0]) x6"

$load = Invoke-RestMethod -Method Post "$base/gateway/load-tests" -ContentType 'application/json' `
    -Body '{"total":120,"highPercent":20,"lowPercent":50,"bypassCache":true}'
$load.byPriority | Format-Table priority, count, averageMs, maxMs
$high = ($load.byPriority | Where-Object priority -eq 'HIGH').averageMs
$low = ($load.byPriority | Where-Object priority -eq 'LOW').averageMs
if ($high -ge $low) { Write-Warning "HIGH ($high ms) was not faster than LOW ($low ms); raise the request count." }
else { Write-Host "Priority queue: HIGH $high ms < LOW $low ms" }

Write-Host 'Smart proxy test passed.'
