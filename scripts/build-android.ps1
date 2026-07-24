<#
.SYNOPSIS
  docs/19 C2:出安卓内测 APK。剥离 MCP 开发 autoload + 版本号盖章 + 导出(debug keystore 签名,侧载够用)。

.DESCRIPTION
  版本号读 game/HoldtheLine.Game.csproj 的 <Version>(与 release.ps1 同源)。默认 --export-release
  (更干净:无 DevCheats / 无卡面编辑按钮 / 体积更小);加 -Debug 用 --export-debug 兜底。
  产物:build/android/HoldTheLine-vX.Y.Z-android-arm64.apk。
  前置:JDK17 + Android SDK 已装(见记忆 holdtheline-mobile-port);.NET Android 导出走 gradle 构建(net8.0)。

.EXAMPLE
  pwsh scripts/build-android.ps1            # release 包
  pwsh scripts/build-android.ps1 -DebugBuild  # debug 包(兜底)
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Godot = $(if ($env:GODOT) { $env:GODOT } else { 'D:\Program Files\Godot .NET\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' }),
    [switch]$DebugBuild   # 用 --export-debug 兜底(-Debug 是 PowerShell 保留公共参数,勿用)
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root    = Split-Path -Parent $PSScriptRoot
$Game    = Join-Path $Root 'game'
$Proj    = Join-Path $Game 'project.godot'
$Presets = Join-Path $Game 'export_presets.cfg'
$Csproj  = Join-Path $Game 'HoldtheLine.Game.csproj'
$OutDir  = Join-Path $Root 'build\android'
$Jdk     = 'C:\Users\Cicala\AndroidTooling\jdk17\jdk-17.0.20+8'
$Sdk     = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ① 版本号(参数优先,否则读 csproj)
if (-not $Version) {
    [xml]$xml = Get-Content -Raw -LiteralPath $Csproj
    $Version = (($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 X.Y.Z,当前:'$Version'" }
$vp   = $Version.Split('.')
$Code = [int]$vp[0] * 10000 + [int]$vp[1] * 100 + [int]$vp[2]
Info "打包 v$Version(versionCode $Code)"

# ② 工具链环境
if (-not (Test-Path -LiteralPath $Godot)) { throw "找不到 Godot(.NET 版):$Godot" }
if (-not (Test-Path -LiteralPath $Jdk))   { throw "找不到 JDK17:$Jdk" }
$env:JAVA_HOME = $Jdk
$env:PATH = "$Jdk\bin;$env:PATH"
$env:ANDROID_HOME = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$Keystore = (Join-Path $env:USERPROFILE '.android\debug.keystore') -replace '\\', '/'

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$Apk   = Join-Path $OutDir 'HoldTheLine.apk'
$Final = Join-Path $OutDir "HoldTheLine-v$Version-android-arm64.apk"
$Log   = Join-Path $OutDir 'export.log'
$Mode  = if ($DebugBuild) { '--export-debug' } else { '--export-release' }

# ③ 临时改 project.godot / export_presets.cfg(finally 里按原文还原,与 git 状态无关)
$projOrig    = Get-Content -Raw -LiteralPath $Proj
$presetsOrig = Get-Content -Raw -LiteralPath $Presets
$utf8 = New-Object System.Text.UTF8Encoding($false)
$ec = -1
try {
    # 剥离 MCP 开发 autoload(与 release.ps1 同一正则;UpdateService 走 res://scripts/ 不匹配、保留)
    $projStripped = [regex]::Replace($projOrig, '(?m)^(\s*)(\w+="\*res://addons/godot_mcp/[^"]*")', '$1; $2')
    [System.IO.File]::WriteAllText($Proj, $projStripped, $utf8)
    # 往 [preset.1.options] 盖版本号 + release keystore(用 debug key 签,内测侧载够用)
    $inject = @(
        "version/code=$Code"
        "version/name=`"$Version`""
        "keystore/release=`"$Keystore`""
        "keystore/release_user=`"androiddebugkey`""
        "keystore/release_password=`"android`""
    ) -join "`n"
    $presetsNew = [regex]::Replace($presetsOrig, '(?m)^\[preset\.1\.options\]\r?$', "[preset.1.options]`n$inject")
    [System.IO.File]::WriteAllText($Presets, $presetsNew, $utf8)

    Info "Godot $Mode 导出中(首次 gradle 会慢)…"
    if (Test-Path -LiteralPath $Apk) { Remove-Item -Force -LiteralPath $Apk }
    & $Godot --headless --path "$Game/" $Mode 'Android' $Apk *>&1 | Tee-Object -FilePath $Log | Out-Null
    $ec = $LASTEXITCODE
}
finally {
    [System.IO.File]::WriteAllText($Proj, $projOrig, $utf8)
    [System.IO.File]::WriteAllText($Presets, $presetsOrig, $utf8)
    Info "已还原 project.godot + export_presets.cfg"
}

if ($ec -ne 0 -or -not (Test-Path -LiteralPath $Apk)) {
    Get-Content -Tail 30 -LiteralPath $Log -ErrorAction SilentlyContinue | Write-Host
    throw "导出失败(ExitCode=$ec)。日志:$Log"
}
Move-Item -Force -LiteralPath $Apk -Destination $Final
Info ("完成 → $Final (" + [math]::Round((Get-Item $Final).Length / 1MB, 1) + " MB)")
