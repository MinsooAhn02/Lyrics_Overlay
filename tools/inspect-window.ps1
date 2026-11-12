# 사용: powershell -File inspect-window.ps1 -Name LyricsOnTheGo [-Texts]
# 출력: 프로세스 트리 메모리(작업집합/개인), 보이는 창의 스타일·위치, (옵션) UIA 텍스트
param([Parameter(Mandatory)][string]$Name, [switch]$Texts)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @"
using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;
public class WInspect{
 public delegate bool EP(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EP f,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GL(IntPtr h,int i);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 public struct RECT{public int L,T,R,B;}
 public static List<IntPtr> ForPid(uint pid){var l=new List<IntPtr>();EnumWindows((h,x)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid)l.Add(h);return true;},IntPtr.Zero);return l;}
}
"@
$root = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $root) { Write-Output "NOT RUNNING: $Name"; exit 2 }
$all = Get-CimInstance Win32_Process; $ids=@($root.Id); $g=$true
while($g){ $g=$false; foreach($x in $all){ if($ids -contains $x.ParentProcessId -and $ids -notcontains $x.ProcessId){ $ids+=$x.ProcessId; $g=$true } } }
$ps = Get-Process -Id $ids -ErrorAction SilentlyContinue
$ws = [math]::Round(($ps | Measure-Object WorkingSet64 -Sum).Sum/1MB,1)
$pv = [math]::Round(($ps | Measure-Object PrivateMemorySize64 -Sum).Sum/1MB,1)
Write-Output "processes=$($ps.Count) workingSetMB=$ws privateMB=$pv threads=$(($root.Threads).Count) uptimeSec=$([int]((Get-Date)-$root.StartTime).TotalSeconds)"
foreach($h in [WInspect]::ForPid($root.Id)){
  if(-not [WInspect]::IsWindowVisible($h)){ continue }
  $ex=[int64][WInspect]::GL($h,-20); $r=New-Object WInspect+RECT; [void][WInspect]::GetWindowRect($h,[ref]$r)
  $sb=New-Object Text.StringBuilder 256; [void][WInspect]::GetWindowText($h,$sb,256)
  Write-Output ("window hwnd={0} title='{1}' topmost={2} clickthru={3} layered={4} rect={5},{6},{7},{8}" -f $h,$sb,(($ex -band 0x8) -ne 0),(($ex -band 0x20) -ne 0),(($ex -band 0x80000) -ne 0),$r.L,$r.T,$r.R,$r.B)
  if($Texts){
    try{ $el=[Windows.Automation.AutomationElement]::FromHandle($h)
      $names=@(); foreach($e in $el.FindAll('Descendants',[Windows.Automation.Condition]::TrueCondition)){ $n=$e.Current.Name; if($n){$names+=$n} }
      Write-Output ("  texts: " + (($names | Select-Object -Unique | Select-Object -First 20) -join ' | '))
    }catch{ Write-Output "  uia error: $($_.Exception.Message)" }
  }
}
