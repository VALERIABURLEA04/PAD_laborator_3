param([int]$TimeoutSeconds = 20)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(10)
$apiUrls = @('http://localhost:18081', 'http://localhost:18082', 'http://localhost:18083')
$createdId = $null

function Send-Request {
    param([string]$BaseUrl, [string]$Method, [string]$Path, [object]$Body = $null)

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method), "$BaseUrl$Path")
    try {
        if ($null -ne $Body) {
            $json = ConvertTo-Json -InputObject $Body -Depth 5 -Compress
            $request.Content = [System.Net.Http.StringContent]::new(
                $json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            return [pscustomobject]@{
                Status = [int]$response.StatusCode
                Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Wait-ForState {
    param([string]$Id, [int]$Status, [string]$Title = $null)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $allMatch = $true
        foreach ($api in $apiUrls) {
            $result = Send-Request $api 'GET' "/api/books/$Id"
            if ($result.Status -ne $Status) {
                $allMatch = $false
                break
            }
            if ($Status -eq 200 -and
                (ConvertFrom-Json -InputObject $result.Body).title -ne $Title) {
                $allMatch = $false
                break
            }
        }
        if ($allMatch) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "The three APIs did not converge to HTTP $Status for book $Id."
}

try {
    $title = "Cluster test $([Guid]::NewGuid().ToString('N'))"
    $book = @{
        title = $title
        author = 'Test Author'
        publishedYear = 2024
        tags = @('cluster-test')
    }

    $created = Send-Request $apiUrls[0] 'POST' '/api/books' $book
    if ($created.Status -ne 201) {
        throw "Create through API 1 returned HTTP $($created.Status): $($created.Body)"
    }
    $createdId = (ConvertFrom-Json -InputObject $created.Body).id
    Wait-ForState $createdId 200 $title
    Write-Host 'Create through API 1 synchronized to all 3 databases.'

    $book.title = "$title updated"
    $updated = Send-Request $apiUrls[1] 'PUT' "/api/books/$createdId" $book
    if ($updated.Status -ne 200) {
        throw "Update through API 2 returned HTTP $($updated.Status): $($updated.Body)"
    }
    Wait-ForState $createdId 200 $book.title
    Write-Host 'Update through API 2 synchronized to all 3 databases.'

    $deleted = Send-Request $apiUrls[2] 'DELETE' "/api/books/$createdId"
    if ($deleted.Status -ne 204) {
        throw "Delete through API 3 returned HTTP $($deleted.Status): $($deleted.Body)"
    }
    Wait-ForState $createdId 404
    Write-Host 'Delete through API 3 synchronized to all 3 databases.'
    Write-Host 'Cluster synchronization test passed.'
}
finally {
    if ($createdId) {
        foreach ($api in $apiUrls) {
            try { Send-Request $api 'DELETE' "/api/books/$createdId" | Out-Null }
            catch { Write-Warning "Could not clean up test book on $api." }
        }
    }
    $client.Dispose()
}
