# SysFloat 发布前隐私检查
# 用途：扫描 git 跟踪的文本文件，查找可能包含的个人信息或机密内容。
# 退出码：0 = 通过（可能有仅提示的 WARN 项）；1 = 发现 HIGH 级别内容，需要处理后重新检查。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-check.ps1
#       可用 -AllowedUserNames 追加允许出现的系统用户名（如公共 CI 账户）。

param(
    [string[]]$AllowedUserNames = @('Public', 'Default', 'DefaultUser', 'DefaultUser0', 'All Users', 'SYSTEM', 'Administrator', 'WDAGUtilityAccount')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding {
    param([string]$Severity, [string]$File, [int]$Line, [string]$Kind, [string]$Detail)
    $script:findings.Add([pscustomobject]@{ Severity = $Severity; File = $File; Line = $Line; Kind = $Kind; Detail = $Detail })
}

$skipExtensions = @('.png', '.jpg', '.jpeg', '.gif', '.ico', '.bmp', '.exe', '.dll', '.pdb', '.pfx', '.snk', '.zip', '.7z', '.woff', '.woff2', '.ttf', '.otf', '.jar', '.pdf')
$files = @(git ls-files)
$scanned = 0

foreach ($file in $files) {
    $path = Join-Path $repo $file
    if (-not (Test-Path -LiteralPath $path)) { continue }
    # 跳过扫描器自身：检测规则里的示例字面量会误报
    if ($file -eq 'tools/privacy-check.ps1') { continue }
    $ext = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($skipExtensions -contains $ext) { continue }

    # 危险文件扩展：私钥/证书/密码库/环境变量文件不应进仓库
    if ($file -match '(?i)\.(pfx|key|pem|ppk|kdbx|crt|cer|env)$') {
        Add-Finding 'HIGH' $file 0 'secret-file' '敏感扩展名的文件不应提交'
        continue
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -eq 0) { continue }
    # 粗判二进制（含 NUL 字节）不按文本扫描
    if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
    $scanned++

    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $lines = $text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNo = $i + 1

        # 1) 用户目录路径：C:\Users\<name>、/home/<name>、/Users/<name>（HIGH）
        foreach ($m in [regex]::Matches($line, '(?i)(?:[a-z]:\\users\\|/home/|/users/)([a-z0-9_.\-]+)')) {
            $name = $m.Groups[1].Value
            if ($AllowedUserNames -notcontains $name) {
                Add-Finding 'HIGH' $file $lineNo 'user-path' $m.Value
            }
        }

        # 2) 邮箱地址（WARN：可能是公开联系方式，也可能是个人信息）
        foreach ($m in [regex]::Matches($line, '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+')) {
            Add-Finding 'WARN' $file $lineNo 'email' $m.Value
        }

        # 3) 机密赋值：key/secret/token/password = 有实际值（HIGH）
        if ($line -match '(?i)(api[_-]?key|access[_-]?key|secret|token|password|passwd|pwd)\s*[:=]\s*["'']?[^\s"''{}<>]{8,}') {
            Add-Finding 'HIGH' $file $lineNo 'secret-assignment' $Matches[0]
        }

        # 4) Bearer 令牌（HIGH）
        if ($line -match '(?i)bearer\s+[A-Za-z0-9._\-]{16,}') {
            Add-Finding 'HIGH' $file $lineNo 'bearer-token' $Matches[0]
        }

        # 5) 带签名的临时云链接（HIGH）
        if ($line -match '(?i)(signature|sig|token|apikey|api-key)=[A-Za-z0-9+/=%_\-]{8,}') {
            Add-Finding 'HIGH' $file $lineNo 'signed-url' $Matches[0]
        }

        # 6) 私网 IP（WARN：内网拓扑信息）
        if ($line -match '(?<![\d])(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\d])') {
            Add-Finding 'WARN' $file $lineNo 'private-ip' $Matches[1]
        }

        # 7) 超长十六进制串（WARN：可能是令牌或个人标识；发布哈希属正常，需人工确认）
        foreach ($m in [regex]::Matches($line, '(?i)(?<![0-9a-f])[0-9a-f]{32,}(?![0-9a-f])')) {
            Add-Finding 'WARN' $file $lineNo 'long-hex' $m.Value.Substring(0, [Math]::Min(16, $m.Value.Length)) + '…'
        }

        # 8) 超长 base64 形态串（WARN；字符类不含 / ，避免把 URL 当成 base64）
        foreach ($m in [regex]::Matches($line, '(?<![A-Za-z0-9+])[A-Za-z0-9+]{40,}={0,2}(?![A-Za-z0-9+])')) {
            Add-Finding 'WARN' $file $lineNo 'long-base64' $m.Value.Substring(0, 16) + '…'
        }

        # 9) RDP 会话名 / 计算机名样式（WARN）
        if ($line -match 'RDP-Tcp#') {
            Add-Finding 'WARN' $file $lineNo 'rdp-session' $line.Trim()
        }
    }
}

Write-Output ("扫描文件数: " + $scanned + " / git 跟踪文件数: " + $files.Count)
$gitUser = git config user.email
if ($gitUser) { Write-Output ("提交身份 user.email: " + $gitUser + "（将随提交公开，请自行确认）") }

$high = @($findings | Where-Object { $_.Severity -eq 'HIGH' })
$warn = @($findings | Where-Object { $_.Severity -eq 'WARN' })

if ($findings.Count -gt 0) {
    Write-Output ""
    Write-Output "发现内容："
    $findings | Sort-Object Severity, File, Line | ForEach-Object {
        $detail = $_.Detail
        if ($detail.Length -gt 100) { $detail = $detail.Substring(0, 100) + '…' }
        Write-Output ("[{0}] {1}:{2}  ({3})  {4}" -f $_.Severity, $_.File, $_.Line, $_.Kind, $detail)
    }
}

Write-Output ""
if ($high.Count -gt 0) {
    Write-Output ("结果: FAIL - 发现 " + $high.Count + " 项 HIGH 级别内容，处理后再发布。")
    exit 1
}
Write-Output ("结果: PASS - 无 HIGH 级别内容" + $(if ($warn.Count -gt 0) { "；" + $warn.Count + " 项 WARN 请人工复核" } else { "" }) + "。")
exit 0
