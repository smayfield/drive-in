#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the local PostgreSQL container and applies EF Core migrations.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    Write-Host "==> Starting PostgreSQL (localhost:5433)..." -ForegroundColor Cyan
    docker compose up --wait postgres
    if ($LASTEXITCODE -ne 0) { Write-Error "docker compose failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

    Write-Host "==> Applying EF Core migrations..." -ForegroundColor Cyan
    dotnet tool restore
    dotnet ef database update --project src\DriveIn.Web
    if ($LASTEXITCODE -ne 0) { Write-Error "Migration failed with exit code $LASTEXITCODE"; exit $LASTEXITCODE }

    Write-Host ""
    Write-Host "==> Database is ready." -ForegroundColor Green
}
finally {
    Pop-Location
}
