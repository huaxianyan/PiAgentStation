# 打发布包：中心与客户端各一个 zip，自包含 win-x64。
#
# 用法：
#   pwsh -File scripts/build-release.ps1              # 按 csproj 里的版本号打
#   pwsh -File scripts/build-release.ps1 -SkipPublish # 用已有 publish 输出直接打包
#
# 产物落在 dist/：SevenStation-center-<版本>-win-x64.zip 与 SevenStation-client-<版本>-win-x64.zip，
# 各带一个同名 .sha256（sha256sum 能直接 -c 验证）。
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

$cargoBin = Join-Path $env:USERPROFILE ".cargo/bin"
if (Test-Path $cargoBin) { $env:PATH = "$cargoBin;$env:PATH" }

function Read-Version([string]$project) {
    $text = Get-Content -Raw $project
    $match = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "找不到版本号：$project" }
    return $match.Groups[1].Value.Trim()
}

$centerVersion = Read-Version (Join-Path $root "src/SevenStation.Core/SevenStation.Core.csproj")
$cargoText = Get-Content -Raw (Join-Path $root "src/SevenStation.Desktop/Cargo.toml")
$clientMatch = [regex]::Match($cargoText, '(?m)^version\s*=\s*"([^"]+)"')
if (-not $clientMatch.Success) { throw "找不到客户端版本号" }
$clientVersion = $clientMatch.Groups[1].Value.Trim()
if ($centerVersion -ne $clientVersion) {
    throw "两个项目的版本号不一致：中心 $centerVersion / 客户端 $clientVersion"
}
$version = $centerVersion

$notes = Join-Path $root "docs/release-notes/v$version.md"
if (-not (Test-Path $notes)) {
    throw "缺少发布说明：$notes"
}

Write-Host "版本 $version"

$centerOut = Join-Path $root "src/SevenStation.Core/bin/Release/net10.0/win-x64/publish"
$clientOut = Join-Path $root "src/SevenStation.Desktop/target/release"
$controlOut = Join-Path $root "src/SevenStation.Control/target/release"

if (-not $SkipPublish) {
    dotnet publish (Join-Path $root "src/SevenStation.Core/SevenStation.Core.csproj") `
        -c Release -r win-x64 --self-contained -o $centerOut -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "中心发布失败" }

    Push-Location (Join-Path $root "src/SevenStation.Desktop")
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "客户端发布失败" }
    } finally {
        Pop-Location
    }

    Push-Location (Join-Path $root "src/SevenStation.Control")
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "控制台发布失败" }
    } finally {
        Pop-Location
    }
}

foreach ($output in @($centerOut, $clientOut, $controlOut)) {
    if (-not (Test-Path $output)) { throw "没有发布输出：$output" }
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$centerName = "SevenStation-center-$version-win-x64"
$clientName = "SevenStation-client-$version-win-x64"
$controlName = "SevenStation-control-$version-win-x64"

# 打包前清掉上一次的同名 zip，否则 Compress-Archive 会把新内容塞进旧包。
foreach ($name in @($centerName, $clientName, $controlName)) {
    Remove-Item (Join-Path $dist "$name.zip") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dist "$name.zip.sha256") -Force -ErrorAction SilentlyContinue
}

# 双击就能起的入口。中心的工作目录必须是 exe 所在目录。
$launcher = @(
    '@echo off',
    'cd /d "%~dp0"',
    'start "" "SevenStation.Core.exe"'
) -join "`r`n"
Set-Content -Path (Join-Path $centerOut "启动中心.cmd") -Value $launcher -Encoding ascii

Compress-Archive -Path (Join-Path $centerOut "*") -DestinationPath (Join-Path $dist "$centerName.zip")

# 客户端只发那一个 exe：target/release 里还有 deps、build、pdb 这些构建中间产物，
# 整个压进去是 380 MB，而真正要发的只有 10 MB。
$clientStage = Join-Path $dist ".client-stage"
Remove-Item $clientStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $clientStage | Out-Null
$clientExe = Join-Path $clientOut "SevenStation.exe"
if (-not (Test-Path $clientExe)) { throw "没有客户端产物：$clientExe" }
Copy-Item $clientExe -Destination $clientStage
Compress-Archive -Path (Join-Path $clientStage "*") -DestinationPath (Join-Path $dist "$clientName.zip")
Remove-Item $clientStage -Recurse -Force

# 控制台单独一个包，装到中心那台机器上；exe 名字和中心的 SevenStation.Core.exe 对齐。
$controlStage = Join-Path $dist ".control-stage"
Remove-Item $controlStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $controlStage | Out-Null
$controlExe = Join-Path $controlOut "sevenstation-control.exe"
if (-not (Test-Path $controlExe)) { throw "没有控制台产物：$controlExe" }
Copy-Item $controlExe -Destination (Join-Path $controlStage "SevenStation.Control.exe")
Compress-Archive -Path (Join-Path $controlStage "*") -DestinationPath (Join-Path $dist "$controlName.zip")
Remove-Item $controlStage -Recurse -Force

# 这台机器的 Windows PowerShell 5.1 里没有 Get-FileHash，用 .NET 自己算。
function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

foreach ($name in @($centerName, $clientName, $controlName)) {
    $zip = Join-Path $dist "$name.zip"
    $hash = Get-Sha256 $zip
    # 与 sha256sum 的输出同格式：哈希、两个空格、文件名。
    # 行尾必须是 LF：Set-Content 写出来是 CRLF，sha256sum -c 会把回车当成文件名的一部分读失败。
    [System.IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "$name.zip  $sizeMb MB  $($hash.Substring(0, 16))…"
}

# 包内容对不上就直接失败，别把半成品发出去。
$clientZipBytes = (Get-Item (Join-Path $dist "$clientName.zip")).Length
if ($clientZipBytes -gt 40MB) {
    throw "客户端包 $([math]::Round($clientZipBytes / 1MB, 1)) MB，看着像把构建中间产物打进去了"
}

Write-Host "产物在 $dist"
