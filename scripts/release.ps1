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

.PARAMETER SkipTests
  跳过 dotnet test 闸(docs/24 R3)。规则引擎是客户端与服务端共享的,一个规则回归会同时污染两端,
  而已经打完的联机对局和已变动的天梯分数无法回滚 —— 只在纯美术/文案的紧急补发时用。

.PARAMETER SkipPreflight
  跳过 -Publish 前的三方对账(docs/24 R2)。默认会先跑 scripts/preflight.ps1,确认线上服务端的
  规则版本 / 卡表指纹与本地一致 —— 不一致就发客户端,玩家装上最新版反而连不上(v0.8.6 事故形态)。

.EXAMPLE
  pwsh scripts/release.ps1                 # 本地打包验证(不外推)
  pwsh scripts/release.ps1 -Publish        # 跑测试 + 三方对账 → 打包并发布到 GitHub Releases
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Godot = $(if ($env:GODOT) { $env:GODOT } else { 'D:\Program Files\Godot .NET\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' }),
    [switch]$Publish,
    [switch]$SkipExport,
    [switch]$SkipTests,
    [switch]$SkipPreflight
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

<#
docs/24 R4:从 CHANGELOG.md 抽当前版本段,一处写、两处用 —— version.json 的 notes(玩家在更新横幅里
看到的一句话)与 GitHub Release 的正文。此前这两处都靠发布当下手抄,是最容易忙中出错的时刻。
依赖 CHANGELOG 稳定的版本段格式:`## vX.Y.Z · 日期 · 主题`,紧跟一段 `> 引用块` 摘要。
#>
function Get-ChangelogSection([string]$ver) {
    $path = Join-Path $Root 'CHANGELOG.md'
    if (-not (Test-Path -LiteralPath $path)) { throw "找不到 CHANGELOG.md" }
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    $pattern = '^##\s+' + [regex]::Escape("v$ver") + '(\s|·|$)'

    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $pattern) { $start = $i; break } }
    if ($start -lt 0) { return $null }

    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^##\s') { $end = $i; break } }
    $heading = $lines[$start]
    $body = if ($end -gt $start + 1) { @($lines[($start + 1)..($end - 1)]) } else { @() }

    # 主题 = 标题里 "· 日期 ·" 之后的部分(主题本身可能还含 ·,所以取第 3 段起全部)
    $theme = ''
    $parts = $heading -split '·'
    if ($parts.Count -ge 3) { $theme = ($parts[2..($parts.Count - 1)] -join '·').Trim() }

    # 摘要 = 段首那一段引用块;notes 是纯文本(游戏内直接显示),所以去掉 ** 强调并压成一行
    $quote = @()
    foreach ($l in $body) {
        if ($l -match '^\s*>') { $quote += ($l -replace '^\s*>\s?', '') }
        elseif ($quote.Count -gt 0) { break }
    }
    $summary = ((($quote -join ' ') -replace '\*\*', '') -replace '\s+', ' ').Trim()

    $notes = if ($theme -and $summary) { "v$ver · ${theme}:$summary" }
             elseif ($summary)         { "v$ver · $summary" }
             else                      { "v$ver · $theme" }

    [pscustomobject]@{
        Theme = $theme
        Notes = $notes
        Body  = ((@($heading) + $body) -join "`n").TrimEnd()
    }
}

# ① 版本号:参数优先,否则从 csproj 读 <Version>
if (-not $Version) {
    [xml]$xml = Get-Content -Raw -LiteralPath $Csproj
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "csproj 里没有 <Version>,请先在 $Csproj 加上(docs/15 §1)。" }
}
$Version = "$Version".Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 X.Y.Z,当前:'$Version'" }
Info "发布版本 v$Version"

# ①a 更新日志(docs/24 R4)—— 早失败:找不到版本段说明忘了写 CHANGELOG,这本身就该在导出前拦下
$Changelog = Get-ChangelogSection $Version
if (-not $Changelog) {
    if ($Publish) { throw "CHANGELOG.md 里没有 `## v$Version · …` 版本段 —— 发版前先写更新日志(docs/24 R4)。" }
    Warn "CHANGELOG.md 里没有 v$Version 的版本段;本地打包继续,但 -Publish 会拒绝。"
} else {
    Info "更新日志:$($Changelog.Theme)"
}

# 前置工具检查
foreach ($t in @('vpk','dotnet')) {
    if (-not (Get-Command $t -ErrorAction SilentlyContinue)) {
        throw "缺少命令 '$t'。vpk 安装:dotnet tool install -g vpk --version 1.2.0"
    }
}

# ①b 测试闸(docs/24 R3)—— 排在导出之前:失败就别浪费那 5 分钟导出
if ($SkipTests) {
    Warn "已跳过 dotnet test(-SkipTests)—— 只该在纯美术/文案的紧急补发时这么做。"
} else {
    Info "跑测试(Rules + Server)…"
    foreach ($proj in @('src\HoldTheLine.Rules.Tests', 'src\HoldTheLine.Server.Tests')) {
        dotnet test (Join-Path $Root $proj) -c Release --verbosity quiet --nologo
        if ($LASTEXITCODE -ne 0) { throw "$proj 测试未通过 —— 已中止发布(要强发用 -SkipTests)。" }
    }
    Info "测试全绿"
}

# ①c 三方对账(docs/24 R2)—— 只在真要外推时做(本地 pack 不需要联网)
if ($Publish) {
    if ($SkipPreflight) {
        Warn "已跳过发布前三方对账(-SkipPreflight)—— 若线上服务端的规则/卡表与本包不一致,玩家更新后会连不上。"
    } else {
        Info "发布前三方对账(本地 / 线上服务端 / 线上公告)…"
        & (Join-Path $PSScriptRoot 'preflight.ps1')
        if ($LASTEXITCODE -ne 0) { throw "三方对账未通过 —— 已中止发布(按上面的提示补齐步骤;确认无碍再用 -SkipPreflight)。" }
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

    # 正文只能事后补:vpk 1.2.0 的 upload github 没有 --releaseNotes(已核 --help),它只上传产物。
    # 失败不回滚发布(包已经在了,正文差一点不值得中止),但要吵得够响。
    if ($Changelog) {
        $notesFile = Join-Path $env:TEMP "htl-release-notes-$Version.md"
        [System.IO.File]::WriteAllText($notesFile, $Changelog.Body, (New-Object System.Text.UTF8Encoding($false)))
        gh release edit "v$Version" --repo $RepoUrl --notes-file $notesFile
        if ($LASTEXITCODE -ne 0) { Warn "写 Release 正文失败,请手动贴:$notesFile" }
        else { Info "已写入 Release 正文(取自 CHANGELOG v$Version)"; Remove-Item $notesFile -ErrorAction SilentlyContinue }
    }
} else {
    Warn "未加 -Publish:仅本地打包,未推 GitHub Releases。"
}

# ⑤ (二期)itch.io / butler —— 公开测试时取消注释。推的是②的裸导出目录,绝不含 Setup.exe/nupkg。
# butler push $ExportDir <itch用户>/hold-the-line:windows --userversion $Version

# ⑥ 改写本地 version.json 的 latest + notes(仅 -Publish —— 本地测试 pack 不动它,否则未发布的版本号
#    一旦被推上服务器,全体客户端会弹一个 GitHub 上并不存在的"新版本")。
#    上传不在这里做:公告是服务端侧资产,由 deploy.ps1 负责推(docs/24 R5),这样"对外宣布最新版"永远
#    发生在"服务端确实能接待这个版本"之后。
if ($Publish -and (Test-Path -LiteralPath $VersionJson)) {
    $vj = Get-Content -Raw -LiteralPath $VersionJson | ConvertFrom-Json
    $vj.latest = $Version
    if ($Changelog) { $vj.notes = $Changelog.Notes }   # docs/24 R4:不再手抄
    # WriteAllText with a no-BOM UTF-8 so version.json stays clean JSON on both PS 7 and WinPS 5.1
    # (5.1's `Set-Content -Encoding utf8` would prepend a BOM).
    [System.IO.File]::WriteAllText($VersionJson, ($vj | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
    Info "已把 version.json 的 latest 更新为 $Version(notes 取自 CHANGELOG)"
    Warn "最后一步 —— 把公告推上去(玩家的更新提示要靠它):  pwsh deploy/vps/deploy.ps1 -OnlyVersionJson"
}

Info "完成。产物在 $RelDir(Setup.exe / *.nupkg / releases.win.json)。"
