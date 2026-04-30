<#
.SYNOPSIS
    Downganizer service installer / manager.

.DESCRIPTION
    Builds, installs, starts, stops, restarts, or uninstalls the Downganizer
    Windows Service. Must be run as Administrator for any action that touches
    the Service Control Manager (Install, Uninstall, Start, Stop, Restart).

    Layout assumed (everything lives under C:\Downganizer):
        C:\Downganizer\src\Downganizer\Downganizer.csproj   <- the project
        C:\Downganizer\bin\publish\Downganizer.exe          <- built output
        C:\Downganizer\config\config.json                   <- user config
        C:\Downganizer\data\history.json                    <- state DB (created at runtime)
        C:\Downganizer\logs\downganizer-YYYY-MM-DD.log      <- rolling logs

.PARAMETER Action
    Build      - dotnet publish into C:\Downganizer\bin\publish (self-contained, win-x64).
    Install    - Build (if needed) + register the service with auto-start + failure recovery.
    Uninstall  - Stop + delete the service.
    Reinstall  - Uninstall, Build, Install. Use after editing source.
    Start      - Start the service.
    Stop       - Stop the service.
    Restart    - Stop then Start.
    Status     - Show service state and tail the most recent log file.

.EXAMPLE
    PS> .\Downganizer.ps1 Install
    PS> .\Downganizer.ps1 Status
    PS> .\Downganizer.ps1 Reinstall
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('Build', 'Install', 'Uninstall', 'Reinstall', 'Start', 'Stop', 'Restart', 'Status')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'

# ============================================================================
# Configuration
# ============================================================================
$ServiceName    = 'Downganizer'
$ServiceDisplay = 'Downganizer Auto-Organization Service'
$ServiceDesc    = 'Robust 24/7 background service for automatic Downloads folder organization.'

$ProjectRoot    = 'C:\Downganizer'
$ProjectFile    = Join-Path $ProjectRoot 'src\Downganizer\Downganizer.csproj'
$PublishDir     = Join-Path $ProjectRoot 'bin\publish'
$BinaryPath     = Join-Path $PublishDir 'Downganizer.exe'
$LogsDir        = Join-Path $ProjectRoot 'logs'

# ============================================================================
# Helpers
# ============================================================================
function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This action requires Administrator. Re-run PowerShell as Admin."
    }
}

function Test-ServiceExists {
    return $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

function Wait-ForServiceStatus {
    param(
        [string]$Status,
        [int]$TimeoutSeconds = 30
    )
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) { return }
    try {
        $svc.WaitForStatus($Status, [TimeSpan]::FromSeconds($TimeoutSeconds)) | Out-Null
    } catch {
        Write-Warning "Timed out waiting for service status '$Status'"
    }
}

# ============================================================================
# Actions
# ============================================================================
function Invoke-Build {
    Write-Host "=== Build ===" -ForegroundColor Cyan
    if (-not (Test-Path $ProjectFile)) {
        throw "Project file not found: $ProjectFile"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet CLI not found in PATH. Install the .NET 8 SDK."
    }

    Write-Host "Publishing $ProjectFile -> $PublishDir"
    & dotnet publish $ProjectFile `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit code $LASTEXITCODE)."
    }
    if (-not (Test-Path $BinaryPath)) {
        throw "Build succeeded but binary not found at $BinaryPath"
    }
    Write-Host "Build OK -> $BinaryPath" -ForegroundColor Green
}

function Invoke-Install {
    Assert-Admin
    Write-Host "=== Install ===" -ForegroundColor Cyan

    if (Test-ServiceExists) {
        Write-Host "Service already exists; uninstalling first..." -ForegroundColor Yellow
        Invoke-Uninstall
    }

    if (-not (Test-Path $BinaryPath)) {
        Write-Host "Binary not found, building first..." -ForegroundColor Yellow
        Invoke-Build
    }

    # EventLog source needs to exist before the service starts logging to it.
    # New-EventLog requires Admin and is a no-op if the source already exists.
    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
        Write-Host "Creating EventLog source '$ServiceName' under Application log..."
        New-EventLog -LogName Application -Source $ServiceName
    }

    Write-Host "Registering service '$ServiceName' with SCM..."
    & sc.exe create $ServiceName binPath= "`"$BinaryPath`"" DisplayName= "`"$ServiceDisplay`"" start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed." }

    & sc.exe description $ServiceName "$ServiceDesc" | Out-Null

    # Auto-restart on crash:
    #   1st failure -> restart after  5s
    #   2nd failure -> restart after 10s
    #   3rd failure -> restart after 30s
    #   counter resets to zero after 24h of stability (86400 seconds)
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

    # Trigger restart actions for any non-zero exit (failureflag = 1).
    & sc.exe failureflag $ServiceName 1 | Out-Null

    Write-Host "Starting service..."
    Start-Service -Name $ServiceName
    Wait-ForServiceStatus -Status 'Running' -TimeoutSeconds 15

    Get-Service -Name $ServiceName | Format-Table Name, Status, StartType
    Write-Host "Install complete." -ForegroundColor Green
}

function Invoke-Uninstall {
    Assert-Admin
    Write-Host "=== Uninstall ===" -ForegroundColor Cyan

    if (-not (Test-ServiceExists)) {
        Write-Host "Service '$ServiceName' is not installed."
        return
    }

    $svc = Get-Service -Name $ServiceName
    if ($svc.Status -eq 'Running') {
        Write-Host "Stopping service..."
        Stop-Service -Name $ServiceName -Force
        Wait-ForServiceStatus -Status 'Stopped' -TimeoutSeconds 30
    }

    Write-Host "Deleting service..."
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Uninstalled." -ForegroundColor Green
}

function Invoke-Reinstall {
    Invoke-Uninstall
    Invoke-Build
    Invoke-Install
}

function Invoke-Start {
    Assert-Admin
    if (-not (Test-ServiceExists)) { throw "Service '$ServiceName' is not installed." }
    Start-Service -Name $ServiceName
    Wait-ForServiceStatus -Status 'Running' -TimeoutSeconds 15
    Get-Service -Name $ServiceName | Format-Table Name, Status, StartType
}

function Invoke-Stop {
    Assert-Admin
    if (-not (Test-ServiceExists)) { throw "Service '$ServiceName' is not installed." }
    Stop-Service -Name $ServiceName -Force
    Wait-ForServiceStatus -Status 'Stopped' -TimeoutSeconds 30
    Get-Service -Name $ServiceName | Format-Table Name, Status, StartType
}

function Invoke-Restart {
    Invoke-Stop
    Start-Sleep -Seconds 1
    Invoke-Start
}

function Invoke-Status {
    if (-not (Test-ServiceExists)) {
        Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
        return
    }

    Get-Service -Name $ServiceName | Format-List Name, DisplayName, Status, StartType
    Write-Host ""

    Write-Host "--- Recent log ---" -ForegroundColor Cyan
    if (Test-Path $LogsDir) {
        $log = Get-ChildItem -Path $LogsDir -Filter 'downganizer-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) {
            Write-Host $log.FullName
            Write-Host "(last 30 lines)"
            Get-Content -Path $log.FullName -Tail 30
        } else {
            Write-Host "(no log file in $LogsDir yet)"
        }
    } else {
        Write-Host "(logs directory $LogsDir does not exist yet)"
    }
}

# ============================================================================
# Dispatch
# ============================================================================
switch ($Action) {
    'Build'     { Invoke-Build }
    'Install'   { Invoke-Install }
    'Uninstall' { Invoke-Uninstall }
    'Reinstall' { Invoke-Reinstall }
    'Start'     { Invoke-Start }
    'Stop'      { Invoke-Stop }
    'Restart'   { Invoke-Restart }
    'Status'    { Invoke-Status }
}
