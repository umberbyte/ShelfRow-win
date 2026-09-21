param(
    [switch]$NoBuild,
    [switch]$Restore
)

$ErrorActionPreference = "Stop"

# Ensure dotnet is in PATH
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    if (Test-Path "C:\Program Files\dotnet\dotnet.exe") {
        $env:Path = "C:\Program Files\dotnet;" + $env:Path
    }
}

$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Item .).FullName }
$exePath = Join-Path $root "src\ShelfRow.App\bin\x64\Debug\net10.0-windows10.0.19041.0\ShelfRow.App.exe"
$projectPath = Join-Path $root "src\ShelfRow.App\ShelfRow.App.csproj"

if (-not $NoBuild) {
    # A running instance keeps its DLLs open and the build fails on the copy step.
    $running = Get-Process "ShelfRow.App" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing running ShelfRow.App (PID $($running.Id -join ', '))..." -ForegroundColor Yellow
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }

    Write-Host "Building ShelfRow.App..." -ForegroundColor Cyan
    $buildArgs = @("build", $projectPath)
    if (-not $Restore) {
        $buildArgs += "--no-restore"
    }
    dotnet @buildArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed."
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found: $exePath"
    exit 1
}

Write-Host "Starting ShelfRow: $exePath" -ForegroundColor Green
$workingDir = Split-Path -Parent $exePath
Start-Process -FilePath $exePath -WorkingDirectory $workingDir
