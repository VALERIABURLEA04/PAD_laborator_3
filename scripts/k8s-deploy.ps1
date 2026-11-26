param([switch]$Delete)

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    kubectl config use-context docker-desktop | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Nu găsesc clusterul docker-desktop. Activează Kubernetes din Docker Desktop -> Settings -> Kubernetes." }

    if ($Delete) {
        kubectl delete namespace bookcatalog
        Write-Host "Clusterul BookCatalog a fost șters (inclusiv bazele de date din Kubernetes)."
        return
    }

    Write-Host "1/3 Construiesc imaginile..."
    docker compose build api1 discovery gateway
    if ($LASTEXITCODE -ne 0) { throw "Build-ul imaginilor a eșuat." }

    Write-Host "2/3 Aplic manifestele din k8s/..."
    kubectl apply -f k8s/namespace.yaml | Out-Null
    kubectl create configmap seed-script -n bookcatalog --from-file=seed.js=scripts/seed.js --dry-run=client -o yaml | kubectl apply -f - | Out-Null
    kubectl apply -k k8s
    if ($LASTEXITCODE -ne 0) { throw "kubectl apply a eșuat." }

    Write-Host "3/3 Aștept pornirea pod-urilor..."
    kubectl rollout status statefulset/api -n bookcatalog --timeout=240s
    kubectl rollout status deployment/gateway -n bookcatalog --timeout=120s
    kubectl get pods -n bookcatalog
    Write-Host ""
    Write-Host "Gata: http://localhost:8080/dashboard/"
} finally {
    Pop-Location
}
