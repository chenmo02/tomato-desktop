param([string]$Setup,[string]$Profile,[string]$InstallDir)
$ErrorActionPreference='Stop'
function Run-Setup { $p=Start-Process -FilePath $Setup -ArgumentList @('/S',('/D='+$InstallDir)) -WindowStyle Hidden -PassThru; $p.WaitForExit(); if($p.ExitCode -ne 0){throw ('Setup exit '+$p.ExitCode)} }
$before=(Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash
Run-Setup
$exe=Join-Path $InstallDir 'DesktopNook.exe'
$app=Start-Process -FilePath $exe -ArgumentList @('--sandbox',('"'+$Profile+'"')) -WindowStyle Hidden -PassThru
Run-Setup
if((Get-FileHash -LiteralPath (Join-Path $Profile 'Data\state.json')).Hash -ne $before){throw 'Upgrade changed user state'}
$uninstaller=Join-Path $InstallDir '卸载番茄桌面.exe'
$removal=Start-Process -FilePath $uninstaller -ArgumentList @('/S',('_?='+$InstallDir)) -WindowStyle Hidden -PassThru
$removal.WaitForExit()
$state=Get-Content -LiteralPath (Join-Path $Profile 'Data\state.json') -Raw|ConvertFrom-Json
if($removal.ExitCode -ne 0 -or $state.Items.Count -ne 0 -or (Test-Path -LiteralPath $exe)){throw 'Uninstall recovery failed'}
[pscustomobject]@{Installed=$true;Upgraded=$true;UserStateHashPreserved=$true;Uninstalled=$true;RemainingItems=$state.Items.Count;RecoveredFiles=@(Get-ChildItem -LiteralPath (Join-Path $Profile 'Desktop') -File).Count;LiveUserDataTouched=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\test-results\installer-verification.json') -Encoding utf8
