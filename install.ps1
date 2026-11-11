<#
.SYNOPSIS
    Builds SearchAnything from this source code and installs it for the current user.

.DESCRIPTION
    Runs the unit tests, publishes a self-contained SearchAnything.exe, copies it to
    %LOCALAPPDATA%\Programs\SearchAnything and adds a SearchAnything shortcut to your
    Start menu, so Windows Search finds it. Then it starts the app (it waits in the tray).

    Run it again after `git pull` to update. No administrator rights are needed, and
    nothing outside your own user profile is changed.

.PARAMETER Uninstall
    Closes SearchAnything and removes the installed .exe, the Start menu shortcut and
    "Start with Windows". Your settings in %APPDATA%\SearchAnything are kept.

.PARAMETER SkipTests
    Don't run the unit tests before building.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$AppName     = 'SearchAnything'
$InstallDir  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$InstalledExe = Join-Path $InstallDir "$AppName.exe"
$Shortcut    = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RepoRoot    = $PSScriptRoot
$PublishDir  = Join-Path $RepoRoot 'publish'

function Stop-InstalledApp {
    # Only the copy in the install folder: one you're running from bin\ or publish\ is left alone.
    $running = Get-Process -Name $AppName -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -ieq $InstalledExe) }
    foreach ($p in $running) {
        Write-Host "Closing the running $AppName..."
        $p.Kill()
        $p.WaitForExit(5000) | Out-Null
    }
}

function Remove-StartWithWindows {
    $value = (Get-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue).$AppName
    if ($value -and ($value.Trim('"') -ieq $InstalledExe)) {
        Remove-ItemProperty -Path $RunKey -Name $AppName
        Write-Host 'Turned off Start with Windows.'
    }
}

if ($Uninstall) {
    Stop-InstalledApp
    Remove-StartWithWindows
    if (Test-Path $Shortcut) {
        Remove-Item $Shortcut
        Write-Host "Removed the Start menu shortcut."
    }
    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse
        Write-Host "Removed $InstallDir"
    }
    Write-Host "$AppName is uninstalled. Your settings are still in $env:APPDATA\$AppName; delete that folder too if you want them gone."
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.'
}

# The PC's own architecture. $env:PROCESSOR_ARCHITECTURE can say AMD64 on ARM PCs when PowerShell runs emulated.
$arch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
$runtime = if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

Push-Location $RepoRoot
try {
    if (-not $SkipTests) {
        Write-Host 'Running the tests...'
        dotnet test SearchAnything.slnx -c Release
        if ($LASTEXITCODE -ne 0) { throw 'The tests failed, so nothing was installed.' }
    }

    Write-Host "Building $AppName.exe for $runtime..."
    dotnet publish src/SearchAnything -c Release -r $runtime -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw 'The build failed, so nothing was installed.' }
}
finally {
    Pop-Location
}

$builtExe = Join-Path $PublishDir "$AppName.exe"
if (-not (Test-Path $builtExe)) { throw "The build didn't produce $builtExe." }

Stop-InstalledApp
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $builtExe $InstalledExe -Force
Write-Host "Installed $InstalledExe"

$shell = New-Object -ComObject WScript.Shell
try {
    $link = $shell.CreateShortcut($Shortcut)
    $link.TargetPath = $InstalledExe
    $link.WorkingDirectory = $InstallDir
    $link.Description = 'Ctrl+F for anything on your screen.'
    $link.Save()
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}
Write-Host 'Added SearchAnything to the Start menu.'

# If Start with Windows points at an older copy, starting this one moves it here (the app updates it on launch).
Start-Process $InstalledExe -WorkingDirectory $InstallDir
Write-Host "$AppName is running in the tray. Press Ctrl+Alt+F to search your screen."
