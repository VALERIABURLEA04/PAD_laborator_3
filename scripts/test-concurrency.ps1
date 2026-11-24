param(
    [string]$BaseUrl = 'http://localhost:8080',
    [ValidateRange(2, 50)][int]$Count = 10
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$baseAddress = $BaseUrl.TrimEnd('/')
$replicaUrls = @('http://localhost:18081', 'http://localhost:18082', 'http://localhost:18083')
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(30)
$createdIds = [System.Collections.Generic.List[string]]::new()
$runId = [Guid]::NewGuid().ToString('N')

function Send-Batch {
    param([object[]]$Jobs)

    $pending = foreach ($job in $Jobs) {
        $target = if ($job.BaseUrl) { $job.BaseUrl } else { $baseAddress }
        $request = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::new($job.Method),
            "$target$($job.Path)")
        if ($null -ne $job.Body) {
            $json = ConvertTo-Json -InputObject $job.Body -Depth 5 -Compress
            $request.Content = [System.Net.Http.StringContent]::new(
                $json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        [pscustomobject]@{
            Request = $request
            Task = $client.SendAsync($request)
        }
    }

    foreach ($item in $pending) {
        try {
            $response = $item.Task.GetAwaiter().GetResult()
            try {
                [pscustomobject]@{
                    Status = [int]$response.StatusCode
                    Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                }
            }
            finally {
                $response.Dispose()
            }
        }
        finally {
            $item.Request.Dispose()
        }
    }
}

function Wait-ForReplicaState {
    param(
        [string[]]$Ids,
        [int]$ExpectedStatus,
        [hashtable]$ExpectedTitles = @{},
        [string[]]$AllowedTitles = @()
    )

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $jobs = @(
            foreach ($replica in $replicaUrls) {
                foreach ($id in $Ids) {
                    [pscustomobject]@{
                        BaseUrl = $replica
                        Method = 'GET'
                        Path = "/api/books/$id"
                        Body = $null
                    }
                }
            }
        )
        $results = @(Send-Batch $jobs)
        $matches = $true
        $titles = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $results.Count; $i++) {
            if ($results[$i].Status -ne $ExpectedStatus) {
                $matches = $false
                break
            }
            if ($ExpectedStatus -eq 200) {
                $id = $Ids[$i % $Ids.Count]
                $title = (ConvertFrom-Json -InputObject $results[$i].Body).title
                $titles.Add($title)
                if ($ExpectedTitles.ContainsKey($id) -and
                    $title -ne $ExpectedTitles[$id]) {
                    $matches = $false
                    break
                }
            }
        }
        if ($matches -and $AllowedTitles.Count -gt 0) {
            $matches = @($titles | Select-Object -Unique).Count -eq 1 -and
                $titles[0] -in $AllowedTitles
        }
        if ($matches) {
            if ($AllowedTitles.Count -gt 0) { return $titles[0] }
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "The three databases did not converge to HTTP $ExpectedStatus."
}

function Assert-Statuses {
    param([object[]]$Results, [int]$Expected, [string]$Step)
    if ($Results.Count -ne $Count) {
        throw "$Step returned $($Results.Count) responses; expected $Count."
    }
    $failed = @($Results | Where-Object Status -ne $Expected)
    if ($failed.Count -gt 0) {
        throw "$Step failed: $($failed.Count) responses were not HTTP $Expected. First: HTTP $($failed[0].Status) $($failed[0].Body)"
    }
    Write-Host "$Step : $Count/$Count HTTP $Expected"
}

try {
    $createJobs = @(
        for ($i = 1; $i -le $Count; $i++) {
            [pscustomobject]@{
                Method = 'POST'
                Path = '/api/books'
                Body = @{
                    title = "Concurrent $runId book $i"
                    author = 'Test Author'
                    publishedYear = 2024
                    tags = @('concurrency-test')
                }
            }
        }
    )
    $created = @(Send-Batch $createJobs)
    foreach ($result in $created) {
        if ($result.Status -eq 201) {
            $id = (ConvertFrom-Json -InputObject $result.Body).id
            if ($id) { $createdIds.Add($id) }
        }
    }
    Assert-Statuses $created 201 'Concurrent POST'
    if ($createdIds.Count -ne $Count -or @($createdIds | Select-Object -Unique).Count -ne $Count) {
        throw 'Created books do not have unique ids.'
    }
    Wait-ForReplicaState -Ids $createdIds.ToArray() -ExpectedStatus 200

    $readJobs = @($createdIds | ForEach-Object {
        [pscustomobject]@{ Method = 'GET'; Path = "/api/books/$_"; Body = $null }
    })
    $read = @(Send-Batch $readJobs)
    Assert-Statuses $read 200 'Concurrent GET'

    $updateJobs = @(
        for ($i = 0; $i -lt $Count; $i++) {
            [pscustomobject]@{
                Method = 'PUT'
                Path = "/api/books/$($createdIds[$i])"
                Body = @{
                    title = "Concurrent $runId book $($i + 1) updated"
                    author = 'Test Author'
                    publishedYear = 2025
                    tags = @('concurrency-test')
                }
            }
        }
    )
    $updated = @(Send-Batch $updateJobs)
    Assert-Statuses $updated 200 'Concurrent PUT'
    $expectedTitles = @{}
    for ($i = 0; $i -lt $Count; $i++) {
        $expectedTitles[$createdIds[$i]] = $updateJobs[$i].Body.title
    }
    Wait-ForReplicaState -Ids $createdIds.ToArray() -ExpectedStatus 200 -ExpectedTitles $expectedTitles

    $readUpdated = @(Send-Batch $readJobs)
    Assert-Statuses $readUpdated 200 'GET after concurrent PUT'
    for ($i = 0; $i -lt $Count; $i++) {
        $book = ConvertFrom-Json -InputObject $readUpdated[$i].Body
        if ($book.title -ne $updateJobs[$i].Body.title) {
            throw "Update was not persisted for book $($createdIds[$i])."
        }
    }
    Write-Host "Updated values verified: $Count/$Count"

    $deleteJobs = @($createdIds | ForEach-Object {
        [pscustomobject]@{ Method = 'DELETE'; Path = "/api/books/$_"; Body = $null }
    })
    $deleted = @(Send-Batch $deleteJobs)
    Assert-Statuses $deleted 204 'Concurrent DELETE'
    Wait-ForReplicaState -Ids $createdIds.ToArray() -ExpectedStatus 404

    $readDeleted = @(Send-Batch $readJobs)
    Assert-Statuses $readDeleted 404 'GET after concurrent DELETE'
    Write-Host 'Independent-request concurrency test passed.'

    $conflictCreate = @(Send-Batch @([pscustomobject]@{
        Method = 'POST'
        Path = '/api/books'
        Body = @{
            title = "Conflict $runId original"
            author = 'Test Author'
            publishedYear = 2024
            tags = @('concurrency-test')
        }
    }))
    if ($conflictCreate.Count -ne 1 -or $conflictCreate[0].Status -ne 201) {
        throw 'Could not create the book for the conflicting-update test.'
    }
    $conflictId = (ConvertFrom-Json -InputObject $conflictCreate[0].Body).id
    $createdIds.Add($conflictId)
    Wait-ForReplicaState -Ids @($conflictId) -ExpectedStatus 200

    $conflictTitles = @("Conflict $runId A", "Conflict $runId B")
    $conflictJobs = @(
        foreach ($title in $conflictTitles) {
            [pscustomobject]@{
                Method = 'PUT'
                Path = "/api/books/$conflictId"
                Body = @{
                    title = $title
                    author = 'Test Author'
                    publishedYear = 2024
                    tags = @('concurrency-test')
                }
            }
        }
    )
    $conflictResults = @(Send-Batch $conflictJobs)
    if (@($conflictResults | Where-Object Status -ne 200).Count -gt 0) {
        throw 'A concurrent update of the same book failed.'
    }
    $convergedTitle = Wait-ForReplicaState -Ids @($conflictId) -ExpectedStatus 200 -AllowedTitles $conflictTitles
    $finalBookResponse = @(Send-Batch @([pscustomobject]@{
        Method = 'GET'; Path = "/api/books/$conflictId"; Body = $null
    }))
    if ($finalBookResponse[0].Status -ne 200) {
        throw 'Could not read the book after conflicting updates.'
    }
    $finalTitle = (ConvertFrom-Json -InputObject $finalBookResponse[0].Body).title
    if ($finalTitle -notin $conflictTitles) {
        throw 'Unexpected final title after conflicting updates.'
    }
    if ($finalTitle -ne $convergedTitle) {
        throw 'Gateway returned a value different from the converged databases.'
    }
    Write-Host "Same-book PUT: 2/2 HTTP 200; final title: $finalTitle"
    Write-Host 'Both updates succeeded; the higher synchronization version determines the stored value.'
}
finally {
    if ($createdIds.Count -gt 0) {
        $cleanupJobs = @(
            foreach ($replica in $replicaUrls) {
                foreach ($id in $createdIds) {
                    [pscustomobject]@{
                        BaseUrl = $replica
                        Method = 'DELETE'
                        Path = "/api/books/$id"
                        Body = $null
                    }
                }
            }
        )
        try { Send-Batch $cleanupJobs | Out-Null }
        catch { Write-Warning 'Some temporary books could not be cleaned up.' }
    }
    $client.Dispose()
}
