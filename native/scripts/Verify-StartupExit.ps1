param([string]$Exe,[string]$Root)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class NookStartupProbe{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);[DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);}'
function Find-UI($root,$name,$type){$condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$type));for($i=0;$i -lt 20;$i++){$found=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition);if($found){return $found};[Threading.Thread]::Sleep(60)};throw ('UI missing: '+$name)}
function Invoke-UI($element){([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}
$app=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--autostart','--keep-open-verification') -WindowStyle Hidden -PassThru
for($i=0;$i -lt 40;$i++){$app.Refresh();if($app.MainWindowHandle -ne [IntPtr]::Zero){break};[Threading.Thread]::Sleep(100)}
$handle=$app.MainWindowHandle
if(-not[NookStartupProbe]::IsIconic($handle)){throw 'Autostart did not minimize'}
$open=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--keep-open-verification') -WindowStyle Hidden -PassThru; $null=$open.WaitForExit(3000)
$ui=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
$before=(Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash
Write-Output "More phase 1"
Write-Output ($ui.Current.BoundingRectangle.ToString())
Invoke-UI (Find-UI $ui '更多操作' ([System.Windows.Automation.ControlType]::Button))
$closeOption=Find-UI $ui '关闭时结束进程' ([System.Windows.Automation.ControlType]::MenuItem)
([System.Windows.Automation.TogglePattern]$closeOption.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
[Threading.Thread]::Sleep(180)
Invoke-UI (Find-UI $ui '关闭番茄' ([System.Windows.Automation.ControlType]::Button))
[Threading.Thread]::Sleep(150)
if($app.HasExited -or -not[NookStartupProbe]::IsIconic($handle)){throw 'Close-to-hide preference failed'}
$open=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--keep-open-verification') -WindowStyle Hidden -PassThru; $null=$open.WaitForExit(3000)
Write-Output "More phase 2"
Write-Output ($ui.Current.BoundingRectangle.ToString())
Invoke-UI (Find-UI $ui '更多操作' ([System.Windows.Automation.ControlType]::Button))
$closeOption=Find-UI $ui '关闭时结束进程' ([System.Windows.Automation.ControlType]::MenuItem)
([System.Windows.Automation.TogglePattern]$closeOption.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
[Threading.Thread]::Sleep(180)
Invoke-UI (Find-UI $ui '关闭番茄' ([System.Windows.Automation.ControlType]::Button))
if(-not $app.WaitForExit(5000)){throw 'Close button did not terminate process'}
if((Get-FileHash -LiteralPath (Join-Path $Root 'Data\state.json')).Hash -ne $before){throw 'Closing modified collection data'}
$app=Start-Process -FilePath $Exe -ArgumentList @('--sandbox',('"'+$Root+'"'),'--keep-open-verification') -WindowStyle Hidden -PassThru
for($i=0;$i -lt 40;$i++){$app.Refresh();if($app.MainWindowHandle -ne [IntPtr]::Zero){break};[Threading.Thread]::Sleep(100)}
$null=$app.WaitForInputIdle(5000)
[Threading.Thread]::Sleep(150)
$ui=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
Write-Output "More phase 3"
Write-Output ($ui.Current.BoundingRectangle.ToString())
Invoke-UI (Find-UI $ui '更多操作' ([System.Windows.Automation.ControlType]::Button))
Invoke-UI (Find-UI $ui '退出并结束进程' ([System.Windows.Automation.ControlType]::MenuItem))
if(-not $app.WaitForExit(5000)){throw 'Exit menu did not terminate process'}
[pscustomobject]@{AutostartStartsMinimized=$true;CloseToHideWorks=$true;CloseToExitTerminatesProcess=$true;ExitMenuTerminatesProcess=$true;CollectionStateUnchanged=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Root 'startup-exit-verification.json') -Encoding utf8
