#Requires -Version 5.1
param(
    [switch]$RestartService,
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$solution = Join-Path (Join-Path $repoRoot 'ProxiFyre') 'socksify.sln'
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe'

$srcBin = Join-Path (Join-Path $repoRoot 'ProxiFyre') 'bin'
$srcExe = Join-Path (Join-Path (Join-Path (Join-Path $srcBin 'exe') $Platform) $Configuration) 'ProxiFyre.exe'
$srcDll = Join-Path (Join-Path (Join-Path (Join-Path $srcBin 'dll') $Platform) $Configuration) 'socksify.dll'

$deployDir = Join-Path (Join-Path $repoRoot 'backend') 'proxifyre'

function Show-Timestamp($path) {
    if (Test-Path $path) {
        $item = Get-Item $path
        "  {0,-50} {1}" -f $item.Name, $item.LastWriteTime
    } else {
        "  {0,-50} {1}" -f (Split-Path $path -Leaf), 'NOT FOUND'
    }
}

Write-Host "=== ProxiFyre Build & Deploy ===" -ForegroundColor Cyan
Write-Host "Solution : $solution"
Write-Host "Config   : $Configuration | $Platform"
Write-Host "Deploy   : $deployDir"
Write-Host ""

# Build
Write-Host "[1/4] Building..." -ForegroundColor Yellow
if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found: $msbuild"
}

& $msbuild $solution /m /p:Configuration=$Configuration /p:Platform=$Platform /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

Write-Host "Build succeeded." -ForegroundColor Green
Write-Host ""

# Verify build outputs
Write-Host "[2/5] Build outputs:" -ForegroundColor Yellow
Show-Timestamp $srcExe
Show-Timestamp $srcDll
Write-Host ""

# Stop service if needed before copying locked binaries
$svcStopped = $false
if ($RestartService) {
    Write-Host "[3/5] Checking ProxiFyreService..." -ForegroundColor Yellow
    $svc = Get-Service 'ProxiFyreService' -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        try {
            Stop-Service 'ProxiFyreService' -Force
            $svcStopped = $true
            Write-Host "Service stopped."
        } catch {
            Write-Warning "Cannot stop ProxiFyreService directly (admin required). Trying UAC elevation..."
            try {
                Start-Process -FilePath 'powershell.exe' -ArgumentList "-Command Stop-Service 'ProxiFyreService' -Force" -Verb RunAs -Wait -WindowStyle Hidden
                $svcStopped = $true
                Write-Host "Service stopped via UAC elevation."
                # Also kill any orphaned ProxiFyre.exe process that may still hold file locks.
                Start-Process -FilePath 'taskkill.exe' -ArgumentList '/f /im ProxiFyre.exe' -Verb RunAs -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
            } catch {
                Write-Warning "UAC elevation also failed. $_"
                Write-Warning "If file copy fails below, stop the service manually as Administrator and re-run."
            }
        }
    } else {
        Write-Host "Service not running or not installed."
    }
    Write-Host ""
}

# Deploy
Write-Host "[4/5] Deploying to $deployDir ..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $deployDir -Force | Out-Null

$relBin = Join-Path (Join-Path (Join-Path $srcBin 'exe') $Platform) $Configuration
$filesToDeploy = @(
    $srcExe
    $srcDll
    (Join-Path $relBin 'ProxiFyre.exe.config')
    (Join-Path $relBin 'NLog.dll')
    (Join-Path $relBin 'NLog.config')
    (Join-Path $relBin 'Newtonsoft.Json.dll')
    (Join-Path $relBin 'Topshelf.dll')
    (Join-Path $relBin 'System.Runtime.InteropServices.RuntimeInformation.dll')
)

foreach ($src in $filesToDeploy) {
    if (Test-Path $src) {
        $dest = Join-Path $deployDir (Split-Path $src -Leaf)
        try {
            Copy-Item $src $dest -Force
            Show-Timestamp $dest
        } catch {
            Write-Warning "Failed to copy $(Split-Path $src -Leaf): $_"
        }
    } else {
        Write-Warning "Skip missing: $src"
    }
}
Write-Host ""

# Service restart
if ($RestartService) {
    Write-Host "[5/5] Restarting ProxiFyreService..." -ForegroundColor Yellow
    $svc = Get-Service 'ProxiFyreService' -ErrorAction SilentlyContinue
    if ($svc) {
        try {
            Start-Service 'ProxiFyreService'
            Write-Host "Service started. Status: $((Get-Service 'ProxiFyreService').Status)" -ForegroundColor Green
        } catch {
            Write-Warning "Cannot start ProxiFyreService directly (admin required). Trying UAC elevation..."
            try {
                Start-Process -FilePath 'powershell.exe' -ArgumentList "-Command Start-Service 'ProxiFyreService'" -Verb RunAs -Wait -WindowStyle Hidden
                Write-Host "Service started via UAC elevation. Status: $((Get-Service 'ProxiFyreService').Status)" -ForegroundColor Green
            } catch {
                Write-Warning "UAC elevation also failed. Start the service manually as Administrator:"
                Write-Warning "  sc.exe start ProxiFyreService"
            }
        }
    } else {
        Write-Warning "ProxiFyreService not found. Skipping restart."
    }
} else {
    Write-Host "[5/5] Skipping service restart (use -RestartService to enable)." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
