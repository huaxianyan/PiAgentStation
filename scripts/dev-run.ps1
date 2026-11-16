# 杀掉旧实例、构建、带调试端口启动 Pi 界面。
#
# 用法：
#   pwsh -File scripts/dev-run.ps1
#   pwsh -File scripts/dev-run.ps1 -SessionDir E:\tmp\pigui-sessions
param(
    [string]$SessionDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\PiAgentStation\PiAgentStation.csproj"
$exe = Join-Path $root "src\PiAgentStation\bin\Debug\net10.0-windows\PiAgentStation.exe"

Get-Process PiAgentStation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

dotnet build $project -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=9222"
if ($SessionDir) {
    New-Item -ItemType Directory -Force -Path $SessionDir | Out-Null
    $env:PI_CODING_AGENT_SESSION_DIR = $SessionDir
    Write-Host "会话目录：$SessionDir"
}

Start-Process -FilePath $exe | Out-Null
Write-Host "已启动，调试端口 9222"
