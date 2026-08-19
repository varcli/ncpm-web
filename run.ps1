<#
.SYNOPSIS
    Local development runner for NCPM Web Panel.

.DESCRIPTION
    Builds and runs the NCPM Blazor Server application locally without Docker.
    Uses Development environment so hot-reload and detailed errors are available.

.PARAMETER Port
    Port to listen on. Default: 8098.

.PARAMETER NoBuild
    Skip the build step and run the previously compiled binary directly.

.EXAMPLE
    .\run.ps1
    .\run.ps1 -Port 5000
    .\run.ps1 -NoBuild
#>
param(
    [int]$Port = 5080,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

function Pause-Exit([int]$code = 1) {
    Write-Host "`nPress any key to exit..." -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    exit $code
}

try {
    $ProjectDir = "$PSScriptRoot\src\Ncpm.Web"
    $ProjectFile = "$ProjectDir\Ncpm.Web.csproj"

    # ── Pre-flight checks ────────────────────────────────────────────────────
    if (-not (Test-Path $ProjectFile)) {
        Write-Error "Project file not found: $ProjectFile"
        Pause-Exit
    }

    # Check .NET SDK
    try {
        $dotnetVersion = dotnet --version 2>&1
        Write-Host "[OK] .NET SDK: $dotnetVersion" -ForegroundColor Green
    } catch {
        Write-Host "[ERROR] dotnet SDK not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Red
        Pause-Exit
    }

    # ── Prepare data directory ───────────────────────────────────────────────
    $DataDir = "$PSScriptRoot\data"
    if (-not (Test-Path $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        Write-Host "[OK] Created data directory: $DataDir" -ForegroundColor Cyan
    }

    # ── Restore & Build ──────────────────────────────────────────────────────
    if (-not $NoBuild) {
        Write-Host "`n==> Restoring packages..." -ForegroundColor Yellow
        dotnet restore $ProjectFile
        if ($LASTEXITCODE -ne 0) {
            Write-Host "`n[ERROR] dotnet restore failed (exit code $LASTEXITCODE)" -ForegroundColor Red
            Pause-Exit $LASTEXITCODE
        }

        Write-Host "`n==> Building..." -ForegroundColor Yellow
        dotnet build $ProjectFile -c Debug --no-restore
        if ($LASTEXITCODE -ne 0) {
            Write-Host "`n[ERROR] Build failed (exit code $LASTEXITCODE)" -ForegroundColor Red
            Pause-Exit $LASTEXITCODE
        }
    }

    # ── Run ──────────────────────────────────────────────────────────────────
    Write-Host "`n==> Starting NCPM on http://localhost:$Port ..." -ForegroundColor Green
    Write-Host "    Press Ctrl+C to stop.`n" -ForegroundColor DarkGray

    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ASPNETCORE_URLS = "http://localhost:$Port"
    $env:Config__Path = $DataDir
    $env:Logging__Path = "$DataDir\logs"

    dotnet run --project $ProjectFile -c Debug --no-build:$NoBuild -- --urls "http://localhost:$Port"

} catch {
    Write-Host "`n[ERROR] $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
    Pause-Exit
}
