param(
    [string]$BaseUrl = 'http://localhost:8080',
    [ValidateRange(3, 100)][int]$Requests = 30
)

$ErrorActionPreference = 'Stop'
$baseAddress = $BaseUrl.TrimEnd('/')
$instances = @(
    for ($i = 0; $i -lt $Requests; $i++) {
        $response = Invoke-RestMethod -Uri "$baseAddress/health" -TimeoutSec 10
        if ($response.status -ne 'healthy' -or -not $response.instance) {
            throw "Unhealthy or incomplete response from /health."
        }
        $response.instance
    }
)

$counts = @($instances | Group-Object | Sort-Object Name)
$counts | ForEach-Object {
    Write-Host "$($_.Name): $($_.Count) responses"
}
if ($counts.Count -ne 3) {
    throw "Expected 3 distinct API instances, saw $($counts.Count)."
}

$books = Invoke-RestMethod -Uri "$baseAddress/api/books" -TimeoutSec 15
Write-Host "Gateway and 3 API instances passed. Books returned: $($books.Count)"
