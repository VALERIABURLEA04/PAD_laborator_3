param(
    [string]$BaseUrl = 'http://localhost:18081'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$baseAddress = $BaseUrl.TrimEnd('/')
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(15)
$createdId = $null
$deleted = $false

function Send-Request {
    param(
        [System.Net.Http.HttpMethod]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    $request = [System.Net.Http.HttpRequestMessage]::new($Method, "$baseAddress$Path")
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
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Assert-Status {
    param([object]$Result, [int]$Expected, [string]$Step)
    if ($Result.Status -ne $Expected) {
        throw "$Step expected HTTP $Expected, received HTTP $($Result.Status). $($Result.Body)"
    }
    Write-Host "$Step : HTTP $Expected"
}

try {
    $uniqueTitle = "CRUD test $([Guid]::NewGuid().ToString('N'))"
    $newBook = @{
        title = $uniqueTitle
        author = 'Test Author'
        publishedYear = 2024
        description = 'Temporary test book'
        tags = @('test')
    }

    $post = Send-Request -Method ([System.Net.Http.HttpMethod]::Post) -Path '/api/books' -Body $newBook
    Assert-Status $post 201 'POST /api/books'
    $createdId = ($post.Body | ConvertFrom-Json).id
    if (-not $createdId) { throw 'POST response did not contain a book id.' }

    $get = Send-Request -Method ([System.Net.Http.HttpMethod]::Get) -Path "/api/books/$createdId"
    Assert-Status $get 200 'GET /api/books/{id}'
    if (($get.Body | ConvertFrom-Json).title -ne $uniqueTitle) {
        throw 'GET returned the wrong book.'
    }

    $list = Send-Request -Method ([System.Net.Http.HttpMethod]::Get) -Path '/api/books'
    Assert-Status $list 200 'GET /api/books'
    if (-not (@($list.Body | ConvertFrom-Json).id -contains $createdId)) {
        throw 'The new book is missing from the list.'
    }

    $updatedBook = $newBook.Clone()
    $updatedBook.title = "$uniqueTitle updated"
    $put = Send-Request -Method ([System.Net.Http.HttpMethod]::Put) -Path "/api/books/$createdId" -Body $updatedBook
    Assert-Status $put 200 'PUT /api/books/{id}'

    $updated = Send-Request -Method ([System.Net.Http.HttpMethod]::Get) -Path "/api/books/$createdId"
    Assert-Status $updated 200 'GET after PUT'
    if (($updated.Body | ConvertFrom-Json).title -ne $updatedBook.title) {
        throw 'PUT did not persist the updated title.'
    }

    $delete = Send-Request -Method ([System.Net.Http.HttpMethod]::Delete) -Path "/api/books/$createdId"
    Assert-Status $delete 204 'DELETE /api/books/{id}'
    $deleted = $true

    $missing = Send-Request -Method ([System.Net.Http.HttpMethod]::Get) -Path "/api/books/$createdId"
    Assert-Status $missing 404 'GET after DELETE'

    Write-Host 'CRUD test passed.'
}
finally {
    if ($createdId -and -not $deleted) {
        try {
            Send-Request -Method ([System.Net.Http.HttpMethod]::Delete) -Path "/api/books/$createdId" | Out-Null
        }
        catch {
            Write-Warning "Could not remove test book $createdId"
        }
    }
    $client.Dispose()
}
