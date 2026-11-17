# 把中心拉起来（后台常驻，不弹控制台窗口）。

#

# 用法：

#   pwsh -File scripts/start-center.ps1

#   pwsh -File scripts/start-center.ps1 -Listen http://0.0.0.0:8787

#   pwsh -File scripts/start-center.ps1 -Release

param(

    [string]$Listen = "",

    [switch]$Release

)



$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$logDir = Join-Path $env:LOCALAPPDATA "MAStation\logs"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null



$running = Get-Process MAStation -ErrorAction SilentlyContinue

if ($running) {

    Write-Host "中心已经在跑（PID $($running[0].Id)）。"

    exit 0

}



$configuration = if ($Release) { "Release" } else { "Debug" }

$exe = Join-Path $root "src/MAStation/bin/$configuration/net10.0/MAStation.exe"

if (-not (Test-Path $exe)) {

    Write-Host "还没构建：$exe"

    Write-Host "先跑：dotnet build src/MAStation/MAStation.csproj -c $configuration"

    exit 1

}



if ($Listen) { $env:MASTATION_LISTEN = $Listen }



$out = Join-Path $logDir "center.out.log"

$err = Join-Path $logDir "center.err.log"

# 工作目录必须是 exe 所在目录：静态页是相对它找 wwwroot 的。
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $out -RedirectStandardError $err



# 起来要几秒，等它真的在听再报成功。
$port = 8787
if ($env:MASTATION_LISTEN) {
    $match = [regex]::Match($env:MASTATION_LISTEN, ':(\d+)')
    if ($match.Success) { $port = [int]$match.Groups[1].Value }
}

function Test-Port([int]$target) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect("127.0.0.1", $target)
        return $true
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    if ($process.HasExited) {
        Write-Host "中心启动后退出了，看看日志：$err"
        exit 1
    }
    if (Test-Port $port) {
        Write-Host "中心已启动（PID $($process.Id)）：http://127.0.0.1:$port"
        Write-Host "日志：$out"
        exit 0
    }
}

Write-Host "30 秒内没等到端口，PID $($process.Id)，日志：$out"

exit 1
