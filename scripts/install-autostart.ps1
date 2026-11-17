# 登录时自动把中心拉起来（Windows 计划任务，不弹窗口）。
#
# 用法：
#   pwsh -File scripts/install-autostart.ps1            # 安装
#   pwsh -File scripts/install-autostart.ps1 -Remove    # 卸载
#   pwsh -File scripts/install-autostart.ps1 -Status    # 看现在装没装
param(
    [switch]$Remove,
    [switch]$Status,
    [string]$Listen = ""
)

$ErrorActionPreference = "Stop"
$taskName = "SevenStation Center"
$root = Split-Path -Parent $PSScriptRoot
$startScript = Join-Path $PSScriptRoot "start-center.ps1"

if ($Status) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        Write-Host "已安装：$($task.State)，上次运行 $($info.LastRunTime)"
    } else {
        Write-Host "没装。"
    }
    exit 0
}

if ($Remove) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Host "本来就没装。"
        exit 0
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "已移除开机自启。"
    exit 0
}

$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$startScript`""
if ($Listen) { $arguments += " -Listen $Listen" }

$action = New-ScheduledTaskAction -Execute "pwsh.exe" -Argument $arguments -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "登录后把 SevenStation 中心拉起来" -Force | Out-Null

Write-Host "已安装：登录后自动启动中心（任务名：$taskName）"
Write-Host "现在不用等下次登录，直接 pwsh -File scripts/start-center.ps1 就能起来。"
