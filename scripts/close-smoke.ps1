# 关窗口回归：客户端开窗再关，不能留下崩溃记录。
# 用法：pwsh -NoProfile -File scripts/close-smoke.ps1
# 前置：客户端已构建（bin\Debug\net10.0-windows\PiAgentStation.exe）。
param(
    [string]$Exe = (Join-Path $PSScriptRoot '../src/PiAgentStation.Desktop/bin/Debug/net10.0-windows/PiAgentStation.exe'),
    [int]$WaitSeconds = 12
)

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class CloseSmoke {
    public delegate bool EnumProc(IntPtr handle, IntPtr param);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);

    public static int CloseWindow(uint targetPid, string title) {
        int closed = 0;
        EnumWindows((handle, param) => {
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (pid != targetPid) { return true; }
            var text = new StringBuilder(256);
            GetWindowText(handle, text, 256);
            if (text.ToString() == title) {
                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                closed++;
            }
            return true;
        }, IntPtr.Zero);
        return closed;
    }
}
'@

$Exe = (Resolve-Path $Exe).Path
$logPath = Join-Path $env:LOCALAPPDATA 'PiAgentStation/logs/app.log'

Get-Process PiAgentStation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 4

$mark = Get-Date
$process = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) -PassThru
Start-Sleep -Seconds $WaitSeconds

if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
    Write-Output ('✗ 窗口没开起来（pid=' + $process.Id + '，还在的客户端进程 ' +
        @(Get-Process PiAgentStation -ErrorAction SilentlyContinue).Count + ' 个）')
    Write-Output '日志尾部：'
    Get-Content $logPath -Tail 3 -ErrorAction SilentlyContinue
    exit 1
}

$sent = [CloseSmoke]::CloseWindow($process.Id, 'PiAgentStation')
if ($sent -lt 1) {
    Write-Output '✗ 没找到主窗口'
    exit 1
}

for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { break }
}

if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
    Write-Output '✗ 关窗口后进程还活着'
    exit 1
}

Start-Sleep -Seconds 3
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $mark } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*PiAgentStation*' -and ($_.Id -eq 1000 -or $_.Id -eq 1001) })

if ($crashes.Count -gt 0) {
    foreach ($crash in $crashes) {
        Write-Output ('✗ 关闭时崩溃：' + $crash.TimeCreated.ToString('HH:mm:ss'))
    }
    exit 1
}

Write-Output '✓ 关窗口干净退出，没有崩溃记录'
