# 停掉 Seven Station Core。
$running = Get-Process SevenStation.Core -ErrorAction SilentlyContinue
if (-not $running) {
    Write-Host "Seven Station Core 没在跑。"
    exit 0
}

foreach ($process in $running) {
    Stop-Process -Id $process.Id -Force
    Write-Host "已停止 Seven Station Core（PID $($process.Id)）。"
}
