param([string]$Exe)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public static class NookToggleCheck{[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindow(string c,string n);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);[DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);}'
$app=Get-Process -Name 'DesktopNook'|Where-Object Path -eq $Exe|Select-Object -First 1
$handle=$app.MainWindowHandle
$tray=[System.Windows.Automation.AutomationElement]::FromHandle([NookToggleCheck]::FindWindow('Shell_TrayWnd',$null))
$button=$tray.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'Appid: DesktopNook.DesktopNook'))
$rect=$button.Current.BoundingRectangle
[NookToggleCheck]::SendMessage($handle,0x112,[IntPtr]0xF020,[IntPtr]::Zero)|Out-Null
[Threading.Thread]::Sleep(200)
$states=[System.Collections.Generic.List[bool]]::new()
for($i=0;$i -lt 4;$i++){[NookToggleCheck]::SetCursorPos([int]($rect.Left+$rect.Width/2),[int]($rect.Top+$rect.Height/2))|Out-Null;[NookToggleCheck]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[NookToggleCheck]::mouse_event(4,0,0,0,[UIntPtr]::Zero);[Threading.Thread]::Sleep(450);$states.Add(-not[NookToggleCheck]::IsIconic($handle))}
[pscustomobject]@{VisibleAfterClicks=@($states);Expected=@($true,$false,$true,$false);Passed=($states.Count -eq 4 -and $states[0] -and -not $states[1] -and $states[2] -and -not $states[3])}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\发布\任务栏切换验证.json') -Encoding utf8
[NookToggleCheck]::SendMessage($handle,0x112,[IntPtr]0xF120,[IntPtr]::Zero)|Out-Null
