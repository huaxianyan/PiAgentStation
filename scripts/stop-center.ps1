# 停掉中心。
$running = Get-Process MAStation -ErrorAction SilentlyContinue
if (-not $running) {
    Write-Host "中心没在跑。"
    exit 0
}

foreach ($process in $running) {
    Stop-Process -Id $process.Id -Force
    Write-Host "已停止中心（PID $($process.Id)）。"
}
