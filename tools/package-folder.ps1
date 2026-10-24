# SysFloat 文件夹版打包脚本
# 流程：非单文件自包含发布 -> 剥离未使用的 WPF/设计时组件 -> 同步修剪 deps.json -> ZIP
# 产出：release\packaging\SysFloat\（可直接运行的文件夹）与 release\SysFloat-folder.zip
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\package-folder.ps1 [-Version 1.2.2]

param(
    [string]$Version = "1.2.2"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$packRoot = Join-Path $repo 'release\packaging'
$folder = Join-Path $packRoot 'SysFloat'
$zipPath = Join-Path $repo ("release\SysFloat-v" + $Version + ".zip")

# 剥离清单：本项目为纯 WinForms（未用 WPF），设计时组件运行时不需要。
$stripPattern = '^(PresentationFramework(\..*)?|PresentationFramework-.*|PresentationCore|PresentationUI|WindowsBase|ReachFramework|System\.Xaml|WindowsFormsIntegration|D3DCompiler_47_cor3|PenImc_cor3|wpfgfx_cor3|vcruntime140_cor3|System\.Windows\.Forms\.Design|System\.Design|System\.Printing)\.dll$'

Write-Output "== publish (self-contained, folder layout) =="
if (Test-Path $packRoot) { Remove-Item -Recurse -Force $packRoot }
dotnet publish (Join-Path $repo 'src\SysFloat\SysFloat.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:UseAppHost=true -p:SuppressTfmSupportBuildWarnings=true `
    -o $folder --force | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$before = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB

Write-Output "== strip unused WPF / design-time assemblies =="
$removed = @()
Get-ChildItem $folder -Recurse -File -Filter *.dll | ForEach-Object {
    if ($_.Name -match $stripPattern) {
        $removed += $_.FullName.Substring($folder.Length + 1)
        Remove-Item -Force $_.FullName
    }
}
Write-Output ("removed {0} files: {1}" -f $removed.Count, ($removed | Select-Object -First 6) -join ', ')

Write-Output "== prune deps.json entries for stripped files =="
$depsPath = Join-Path $folder 'SysFloat.deps.json'
$deps = Get-Content $depsPath -Raw | ConvertFrom-Json
$pruned = 0
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($package in $target.Value.PSObject.Properties) {
        # 运行时程序集在 runtime 段，原生 DLL（WPF 的 D3DCompiler 等）在 native 段。
        foreach ($section in @('runtime', 'native')) {
            $assets = $package.Value.$section
            if ($assets -eq $null) { continue }
            foreach ($entry in @($assets.PSObject.Properties)) {
                $file = Split-Path -Leaf $entry.Name
                if ($file -match $stripPattern) {
                    $assets.PSObject.Properties.Remove($entry.Name) | Out-Null
                    $pruned++
                }
            }
        }
    }
}
$deps | ConvertTo-Json -Depth 64 -Compress | Set-Content $depsPath -Encoding UTF8
Write-Output ("pruned {0} deps.json runtime entries" -f $pruned)

# 自检：deps.json 中不得再引用任何被剥离的文件。
$depsText = Get-Content $depsPath -Raw
foreach ($name in ($removed | ForEach-Object { Split-Path -Leaf $_ } | Select-Object -Unique)) {
    if ($depsText -match [regex]::Escape($name)) { throw "deps.json still references stripped file: $name" }
}

$after = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Output ("size: {0:N1} MiB -> {1:N1} MiB ({2} files)" -f $before, $after, (Get-ChildItem $folder -Recurse -File).Count)

Write-Output "== zip =="
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path $folder -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output ("zip: {0} ({1:N1} MiB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
Write-Output "== done. Test by running: $folder\SysFloat.exe =="
