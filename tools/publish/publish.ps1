<#
.SYNOPSIS
    构建并生成 Steam 创意工坊发布包（可选直接上传）。

.DESCRIPTION
    全流程：
      1. Release 构建，并把安装目标重定向到 .publish\stage（不碰游戏目录）
      2. 校验发布包内容：manifest.xml / dll / preview.png / Localization / Data
      3. 按 manifest 里的名称、描述、版本生成 .vdf
      4. 加 -Upload 时调用 steamcmd 上传；首次上传后用写回的 publishedfileid 更新已有物品

    发布包里的东西就是「游戏运行必需」的那几样，不含 .git / bin / obj / 源码 / 文档。

    默认输出：<我的文档>\ProbablyEnoughItems-publish\
                ├── stage\          ← 上传内容（contentfolder）
                └── workshop.vdf    ← 物品元数据，发布成功后会被写回物品 ID

    用 -StageDir 可改到别处，但请保持纯 ASCII 路径（steamcmd 对非 ASCII 的 contentfolder 支持不佳）。

.PARAMETER PublishedFileId
    0 = 新建物品；填已有 ID = 更新该物品。上传成功后 steamcmd 会把新 ID 写回 vdf，脚本也会打印出来。

.PARAMETER Visibility
    0 公开 / 1 仅好友 / 2 隐藏。首次发布建议先用 2 验证。

.EXAMPLE
    .\publish.ps1
    只生成发布包与 vdf，不上传。

.EXAMPLE
    .\publish.ps1 -Upload -Username myaccount -Visibility 2
    隐藏上传，用于自测。

.EXAMPLE
    .\publish.ps1 -Upload -Username myaccount -PublishedFileId 3829100000 -ChangeNote "fix sources"
    更新已发布的物品。
#>
param(
    [string]$SteamCmd        = "",
    [string]$GameDir         = "",
    [string]$StageDir        = "",
    [string]$PublishedFileId = "0",
    [ValidateSet("0", "1", "2")]
    [string]$Visibility      = "2",
    [string]$Title           = "",
    [string]$Description     = "",
    [string]$ChangeNote      = "",
    [string]$Username        = "",
    [switch]$SkipBuild,
    [switch]$Upload
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)

$repo   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tools\publish -> 仓库根
$proj   = Join-Path $repo "ProbablyEnoughItems.csproj"
$manifestPath = Join-Path $repo "manifest.xml"

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }
if (-not (Test-Path $manifestPath)) { throw "找不到 manifest：$manifestPath" }

# steamcmd：-SteamCmd > 环境变量 STEAMCMD > PATH 里的 steamcmd。
# 只有 -Upload 时才要求它能解析出来，仓库里不写死安装路径。
if (-not $SteamCmd) { $SteamCmd = $env:STEAMCMD }
if (-not $SteamCmd) {
    $cmd = Get-Command steamcmd -ErrorAction SilentlyContinue
    if ($cmd) { $SteamCmd = $cmd.Source }
}

# 游戏目录：-GameDir > 环境变量 PROBABLY_STOLEN_DIR > Steam 注册表。
if (-not $GameDir) { $GameDir = $env:PROBABLY_STOLEN_DIR }
if (-not $GameDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if ($steam) { $GameDir = Join-Path ($steam -replace '/', '\') 'steamapps\common\Probably Stolen' }
}
if (-not $GameDir) { throw "找不到游戏目录：用 -GameDir <路径>，或设置环境变量 PROBABLY_STOLEN_DIR" }

# 默认放到「文档」下的纯 ASCII 路径：steamcmd 对含中文/空格的 contentfolder 容易出问题，
# 而仓库目录本身叫「深空当铺mod来源显示」，所以发布包不能留在仓库里。
if ([string]::IsNullOrWhiteSpace($StageDir)) {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    $StageDir = Join-Path $docs "ProbablyEnoughItems-publish\stage"
}
$StageDir = [System.IO.Path]::GetFullPath($StageDir)
$publishDir = Split-Path -Parent $StageDir
$vdfPath = Join-Path $publishDir "workshop.vdf"

# —— 读清单 ——
$manifest = [xml][System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
$modId     = $manifest.Manifest.ID
$modName   = $manifest.Manifest.Name
$modVer    = $manifest.Manifest.ModVersion
$gameVers  = @($manifest.Manifest.GameVersions.Version) -join ", "
if ([string]::IsNullOrWhiteSpace($Title))       { $Title = $modName }
if ([string]::IsNullOrWhiteSpace($Description)) { $Description = $manifest.Manifest.Description }
if ([string]::IsNullOrWhiteSpace($ChangeNote))  { $ChangeNote = "v$modVer" }

$gameMods = Join-Path $GameDir "Mods"
if ($StageDir.TrimEnd('\') -eq (Join-Path $gameMods "ProbablyEnoughItems").TrimEnd('\')) {
    throw "StageDir 不能指向游戏里的运行目录，会污染实机安装"
}

Write-Host "== 发布包 =="
Write-Host " 模组      : $modName ($modId) v$modVer"
Write-Host " 游戏版本  : $gameVers"
Write-Host " 发布目录  : $StageDir"
Write-Host " 可见性    : $Visibility  (0 公开 / 1 好友 / 2 隐藏)"
Write-Host ""

# —— 1. 构建到 stage ——
if (-not $SkipBuild) {
    if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

    # 用 -p:ModDir 把安装目标重定向到 stage：走的是同一套 Copy 逻辑，但不会写进游戏目录
    & dotnet build $proj -c Release --nologo "-p:GameDir=$GameDir" "-p:ModDir=$StageDir"
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
}

# —— 2. 校验 ——
$required = @("manifest.xml", "ProbablyEnoughItems.dll", "preview.png")
$missing = @()
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $StageDir $name))) { $missing += $name }
}
$locFiles   = @(Get-ChildItem (Join-Path $StageDir "Localization") -Filter *.csv -ErrorAction SilentlyContinue)
$dataFiles  = @(Get-ChildItem (Join-Path $StageDir "Data") -Filter *.csv -ErrorAction SilentlyContinue)
if ($locFiles.Count  -eq 0) { $missing += "Localization\*.csv" }
if ($dataFiles.Count -eq 0) { $missing += "Data\*.csv" }
if ($missing.Count -gt 0) { throw ("发布包缺少：" + ($missing -join ", ")) }

# 扫描器会拒绝文件夹里未列进 <Assemblies> 的 dll，以及脚本/可执行文件
$declared = @($manifest.Manifest.Assemblies.Assembly)
$extraDll = @(Get-ChildItem $StageDir -Recurse -File -Include *.dll |
    Where-Object { $declared -notcontains $_.Name })
if ($extraDll.Count -gt 0) {
    throw ("发布包里有未声明的 dll：" + (($extraDll | ForEach-Object { $_.Name }) -join ", "))
}
$badFiles = @(Get-ChildItem $StageDir -Recurse -File |
    Where-Object { $_.Extension -in @(".ps1", ".bat", ".cmd", ".exe", ".zip", ".vdf", ".pdb") })
if ($badFiles.Count -gt 0) {
    Write-Warning ("这些文件不建议上传，建议删掉：" + (($badFiles | ForEach-Object { $_.Name }) -join ", "))
}

$previewSize = (Get-Item (Join-Path $StageDir "preview.png")).Length
if ($previewSize -gt 1MB) { Write-Warning ("preview.png 有 {0:N0} 字节，Steam 要求小于 1 MB" -f $previewSize) }

$totalBytes = (Get-ChildItem $StageDir -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ""
Write-Host "-- 发布包内容 --"
Get-ChildItem $StageDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    "  {0,-52} {1,8:N0} B" -f $_.FullName.Substring($StageDir.Length + 1), $_.Length
}
"  合计 {0:N1} KB / {1} 个文件" -f ($totalBytes / 1KB), (Get-ChildItem $StageDir -Recurse -File).Count | Write-Host

# —— 3. 生成 vdf（steamcmd 要求 Windows 路径里的反斜杠转义）——
function ConvertTo-VdfPath([string]$p) { return $p.Replace('\', '\\') }

$vdf = @"
"workshopitem"
{
    "appid"            "4348910"
    "publishedfileid"  "$PublishedFileId"
    "contentfolder"    "$(ConvertTo-VdfPath $StageDir)"
    "previewfile"      "$(ConvertTo-VdfPath (Join-Path $StageDir 'preview.png'))"
    "visibility"       "$Visibility"
    "title"            "$Title"
    "description"      "$Description"
    "changenote"       "$ChangeNote"
}
"@
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
[System.IO.File]::WriteAllText($vdfPath, $vdf, $utf8)
Write-Host ""
Write-Host "vdf 已写入：$vdfPath"

if (-not $Upload) {
    Write-Host ""
    Write-Host "未上传（加 -Upload 才会调用 steamcmd）。手动上传命令：" -ForegroundColor Yellow
    $shownSteamCmd = if ($SteamCmd) { $SteamCmd } else { "<steamcmd 路径>" }
    Write-Host "  & `"$shownSteamCmd`" +login <账号> +workshop_build_item `"$vdfPath`" +quit"
    return
}

# —— 4. 上传 ——
if (-not $SteamCmd -or -not (Test-Path $SteamCmd)) {
    throw "找不到 steamcmd：用 -SteamCmd <路径>，或设置环境变量 STEAMCMD（下载 https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip 解压）"
}
if ([string]::IsNullOrWhiteSpace($Username)) {
    throw "上传需要 -Username <你的Steam账号>（密码与 Steam Guard 由 steamcmd 在终端里问）"
}

Write-Host ""
Write-Host "开始上传……（首次会要求密码 / Steam Guard 验证码）" -ForegroundColor Cyan
& $SteamCmd +login $Username +workshop_build_item $vdfPath +quit
if ($LASTEXITCODE -ne 0) { throw "steamcmd 退出码 $LASTEXITCODE" }

# steamcmd 会把新的 publishedfileid 写回 vdf
$after = [System.IO.File]::ReadAllText($vdfPath, [System.Text.Encoding]::UTF8)
$m = [regex]::Match($after, '"publishedfileid"\s+"(\d+)"')
if ($m.Success) {
    Write-Host ""
    Write-Host "物品 ID：$($m.Groups[1].Value)" -ForegroundColor Green
    Write-Host "物品页面：https://steamcommunity.com/sharedfiles/filedetails/?id=$($m.Groups[1].Value)"
    if ($PublishedFileId -eq "0") {
        Write-Host "下次更新时请带上 -PublishedFileId $($m.Groups[1].Value)，或直接复用这个 vdf。"
    }
}
