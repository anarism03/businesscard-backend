# Starts the whole local stack: PostgreSQL (Docker), backend (http://localhost:5000), frontend (http://127.0.0.1:5173).
# Each server opens in its own window; close the window (or Ctrl+C) to stop it.
$ErrorActionPreference = 'Stop'
$backend = $PSScriptRoot
$frontend = Join-Path (Split-Path $backend -Parent) 'business-card-frontend'

function Test-Port([int]$port) {
    [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

# 1. Docker Desktop + PostgreSQL
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Docker Desktop is starting...'
    Start-Process "$env:LOCALAPPDATA\Programs\DockerDesktop\Docker Desktop.exe"
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        docker info *> $null
        if ($LASTEXITCODE -eq 0) { break }
    }
    if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop did not start. Open it manually and run this script again.' }
}
Push-Location $backend
docker compose up -d
Pop-Location
Write-Host 'PostgreSQL: running (localhost:5432)'

# 2. Backend
if (Test-Port 5000) {
    Write-Host 'Backend: already running (http://localhost:5000)'
} else {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$backend\src\BusinessCard.Api'; dotnet run"
    Write-Host 'Backend: starting in a new window (http://localhost:5000/swagger)'
}

# 3. Frontend
if (Test-Port 5173) {
    Write-Host 'Frontend: already running (http://127.0.0.1:5173)'
} else {
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$frontend'; npm run dev"
    Write-Host 'Frontend: starting in a new window (http://127.0.0.1:5173)'
}
