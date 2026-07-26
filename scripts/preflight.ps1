<#
.SYNOPSIS
  docs/24 R2:发布前三方对账 —— 本地工作树 / 线上服务端 / 线上更新公告。

.DESCRIPTION
  握手闸门是「精确相等」(ClientConnection ①②):协议版本、规则版本、卡表指纹任一不同,客户端就连不上。
  v0.8.6 的事故正是「客户端发了、服务端没跟」——而当时没有任何办法回答"线上到底跑的是什么"。
  本脚本把三方状态摆到一起比:

    本地   = game/HoldtheLine.Game.csproj 的 <Version> + `Sim --print-hash` 的协议/规则/卡表指纹
    服务端 = $Server/healthz 的 protocolVersion / rulesVersion / dataHash (docs/24 R1 起可用)
    公告   = $Server/version.json 的 latest,以及它与本地 deploy/vps/version.json 是否一致

  退出码 0 = 全绿;1 = 有 FAIL(缺步骤,别发)。WARN 不阻断(比如"客户端比公告新"正是发布前的正常态)。
  release.ps1 在 -Publish 前会强制调它。

.PARAMETER Server
  线上服务器基址(默认生产)。本地联调可传 http://127.0.0.1:5210。

.EXAMPLE
  pwsh scripts/preflight.ps1
  pwsh scripts/preflight.ps1 -Server http://127.0.0.1:5210
#>
[CmdletBinding()]
param(
    [string]$Server = 'https://htl.cicala.chat',
    [int]$TimeoutSec = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root        = Split-Path -Parent $PSScriptRoot
$Csproj      = Join-Path $Root 'game\HoldtheLine.Game.csproj'
$SimProj     = Join-Path $Root 'src\HoldTheLine.Sim'
$VersionJson = Join-Path $Root 'deploy\vps\version.json'
$Server      = $Server.TrimEnd('/')

function Info($m) { Write-Host "==> $m" -ForegroundColor Cyan }
$script:Fails = New-Object System.Collections.ArrayList
$script:Warns = New-Object System.Collections.ArrayList
function Ok($m)        { Write-Host "  [ OK ] $m" -ForegroundColor Green }
function Bad($m, $fix) { Write-Host "  [FAIL] $m" -ForegroundColor Red;    [void]$script:Fails.Add($fix) }
function Warn($m,$fix) { Write-Host "  [WARN] $m" -ForegroundColor Yellow; [void]$script:Warns.Add($fix) }

# WinPS 5.1 默认还在 SSL3/TLS1.0,连不上只收 TLS1.2+ 的站点
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}

# Set-StrictMode 下取不存在的属性会抛;老服务端(未上 R1)就是没有那几个字段,得能安静地拿到 $null
function Field($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($p) { return $p.Value } else { return $null }
}

function Short([string]$s) { if ($s -and $s.Length -gt 12) { $s.Substring(0, 12) + '…' } else { $s } }

# 返回 -1 / 0 / 1;非法版本号当作最小
function CompareVer([string]$a, [string]$b) {
    $pa = @(); $pb = @()
    if ($a -match '^\d+\.\d+\.\d+$') { $pa = $a.Split('.') | ForEach-Object { [int]$_ } } else { $pa = @(-1, 0, 0) }
    if ($b -match '^\d+\.\d+\.\d+$') { $pb = $b.Split('.') | ForEach-Object { [int]$_ } } else { $pb = @(-1, 0, 0) }
    for ($i = 0; $i -lt 3; $i++) {
        if ($pa[$i] -lt $pb[$i]) { return -1 }
        if ($pa[$i] -gt $pb[$i]) { return 1 }
    }
    return 0
}

# ---------- ① 本地工作树 ----------
[xml]$xml = Get-Content -Raw -LiteralPath $Csproj
$localClient = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $localClient) { throw "csproj 里没有 <Version>:$Csproj" }
$localClient = "$localClient".Trim()

Info "本地工作树(编译 Sim 取指纹,首次稍慢)…"
$simOut = & dotnet run --project $SimProj -c Release --verbosity quiet -- --print-hash 2>&1
if ($LASTEXITCODE -ne 0) { $simOut | Write-Host; throw "取本地指纹失败:dotnet run --project src/HoldTheLine.Sim -- --print-hash" }
$jsonLine = $simOut | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -First 1
if (-not $jsonLine) { $simOut | Write-Host; throw "--print-hash 没有输出 JSON(Sim 是否已含 docs/24 R2 改动?)" }
$local = $jsonLine | ConvertFrom-Json

# ---------- ② 线上服务端 ----------
Info "线上服务端 $Server/healthz …"
$live = $null
try {
    $live = Invoke-RestMethod -Uri "$Server/healthz" -TimeoutSec $TimeoutSec -UseBasicParsing
} catch {
    Bad "healthz 取不到:$($_.Exception.Message)" "确认服务端在跑:ssh <server> 'systemctl status holdtheline'"
}

# ---------- ③ 线上更新公告 ----------
Info "线上更新公告 $Server/version.json …"
$liveVj = $null
try {
    $liveVj = Invoke-RestMethod -Uri "$Server/version.json" -TimeoutSec $TimeoutSec -UseBasicParsing
} catch {
    Bad "version.json 取不到:$($_.Exception.Message)" "首次:服务器上 mkdir -p /var/www/htl,再跑 deploy.ps1 推公告"
}

# ---------- 比对:握手三元组 ----------
Write-Host ""
Write-Host ("  {0,-16} {1,-26} {2,-26}" -f '识别项', '本地工作树', '线上服务端') -ForegroundColor DarkGray
Write-Host ("  {0,-16} {1,-26} {2,-26}" -f '协议版本', $local.protocolVersion, (Field $live 'protocolVersion'))
Write-Host ("  {0,-16} {1,-26} {2,-26}" -f '规则版本', $local.rulesVersion,    (Field $live 'rulesVersion'))
Write-Host ("  {0,-16} {1,-26} {2,-26}" -f '卡表指纹', (Short $local.dataHash), (Short (Field $live 'dataHash')))
Write-Host ""

if ($live) {
    $liveRules = Field $live 'rulesVersion'
    $liveHash  = Field $live 'dataHash'
    $liveProto = Field $live 'protocolVersion'

    if ($null -eq $liveRules -or $null -eq $liveHash) {
        Bad "线上 healthz 不含 rulesVersion/dataHash —— 服务端还是 docs/24 R1 之前的构建" `
            "重部署服务端:pwsh deploy/vps/deploy.ps1"
    } else {
        $redeploy = "重部署服务端(带上当前 game/data):pwsh deploy/vps/deploy.ps1"
        if ($liveProto -ne $local.protocolVersion) {
            Bad "协议版本不一致(本地 $($local.protocolVersion) / 线上 $liveProto)—— 客户端会被拒连" $redeploy
        } else { Ok "协议版本一致($liveProto)" }

        if ($liveRules -ne $local.rulesVersion) {
            Bad "规则版本不一致(本地 $($local.rulesVersion) / 线上 $liveRules)—— 客户端会被拒连" $redeploy
        } else { Ok "规则版本一致($liveRules)" }

        if ($liveHash -ne $local.dataHash) {
            Bad "卡表指纹不一致 —— 两边卡表不是同一份,客户端会被拒连" $redeploy
        } else { Ok "卡表指纹一致($(Short $liveHash))" }
    }
}

# ---------- 比对:更新公告 ----------
if ($liveVj) {
    $liveLatest = Field $liveVj 'latest'
    $cmp = CompareVer $localClient $liveLatest
    if ($cmp -eq 0) {
        Ok "更新公告 latest = 本地客户端版本($localClient)"
    } elseif ($cmp -gt 0) {
        Warn "本地客户端 v$localClient 比线上公告 latest=$liveLatest 新(尚未发布,或已发布但公告没推)" `
             "发布后把公告推上去:pwsh deploy/vps/deploy.ps1 -OnlyVersionJson"
    } else {
        Bad "线上公告 latest=$liveLatest 比本地客户端 v$localClient 还新 —— 本地落后了,别在这个树上发版" `
            "先 git pull / 确认 csproj <Version> 是否忘了 bump"
    }

    # docs/25:应用内更新的直链是否跟上了 latest。对不上不阻断 —— 安卓包天然晚于 Release 一步(§7),
    # 客户端在这个窗口里会自动退回"跳转下载页";但发布收尾时忘了推这一步的话,手机端就一直是老路径。
    # 匹配 /v<latest>/ 这一段而不是裸子串:裸子串下 "0.8.1" 会匹配 ".../v0.8.10/...",恰好在这个检查
    # 唯一要抓的"公告改了一半"窗口里失效。latest 为空时 IndexOf("") 返回 0,同样会假绿,所以先挡掉。
    $apkUrl = Field $liveVj 'android_apk_url'
    if (-not $liveLatest) {
        Bad "线上公告没有 latest —— 客户端无从判断该不该更新" "检查 /var/www/htl/version.json 是否被写坏"
    } elseif (-not $apkUrl) {
        Warn "线上公告没有 android_apk_url —— 手机端只能跳下载页(应用内更新不生效)" `
             "出安卓包并写字段:pwsh scripts/build-android.ps1 -Upload,再 pwsh deploy/vps/deploy.ps1 -OnlyVersionJson"
    } elseif ("$apkUrl".IndexOf("/v$liveLatest/", [StringComparison]::Ordinal) -lt 0) {
        Warn "线上 android_apk_url 不含 latest=$liveLatest(还指着上一版的安卓包)" `
             "出安卓包并写字段:pwsh scripts/build-android.ps1 -Upload,再 pwsh deploy/vps/deploy.ps1 -OnlyVersionJson"
    } else {
        Ok "安卓直链跟上了 latest($liveLatest)"
    }

    # 本地那份 version.json 与线上是否同步(notes 改了没推,玩家看到的还是旧公告)
    if (Test-Path -LiteralPath $VersionJson) {
        $localVj = Get-Content -Raw -LiteralPath $VersionJson | ConvertFrom-Json
        $a = ($localVj | ConvertTo-Json -Depth 5 -Compress)
        $b = ($liveVj  | ConvertTo-Json -Depth 5 -Compress)
        if ($a -ne $b) {
            Warn "本地 deploy/vps/version.json 与线上不一致(本地 latest=$(Field $localVj 'latest') / 线上 latest=$liveLatest)" `
                 "推公告:pwsh deploy/vps/deploy.ps1 -OnlyVersionJson"
        } else { Ok "本地 version.json 与线上一致" }
    }
}

# ---------- 结论 ----------
Write-Host ""
if ($script:Fails.Count -eq 0 -and $script:Warns.Count -eq 0) {
    Write-Host "==> 三方一致,可以发布。" -ForegroundColor Green
    exit 0
}
foreach ($w in ($script:Warns | Select-Object -Unique)) { Write-Host "  · $w" -ForegroundColor Yellow }
if ($script:Fails.Count -eq 0) {
    Write-Host "==> 无阻断项(上面是提醒)。" -ForegroundColor Green
    exit 0
}
Write-Host "==> 有 $($script:Fails.Count) 项阻断,发布前请先:" -ForegroundColor Red
foreach ($f in ($script:Fails | Select-Object -Unique)) { Write-Host "  · $f" -ForegroundColor Red }
exit 1
