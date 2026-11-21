param([string]$Setup,[string]$Profile,[string]$InstallDir)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $InstallDir 'TomatoDesktop.exe'
$uninstaller = Join-Path $InstallDir '卸载番茄桌面.exe'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNookValidation'
$oldData = Join-Path $Profile 'Data'
$data = Join-Path $InstallDir 'Data\Collection'
$desktop = Join-Path $Profile 'Desktop'
$statePath = Join-Path $data 'state.json'

function Get-Fingerprints($directory) {
    @(Get-ChildItem -LiteralPath $directory -File -Recurse -Force |
        Get-FileHash | Sort-Object Path | ForEach-Object { [IO.Path]::GetRelativePath($directory,$_.Path) + '|' + $_.Hash }) -join [Environment]::NewLine
}

function Assert-DataPreserved {
    if ((Get-FileHash -LiteralPath $statePath).Hash -ne $stateBefore -or
        (Get-Fingerprints (Join-Path $data 'vault')) -ne $vaultBefore -or
        (Get-Fingerprints $desktop) -ne $desktopBefore) {
        throw 'Collection state, stored files, or desktop contents changed.'
    }
}

function Run-Setup {
    $process = Start-Process -FilePath $Setup -ArgumentList "/S /D=$InstallDir" -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Setup exit $($process.ExitCode)" }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $uninstaller -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $InstallDir 'Data') -PathType Container) -or
        (Get-ItemPropertyValue -LiteralPath $registration -Name UninstallString) -ne ('"' + $uninstaller + '"')) {
        throw 'Setup did not register the uninstaller in the chosen folder.'
    }
}

function Run-Uninstall {
    $process = Start-Process -FilePath $uninstaller -ArgumentList '/S' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $exe) -or
        (Test-Path -LiteralPath $uninstaller) -or (Test-Path -LiteralPath $registration)) {
        throw 'Uninstall did not remove the program, uninstaller, and registration.'
    }
    Assert-DataPreserved
}

function Start-IsolatedApp {
    $ready = Join-Path $Profile ('installer-ready-' + [guid]::NewGuid().ToString('N') + '.json')
    $arguments = '--sandbox "' + $Profile + '" --installed-sandbox --autostart --update-ready "' + $ready + '"'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($process.HasExited -or $watch.Elapsed.TotalSeconds -ge 15) { throw 'Sandbox application did not start.' }
        Start-Sleep -Milliseconds 100
    }
    return $process
}

$stateBefore = (Get-FileHash -LiteralPath (Join-Path $oldData 'state.json')).Hash
$vaultBefore = Get-Fingerprints (Join-Path $oldData 'vault')
$desktopBefore = Get-Fingerprints $desktop

Run-Setup
$previous = Start-IsolatedApp
Assert-DataPreserved
Run-Setup
if (-not $previous.WaitForExit(3000)) { throw 'Upgrade did not stop the previous application.' }
Assert-DataPreserved
$running = Start-IsolatedApp
Run-Uninstall
if (-not $running.WaitForExit(3000)) { throw 'Uninstall did not stop the application.' }
Run-Setup
Assert-DataPreserved
Run-Uninstall

[pscustomobject]@{
    Installed = $true
    Upgraded = $true
    UninstallerInProgramFolder = $true
    DataInProgramFolder = $true
    UserStateHashPreserved = $true
    StoredFilesPreserved = $true
    DesktopUnchanged = $true
    Uninstalled = $true
    Reinstalled = $true
    RemainingItems = (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Items.Count
    LiveUserDataTouched = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\test-results\installer-verification.json') -Encoding utf8
