# SysFloat 本机部署脚本
# 约定：仓库根目录的 SysFloat.exe 始终为最新构建（发布 -> 备份旧版 -> 替换 -> 重启 -> 自检）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy.ps1
# 说明：部署的是同源码的新构建，字节可能与 GitHub Release 附件因打包时间戳略有差异；
#       每次部署在 local\deploy-log.txt 记录 git 提交、版本与哈希，便于追溯。

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$publishDir = Join-Path $repo 'release\win-x64-self-contained'
$rootExe = Join-Path $repo 'SysFloat.exe'

Write-Output "== publish (self-contained single file) =="
dotnet publish (Join-Path $repo 'src\SysFloat\SysFloat.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:UseAppHost=true -p:SuppressTfmSupportBuildWarnings=true `
    -o $publishDir --force | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$newExe = Join-Path $publishDir 'SysFloat.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

Write-Output "== backup current root exe =="
if (Test-Path $rootExe) {
    New-Item -ItemType Directory -Force (Join-Path $repo 'backup') | Out-Null
    Copy-Item $rootExe (Join-Path $repo ("backup\SysFloat-before-deploy-$stamp.exe")) -Force
}

Write-Output "== stop running instance and replace =="
Get-Process SysFloat -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item $newExe $rootExe -Force

Write-Output "== restart and verify =="
Start-Process $rootExe
Start-Sleep -Seconds 4
$p = Get-Process SysFloat -ErrorAction SilentlyContinue
if (-not $p) { throw "deploy verification failed: SysFloat.exe is not running" }

$gitSha = (git rev-parse HEAD)
$hash = (Get-FileHash $rootExe -Algorithm SHA256).Hash.ToLower()
$version = (Get-Item $rootExe).VersionInfo.ProductVersion
New-Item -ItemType Directory -Force (Join-Path $repo 'local') | Out-Null
Add-Content (Join-Path $repo 'local\deploy-log.txt') ("$stamp  v$version  git=$gitSha  sha256=$hash  pid=$($p[0].Id)")
Write-Output ("deployed v$version  pid=$($p[0].Id)  sha256=$hash")
Write-Output "root SysFloat.exe is now the latest build."
