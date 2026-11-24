param(
    [switch]$Force
)

$dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
if (-not $dockerCommand) {
    Write-Error "Docker nu este disponibil in terminal. Porneste Docker Desktop si verifica daca 'docker' este in PATH."
    exit 1
}

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker nu raspunde. Porneste Docker Desktop si incearca din nou."
    exit 1
}

Write-Host "Spatiu folosit inainte de curatare:"
docker system df
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nSe sterge cache-ul de build Docker care nu este folosit."
$pruneArguments = @('builder', 'prune', '--all')
if ($Force) { $pruneArguments += '--force' }
docker @pruneArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nSpatiu folosit dupa curatare:"
docker system df
exit $LASTEXITCODE
