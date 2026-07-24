<#
.SYNOPSIS
  docs/15 通道 A 发布脚本:Godot 导出 → Velopack 打包 →(可选)推 GitHub Releases → 更新 version.json。

.DESCRIPTION
  版本号单一来源 = game/HoldtheLine.Game.csproj 的 <Version>(docs/15 §1),脚本读它驱动
  vpk / git tag / version.json,人工只改那一处。默认只做本地 pack(安全);加 -Publish 才会
  推 GitHub Releases(对外、不可逆——脚本会先打印将要执行的动作)。

  两条分发通道是平行关系(docs/15 §0):
    A(本脚本)Velopack + GitHub Releases —— 现在就做。
    B itch.io / butler          —— 二期(公开测试)才启用,见文末被注释的 ⑤。

.PARAMETER Version
  覆盖版本号(默认读 csproj)。仅用于补发/测试;正常发布请改 csproj 后不带此参数。

.PARAMETER Godot
  Godot 可执行文件路径(带超时看护,导出 wrapper 有挂十几分钟的前科,docs/15 §4)。默认取
  $env:GODOT,否则用本机 .NET(mono)版 Godot。**必须是 .NET/mono 版**:标准版 Godot 无法加载 C#
  脚本(报 "No loader found for .cs")、且找的是 export_templates/<ver>/ 而非 <ver>.mono/,导出必败
  (docs/15 §4 标准版陷阱)。导出日志落盘到 build/ 再在失败时回显。

.PARAMETER Publish
  开关:向 GitHub Releases 上传并发布(用 gh 已登录的 token)。不加则只本地 pack。

.PARAMETER SkipExport
  跳过 Godot 导出,直接用 build/windows 里已有的产物打包(调试脚本用)。

.EXAMPLE
  pwsh scripts/release.ps1                 # 本地打包验证(不外推)
  pwsh scripts/release.ps1 -Publish        # 打包并发布到 GitHub Releases
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Godot = $(if ($env:GODOT) { $env:GODOT } else { 'D:\Program Files\Godot .NET\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' }),
    [switch]$Publish,
    [switch]$SkipExport
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 仓库布局(脚本在 scripts/ 下)
$Root      = Split-Path -Parent $PSScriptRoot
$GameDir   = Join-Path $Root 'game'
$Csproj    = Join-Path $GameDir 'HoldtheLine.Game.csproj'
$ExportDir = Join-Path $Root 'build\windows'
$RelDir    = Join-Path $Root 'build\releases'
$ExeName   = 'HoldTheLine.exe'          # export_presets.cfg 的导出名
$PackId    = 'HoldTheLine'              # Velopack 包 id(--packId / -u)
$Preset    = 'Windows Desktop'          # export_presets.cfg 的 preset 名
$RepoUrl   = 'https://github.com/Shikasar7/HoldTheLine'
$VersionJson = Join-Path $Root 'deploy\vps\version.json'

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }

# ① 版本号:参数优先,否则从 csproj 读 <Version>
if (-not $Version) {
    [xml]$xml = Get-Content -Raw -LiteralPath $Csproj
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "csproj 里没有 <Version>,请先在 $Csproj 加上(docs/15 §1)。" }
}
$Version = "$Version".Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 X.Y.Z,当前:'$Version'" }
Info "发布版本 v$Version"

# 前置工具检查
foreach ($t in @('vpk','dotnet')) {
    if (-not (Get-Command $t -ErrorAction SilentlyContinue)) {
        throw "缺少命令 '$t'。vpk 安装:dotnet tool install -g vpk --version 1.2.0"
    }
}

# ② Godot headless 导出 → build/windows(超时看护 + 日志落盘)
if ($SkipExport) {
    Warn "跳过导出,直接用 $ExportDir 现有产物"
} else {
    if (-not (Test-Path -LiteralPath $Godot)) {
        throw "找不到 Godot:$Godot(用 -Godot 指定,或设 `$env:GODOT)"
    }
    # 发布前临时剥离 MCP 开发用 autoload(godot_mcp 的截图/输入/inspector 服务;文件轮询 IPC,不开端口,
    # 但 inspector 能从 user:// 请求文件执行 GDScript,属开发工具,不该随包发给玩家)。仅导出期间注释,
    # finally 里按原文还原(与 git 状态无关,即使用户有未提交改动也安全)。
    $ProjectGodot   = Join-Path $GameDir 'project.godot'
    $godotProjOrig  = Get-Content -Raw -LiteralPath $ProjectGodot
    $stripped       = [regex]::Replace($godotProjOrig, '(?m)^(\s*)(\w+="\*res://addons/godot_mcp/[^"]*")', '$1; $2')
    [System.IO.File]::WriteAllText($ProjectGodot, $stripped, (New-Object System.Text.UTF8Encoding($false)))
    Info "已临时注释 MCP autoload(发布包不含开发工具,导出后自动还原)"
    try {
        Info "Godot 导出中(超时 5 分钟看护)…"
        if (Test-Path -LiteralPath $ExportDir) { Remove-Item -Recurse -Force -LiteralPath $ExportDir }
        New-Item -ItemType Directory -Force -Path $ExportDir | Out-Null
        $exeOut = Join-Path $ExportDir $ExeName

        $godotLog = Join-Path $Root 'build\godot-export.log'
        # 用单个带引号的参数串:Start-Process 对数组元素不会自动加引号,预设名 "Windows Desktop"
        # 含空格会被拆成两个参数(Godot 报 Invalid export preset name: Windows)。路径一并加引号防空格。
        $godotArgs = "--headless --path `"$GameDir`" --export-release `"$Preset`" `"$exeOut`""
        $proc = Start-Process -FilePath $Godot -ArgumentList $godotArgs -PassThru -NoNewWindow `
            -RedirectStandardOutput $godotLog -RedirectStandardError "$godotLog.err"
        if (-not $proc.WaitForExit(300000)) {
            try { $proc.Kill() } catch {}
            throw "Godot 导出超时(>5min)—— 多半是导出 wrapper 挂住,重跑一次(docs/15 §4 排雷)。日志:$godotLog"
        }
        if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exeOut)) {
            Get-Content -Tail 30 -LiteralPath $godotLog -ErrorAction SilentlyContinue | Write-Host
            Get-Content -Tail 30 -LiteralPath "$godotLog.err" -ErrorAction SilentlyContinue | Write-Host
            throw "Godot 导出失败(ExitCode=$($proc.ExitCode))。日志:$godotLog"
        }
        Info "导出完成:$exeOut"
    } finally {
        [System.IO.File]::WriteAllText($ProjectGodot, $godotProjOrig, (New-Object System.Text.UTF8Encoding($false)))
        Info "已还原 project.godot 的 MCP autoload"
    }
}

# ③ vpk pack —— 产出 Setup.exe + 完整包 + 与上一版的 delta + releases.win.json
New-Item -ItemType Directory -Force -Path $RelDir | Out-Null
Info "vpk pack v$Version …"
vpk pack --packId $PackId --packVersion $Version --packDir $ExportDir --mainExe $ExeName --outputDir $RelDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败" }
Info "打包完成 → $RelDir"

# ④ vpk upload github(仅 -Publish;对外、不可逆)
if ($Publish) {
    $token = (gh auth token 2>$null)
    if (-not $token) { throw "gh 未登录(gh auth login)——上传需要它的 token。" }
    Warn "将向 GitHub Releases 发布 v$Version(tag v$Version,仓库 $RepoUrl)——此操作对外可见。"
    vpk upload github --repoUrl $RepoUrl --publish --releaseName "v$Version" --tag "v$Version" --token $token --outputDir $RelDir
    if ($LASTEXITCODE -ne 0) { throw "vpk upload 失败" }
    Info "已发布 v$Version 到 GitHub Releases"
} else {
    Warn "未加 -Publish:仅本地打包,未推 GitHub Releases。"
}

# ⑤ (二期)itch.io / butler —— 公开测试时取消注释。推的是②的裸导出目录,绝不含 Setup.exe/nupkg。
# butler push $ExportDir <itch用户>/hold-the-line:windows --userversion $Version

# ⑥ 更新 version.json 的 latest(仅 -Publish —— 本地测试 pack 不动它,否则未发布的版本号一旦被
#    scp 上服务器,全体客户端会弹一个 GitHub 上并不存在的"新版本")
if ($Publish -and (Test-Path -LiteralPath $VersionJson)) {
    $vj = Get-Content -Raw -LiteralPath $VersionJson | ConvertFrom-Json
    $vj.latest = $Version
    # WriteAllText with a no-BOM UTF-8 so version.json stays clean JSON on both PS 7 and WinPS 5.1
    # (5.1's `Set-Content -Encoding utf8` would prepend a BOM).
    [System.IO.File]::WriteAllText($VersionJson, ($vj | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Info "已把 version.json 的 latest 更新为 $Version"
    Warn "记得把 version.json 推到服务器:  scp `"$VersionJson`" <user>@htl.cicala.chat:/var/www/htl/version.json"
}

Info "完成。产物在 $RelDir(Setup.exe / *.nupkg / releases.win.json)。"
