Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W4 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Find(string cls) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      if (c.ToString() == cls) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
Add-Type -AssemblyName System.Drawing
$env:SEVENSTATION_SERVICE = 'SevenStationCore-IsolationTest'
$env:SEVENSTATION_CORE_HOME = 'E:\tmp\cc\iso-home'
Start-Process 'E:\Dev\SevenStation\src\SevenStation.Control\target\release\sevenstation-control.exe'
Start-Sleep -Seconds 9
$h = [W4]::Find('Tauri Window')
if ($h -ne [IntPtr]::Zero) {
  $r = New-Object W4+RECT
  [W4]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
  $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  $hdc = $g.GetHdc()
  [W4]::PrintWindow($h, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc)
  $bmp.Save('E:\tmp\cc\iso-control.png', [System.Drawing.Imaging.ImageFormat]::Png)
  '拍到了 ' + $w + 'x' + $hh
} else { '没有窗口' }
'--- 生产服务 ---'
(Get-Service SevenStationCore).Status
Get-Process sevenstation-control -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
