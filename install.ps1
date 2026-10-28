# 把 DSH Unity Bridge 装进任意 Unity 工程：往目标工程的 Packages/manifest.json 写一条 file: 依赖。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File dsh-unity-mcp/install.ps1 -ProjectPath "E:\path\to\UnityProject"
#   powershell ... -File dsh-unity-mcp/install.ps1 -ProjectPath "..." -Undo     # 撤销
#
# 说明：
#   - 包源码始终只有一份（本文件夹的 unity-package/），目标工程只是引用它。
#   - 同一盘符写相对路径，跨盘符退回绝对路径（Unity 两者都认）。
#   - 改前自动备份 manifest.json.bak。
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,
    [string]$PackageId = 'com.dsh.unity-bridge',
    [string]$PackagePath,
    [switch]$Undo
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot 在 param 默认值阶段可能是空的（powershell -File 下实测），所以放到脚本体内取
if (-not $PackagePath) { $PackagePath = Join-Path $PSScriptRoot 'unity-package' }

function Get-PackageValue {
    param([string]$FromDir, [string]$ToPath)
    $fromRoot = [System.IO.Path]::GetPathRoot((Resolve-Path $FromDir).Path)
    $toRoot = [System.IO.Path]::GetPathRoot((Resolve-Path $ToPath).Path)
    if ($fromRoot -ne $toRoot) {
        # 跨盘符没有相对路径，用绝对路径（Unity 接受）
        return ((Resolve-Path $ToPath).Path -replace '\\', '/')
    }
    $fromUri = New-Object System.Uri(((Resolve-Path $FromDir).Path.TrimEnd('\')) + '\')
    $toUri = New-Object System.Uri((Resolve-Path $ToPath).Path)
    $rel = $fromUri.MakeRelativeUri($toUri).ToString()
    return ([System.Uri]::UnescapeDataString($rel) -replace '\\', '/')
}

# ---------- 前置检查 ----------
if (-not (Test-Path $ProjectPath)) { throw "工程目录不存在：$ProjectPath" }
$ProjectPath = (Resolve-Path $ProjectPath).Path
$packagesDir = Join-Path $ProjectPath 'Packages'
$manifest = Join-Path $packagesDir 'manifest.json'
if (-not (Test-Path $manifest)) { throw "找不到 $manifest —— 这个路径看起来不是 Unity 工程的根目录" }
if (-not (Test-Path (Join-Path $PackagePath 'package.json'))) { throw "找不到包描述：$PackagePath\package.json" }

$value = 'file:' + (Get-PackageValue -FromDir $packagesDir -ToPath $PackagePath)
$text = [System.IO.File]::ReadAllText($manifest)
$pattern = '(?m)^\s*"' + [regex]::Escape($PackageId) + '"\s*:\s*"[^"]*"\s*,?\s*$'

# ---------- 撤销 ----------
if ($Undo) {
    if ($text -notmatch $pattern) {
        Write-Host "[跳过] manifest.json 里没有 $PackageId" -ForegroundColor Yellow
        return
    }
    $new = [regex]::Replace($text, $pattern + '\r?\n', '')
    Copy-Item $manifest "$manifest.bak" -Force
    [System.IO.File]::WriteAllText($manifest, $new, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[完成] 已从 manifest.json 移除 $PackageId（备份：manifest.json.bak）" -ForegroundColor Green
    return
}

# ---------- 安装 ----------
if ($text -match $pattern) {
    if ($text -match [regex]::Escape($value)) {
        Write-Host "[跳过] 已经指向 $value" -ForegroundColor Yellow
        return
    }
    $new = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator] {
            param($m)
            '    "' + $PackageId + '": "' + $value + '",'
        })
}
else {
    if ($text -notmatch '"dependencies"\s*:\s*\{') { throw 'manifest.json 里找不到 "dependencies" 段' }
    $new = [regex]::Replace($text, '("dependencies"\s*:\s*\{)', {
            param($m)
            $m.Groups[1].Value + "`r`n    `"$PackageId`": `"$value`","
        })
}

# 先确认结果仍是合法 JSON，再落盘
$null = $new | ConvertFrom-Json

$backup = "$manifest.bak"
if (Test-Path $backup) { $backup = "$manifest.bak-" + (Get-Date -Format 'yyyyMMdd_HHmmss') }
Copy-Item $manifest $backup -Force
[System.IO.File]::WriteAllText($manifest, $new, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "[完成] 已写入 file: 依赖" -ForegroundColor Green
Write-Host "  manifest : $manifest"
Write-Host "  $PackageId -> $value"
Write-Host "  备份     : $backup"
Write-Host ''
Write-Host '下一步：用 Unity 打开该工程等包导入完成；菜单 Tools → DSH Unity Bridge 看桥是否在跑。'
