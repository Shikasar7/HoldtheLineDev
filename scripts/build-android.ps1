<#
.SYNOPSIS
  docs/19 C2:出安卓 APK。剥离 MCP 开发 autoload + 版本号盖章 + 正式 keystore 签名 + 导出。

.DESCRIPTION
  版本号读 game/HoldtheLine.Game.csproj 的 <Version>(与 release.ps1 同源)。默认 --export-release
  (更干净:无 DevCheats / 无卡面编辑按钮 / 体积更小);加 -DebugBuild 用 --export-debug 兜底。
  产物:build/android/HoldTheLine-vX.Y.Z-android-arm64.apk。
  前置:JDK17 + Android SDK 已装(见记忆 holdtheline-mobile-port);.NET Android 导出走 gradle 构建(net8.0)。

  签名(docs/24 R6):默认用**正式 keystore**,路径/别名/口令走环境变量,口令绝不落在仓库里。
  Android 不允许签名不同的包覆盖安装 —— 换签名就意味着存量安装必须"卸载重装",而安卓卸载会清空
  应用数据。所以签名一旦定下就不能再改,且越早定代价越小。缺环境变量时本脚本**报错中止**,绝不
  静默回落到 debug key(那会让人以为签的是正式名,等发现时装机量已经上来了)。

  一次性准备已于 2026-07-25 完成(密钥 %USERPROFILE%\AndroidTooling\HoldTheLine-release.jks,别名 holdtheline)。
  换机器/重建时的步骤(在交互式终端跑,口令当场输入,不要写进任何脚本):
    & "C:\Users\Cicala\AndroidTooling\jdk17\jdk-17.0.20+8\bin\keytool.exe" -genkeypair -v `
        -keyalg RSA -keysize 2048 -validity 10000 `
        -keystore "$env:USERPROFILE\AndroidTooling\HoldTheLine-release.jks" -alias holdtheline
    [Environment]::SetEnvironmentVariable('HTL_KEYSTORE_PASS', '口令本身', 'User')   # 设完重开终端
  两个坑:
    · keytool 最后问"输入 <holdtheline> 的密钥口令(相同则按回车)"时**必须直接回车** —— Godot 的
      Android 导出只有一个密码字段,密钥口令与密钥库口令不同就签不出来,且报错不说明原因。
    · keytool 全路径写死是因为本机 JAVA_HOME 只在本脚本运行期间才设,新终端里是空的。
  然后**把这个 .jks 和口令备份好**:它丢了就再也发不出能覆盖升级现有安装的包了。

.PARAMETER UseDebugKey
  显式改用 debug keystore(仅本机调试侧载)。这样签的包与正式包互不覆盖,装之前要先卸载。

.EXAMPLE
  pwsh scripts/build-android.ps1              # 正式签名的 release 包
  pwsh scripts/build-android.ps1 -UseDebugKey # 本机调试包
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Godot = $(if ($env:GODOT) { $env:GODOT } else { 'D:\Program Files\Godot .NET\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' }),
    [string]$Keystore = $(if ($env:HTL_KEYSTORE) { $env:HTL_KEYSTORE } else { Join-Path $env:USERPROFILE 'AndroidTooling\HoldTheLine-release.jks' }),
    [string]$KeystoreAlias = $(if ($env:HTL_KEYSTORE_ALIAS) { $env:HTL_KEYSTORE_ALIAS } else { 'holdtheline' }),
    [switch]$UseDebugKey,
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
function Warn($m) { Write-Host "!!  $m" -ForegroundColor Yellow }

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
# ②b 签名密钥(docs/24 R6)。Godot 的 cfg 字符串是 C 风格转义,口令里的 \ 和 " 必须转义,否则
#     会把这一行截断成语法错误 —— 而错误发生在被 finally 还原掉的临时文件里,极难排查。
function CfgEscape([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }

if ($UseDebugKey) {
    Warn "用 debug keystore 签名 —— 只适合本机调试。与正式签名的包互不覆盖,装之前要先卸载(会清存档)。"
    $KeyPath  = (Join-Path $env:USERPROFILE '.android\debug.keystore') -replace '\\', '/'
    $KeyAlias = 'androiddebugkey'
    $KeyPass  = 'android'
} else {
    $howto = @"
准备步骤(在交互式终端跑,口令当场输入,别写进任何脚本):
  & "C:\Users\Cicala\AndroidTooling\jdk17\jdk-17.0.20+8\bin\keytool.exe" -genkeypair -v ``
      -keyalg RSA -keysize 2048 -validity 10000 -keystore "$Keystore" -alias $KeystoreAlias
      (最后问"密钥口令(相同则按回车)"时必须直接回车 —— Godot 只有一个密码字段)
  [Environment]::SetEnvironmentVariable('HTL_KEYSTORE_PASS', '口令本身', 'User')   # 设完重开终端
校验环境变量与密钥对得上(不回显口令):
  `$v=[Environment]::GetEnvironmentVariable('HTL_KEYSTORE_PASS','User'); ``
  & "C:\Users\Cicala\AndroidTooling\jdk17\jdk-17.0.20+8\bin\keytool.exe" -list -keystore "$Keystore" -storepass `$v
生成后务必备份 .jks 和口令 —— 丢了就再也发不出能覆盖升级现有安装的包。
只想出个本机调试包?加 -UseDebugKey。
"@
    # 提示单独 Write-Host:PowerShell 的异常格式化会把多行 throw 消息挤成一坨,反而看不清步骤
    if (-not (Test-Path -LiteralPath $Keystore)) {
        Write-Host $howto -ForegroundColor Yellow
        throw "找不到正式 keystore:$Keystore(准备步骤见上)"
    }
    $KeyPass = $env:HTL_KEYSTORE_PASS
    if ([string]::IsNullOrEmpty($KeyPass)) {
        Write-Host $howto -ForegroundColor Yellow
        throw "环境变量 HTL_KEYSTORE_PASS 未设 —— 口令不写进脚本/仓库(准备步骤见上)"
    }
    $KeyPath  = $Keystore -replace '\\', '/'
    $KeyAlias = $KeystoreAlias
    Info "正式签名:$KeyPath(别名 $KeyAlias)"
}

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
    # 往 [preset.1.options] 盖版本号 + 签名密钥。口令只在导出期间落在 export_presets.cfg 里,
    # finally 按原文还原(与 git 状态无关);build/ 已在 .gitignore,导出日志也不出仓库。
    $inject = @(
        "version/code=$Code"
        "version/name=`"$Version`""
        "keystore/release=`"$(CfgEscape $KeyPath)`""
        "keystore/release_user=`"$(CfgEscape $KeyAlias)`""
        "keystore/release_password=`"$(CfgEscape $KeyPass)`""
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
