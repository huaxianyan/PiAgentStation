# 打客户端的安装程序：一个 MSI，装到 Program Files，装时填 Seven Station Core 地址与令牌。
#
# 用法：
# powershell -File scripts/build-installer-client.ps1 # 编译 + 打包
# powershell -File scripts/build-installer-client.ps1 -SkipBuild # 用已有 release 产物
#
# 产物：dist/SevenStation-win-x64-<版本>.msi 与同名 .sha256
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$project = Join-Path $root "src/SevenStation.Desktop"
$installer = Join-Path $root "installer/client/SevenStation.wxs"
$buildDir = Join-Path $project "target/release"

$wix = Join-Path $env:USERPROFILE ".dotnet/tools/wix.exe"
if (-not (Test-Path $wix)) {
    throw "没找到 wix。装一下：dotnet tool install -g wix --version 5.0.2"
}

# 客户端是 Rust 项目，版本号在 Cargo.toml。
$cargo = Get-Content -Raw (Join-Path $project "Cargo.toml")
$match = [regex]::Match($cargo, '(?m)^version\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw "找不到版本号：$project/Cargo.toml" }
$version = $match.Groups[1].Value.Trim()
Write-Host "版本 $version"

if (-not $SkipBuild) {
    # cargo 在 rustup 的 bin 下，别假设 PATH 里有。
    $env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
    Push-Location $project
    try {
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "客户端编译失败" }
    } finally {
        Pop-Location
    }
}

$exe = Join-Path $buildDir "SevenStation.exe"
if (-not (Test-Path $exe)) { throw "没有编译产物：$exe" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$license = Join-Path $root "installer/license.rtf"
$name = "SevenStation-win-x64-$version"
$msi = Join-Path $dist "$name.msi"
Remove-Item $msi -Force -ErrorAction SilentlyContinue
Remove-Item "$msi.sha256" -Force -ErrorAction SilentlyContinue

Push-Location (Split-Path -Parent $installer)
try {
    & $wix build (Split-Path -Leaf $installer) -arch x64 -o $msi `
        -ext WixToolset.UI.wixext `
        -d "Version=$version" -d "AssetsDir=$root/installer/assets" -d "IconFile=$root/src/SevenStation.Desktop/icons/icon.ico" -d "BuildDir=$buildDir" -d "LicenseRtf=$license"
    if ($LASTEXITCODE -ne 0) { throw "打包失败" }
} finally {
    Pop-Location
}

$sha = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($msi)
try {
    $hash = ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
} finally {
    $stream.Dispose()
    $sha.Dispose()
}
[System.IO.File]::WriteAllText("$msi.sha256", "$hash $name.msi`n")

$sizeKb = [math]::Round((Get-Item $msi).Length / 1KB, 0)
Write-Host "$name.msi $sizeKb KB $($hash.Substring(0, 16))…"
Write-Host "产物在 $dist"
