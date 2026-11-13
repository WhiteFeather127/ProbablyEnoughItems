param(
    [string]$GameDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 游戏目录按优先级解析，脚本里不写死绝对路径：
#   1) -GameDir <路径>
#   2) 环境变量 PROBABLY_STOLEN_DIR
#   3) Steam 注册表（HKCU\Software\Valve\Steam 的 SteamPath）下的 steamapps\common\Probably Stolen
if (-not $GameDir) { $GameDir = $env:PROBABLY_STOLEN_DIR }
if (-not $GameDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if ($steam) { $GameDir = Join-Path ($steam -replace '/', '\') 'steamapps\common\Probably Stolen' }
}
if (-not $GameDir) {
    throw "找不到游戏目录：用 -GameDir <路径>，或设置环境变量 PROBABLY_STOLEN_DIR"
}

$modDir = Join-Path $GameDir "Mods\ProbablyEnoughItems"
$dll = Join-Path $projectDir "bin\$Configuration\netstandard2.1\ProbablyEnoughItems.dll"

Write-Host "== ProbablyEnoughItems 安装 =="
Write-Host "工程目录 : $projectDir"
Write-Host "游戏目录 : $GameDir"
Write-Host "目标目录 : $modDir"

if (-not (Test-Path $dll)) {
    Write-Host "未找到编译产物，正在编译..."
    Push-Location $projectDir
    dotnet build -c $Configuration "-p:GameDir=$GameDir"
    Pop-Location
}

if (-not (Test-Path $dll)) {
    throw "编译失败或未生成 $dll"
}

New-Item -ItemType Directory -Force -Path $modDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $modDir "Localization") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $modDir "Data") | Out-Null

Copy-Item $dll -Destination $modDir -Force
Copy-Item (Join-Path $projectDir "manifest.xml") -Destination $modDir -Force
$preview = Join-Path $projectDir "preview.png"
if (Test-Path $preview) { Copy-Item $preview -Destination $modDir -Force }
Copy-Item (Join-Path $projectDir "Localization\*.csv") -Destination (Join-Path $modDir "Localization") -Force
Copy-Item (Join-Path $projectDir "Data\*.csv") -Destination (Join-Path $modDir "Data") -Force

Write-Host "已安装到 $modDir"
Get-ChildItem -Recurse $modDir | Select-Object FullName, Length | Format-Table -AutoSize
