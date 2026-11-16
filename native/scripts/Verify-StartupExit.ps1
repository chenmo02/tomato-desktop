param([string]$Exe,[string]$Root)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class NookStartupProbe{[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);[DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);}'
# 每次重试都从 HWND 重新取 AutomationElement：WPF 窗口最小化 / 恢复后旧元素会失效。
function Find-UI($handle,$name,$type){$condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$type));for($i=0;$i -lt 60;$i++){$r=[System.Windows.Automation.AutomationElement]::FromHandle($handle);$found=$r.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition);if($found){return $found};[Threading.Thread]::Sleep(100)};throw ('UI missing: '+$name)}
function Invoke-UI($element){([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
# 第二个实例只应唤醒已有面板后立即结束进程；残留的副本会锁住 bin 目录里的程序集。
function Activate-Instance(){ $p=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--keep-open-verification') -WindowStyle Hidden -PassThru; if(-not $p.WaitForExit(6000)){ $p.Kill(); throw 'Duplicate instance did not terminate after activating the running panel' } }
function Wait-Visible($handle,$expected){for($i=0;$i -lt 40;$i++){if([NookStartupProbe]::IsWindowVisible($handle) -eq $expected){return $true};[Threading.Thread]::Sleep(100)};return $false}
# 单实例激活偶发比窗口加载更快；重复发几次信号直到面板确实从最小化恢复。
function Ensure-Restored($handle){for($round=0;$round -lt 4;$round++){Activate-Instance;for($i=0;$i -lt 25;$i++){if((-not [NookStartupProbe]::IsIconic($handle)) -and [NookStartupProbe]::IsWindowVisible($handle)){return $true};[Threading.Thread]::Sleep(100)}};return $false}
function Start-Main($extraArgs){ $p=Start-Process -FilePath $Exe -ArgumentList (@('--sandbox',('"'+$Root+'"'))+$extraArgs) -WindowStyle Hidden -PassThru; for($i=0;$i -lt 80;$i++){$p.Refresh();if($p.MainWindowHandle -ne [IntPtr]::Zero){break};[Threading.Thread]::Sleep(100)}; return $p }

# 沙盒设置键由数据目录路径派生，与应用 App.OnStartup 的算法一致。
$fullRoot=[IO.Path]::GetFullPath($Root)
$sha=New-Object System.Security.Cryptography.SHA256Managed
$signature=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fullRoot.ToUpperInvariant()))).Replace('-','')).Substring(0,20)
$sha.Dispose()
$settingsKey="HKCU:\Software\DesktopNook\Sandbox\$signature\Settings"
New-Item -Path $settingsKey -Force | Out-Null
Remove-ItemProperty -LiteralPath $settingsKey -Name CloseEndsProcessV2 -ErrorAction SilentlyContinue
Remove-ItemProperty -LiteralPath $settingsKey -Name CloseEndsProcess -ErrorAction SilentlyContinue

$app=Start-Main @('--autostart','--keep-open-verification')
$handle=$app.MainWindowHandle
if($handle -eq [IntPtr]::Zero){throw 'Main window did not appear'}
if(-not[NookStartupProbe]::IsIconic($handle)){throw 'Autostart did not minimize'}
if(-not (Ensure-Restored $handle)){throw 'Single-instance activation did not restore the minimized panel'}
$before=(Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash

# 新默认：点 × 隐藏窗口到托盘，进程不退出，托盘图标常驻。
Invoke-UI (Find-UI $handle '关闭番茄' ([System.Windows.Automation.ControlType]::Button))
if(-not (Wait-Visible $handle $false)){if($app.HasExited){throw 'Close button terminated process instead of hiding to tray'};throw 'Close button did not hide the window into the tray'}
if($app.HasExited){throw 'Close button terminated process instead of hiding to tray'}
# 再启动一次（单实例激活）等价于点托盘图标，窗口应重新出现。
if(-not (Ensure-Restored $handle)){throw 'Tray background process did not restore the panel'}

# 显式开启「关闭时结束进程」后，点 × 才真正退出；该偏好写在设置页与注册表新键上。
Set-ItemProperty -LiteralPath $settingsKey -Name CloseEndsProcessV2 -Value 1 -Type DWord
Invoke-UI (Find-UI $handle '关闭番茄' ([System.Windows.Automation.ControlType]::Button))
if(-not $app.WaitForExit(6000)){throw 'Close-to-exit preference did not terminate process'}
if((Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash -ne $before){throw 'Closing modified collection data'}
Remove-ItemProperty -LiteralPath $settingsKey -Name CloseEndsProcessV2 -ErrorAction SilentlyContinue

# 「更多 → 退出并结束进程」始终是真正的退出入口。
$app=Start-Main @('--keep-open-verification')
$handle=$app.MainWindowHandle
if($handle -eq [IntPtr]::Zero){throw 'Second main window did not appear'}
$null=$app.WaitForInputIdle(5000)
Invoke-UI (Find-UI $handle '更多操作' ([System.Windows.Automation.ControlType]::Button))
Invoke-UI (Find-UI $handle '退出并结束进程' ([System.Windows.Automation.ControlType]::MenuItem))
if(-not $app.WaitForExit(6000)){ $app.Kill(); throw 'Exit menu did not terminate process' }

# 托盘右键「退出并结束进程」：先把面板收进托盘，再由内置探针点击真实菜单项；
# 这一条专门覆盖「窗口隐藏后 Application.Shutdown() 留下无窗口僵尸进程」的问题。
$marker=Join-Path $Root 'tray-exit-requested.txt'
Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
$tray=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--verify-tray-exit') -WindowStyle Hidden -PassThru
if(-not $tray.WaitForExit(10000)){ $tray.Kill(); throw 'Tray exit menu did not terminate the background process' }
if(-not (Test-Path -LiteralPath $marker)){throw 'Tray exit probe did not reach the exit menu item'}
if($tray.ExitCode -ne 0){throw ('Tray exit returned '+$tray.ExitCode)}

[pscustomobject]@{AutostartStartsMinimized=$true;DuplicateInstanceExits=$true;CloseHidesToTray=$true;TrayRestoreWorks=$true;CloseToExitTerminatesProcess=$true;ExitMenuTerminatesProcess=$true;TrayExitMenuTerminatesProcess=$true;CollectionStateUnchanged=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Root 'startup-exit-verification.json') -Encoding utf8
