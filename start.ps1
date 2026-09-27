#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the solution, runs tests, and if they pass launches the app in a browser.
    Run setup.ps1 first. Emails (confirmations, invites, resets) are written to this console.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot 'src\DriveIn.sln'
$appUrl   = 'http://localhost:5280'

Write-Host ""
Write-Host "==> Building and testing..." -ForegroundColor Cyan
dotnet test $solution --configuration Release
if ($LASTEXITCODE -ne 0) { Write-Error "Build or tests failed. Aborting."; exit $LASTEXITCODE }
Write-Host "==> All tests passed." -ForegroundColor Green

Write-Host ""
Write-Host "==> Launching app at $appUrl ..." -ForegroundColor Cyan

$browserJob = Start-Job -ScriptBlock {
    param($url)
    Start-Sleep -Seconds 5
    Start-Process $url
} -ArgumentList $appUrl

try {
    dotnet run --project (Join-Path $repoRoot 'src\DriveIn.Web\DriveIn.Web.csproj') --configuration Release --launch-profile http
}
finally {
    $browserJob | Remove-Job -Force
}
