# 一键打包成 ClassIsland 能直接导入的 .cipx
#
#   用法（在仓库根目录）：
#       pwsh -ep bypass -File .\Pack-Cipx.ps1
#   或者直接双击同目录的 Pack-Cipx.cmd
#   （Windows 默认「禁止运行脚本」，所以要带 -ep bypass，否则会报 PSSecurityException）
#
#   产物：  cipx\ClassSlideSync.Plugin.cipx   ClassIsland「导入插件包」就选这个文件
#           cipx\checksums.md                带 CLASSISLAND_PKG_MD5 注释，上架插件市场要用
#
# 打包靠 ClassIsland.PluginSdk 自带的 MSBuild 目标 CreateCipx：
#   dotnet build -c Release -p:CreateCipx=true
# 它内部要调 pwsh 生成 MD5；如果机器上没有 pwsh（只有 Windows PowerShell），
# 就用 -p:PowershellBinaryName=... 指定一个能用的。

param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "ClassSlideSync.Plugin.csproj"
if (-not (Test-Path $proj)) { throw "找不到 $proj" }

# 挑一个「真的带 SDK」的 dotnet：PATH 上那个可能只装了运行时
$cands = @()
if ($env:LOCALAPPDATA) { $cands += (Join-Path $env:LOCALAPPDATA "dotnet-sdk\dotnet.exe") }
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $cands += $onPath.Source }
$cands += "C:\Program Files\dotnet\dotnet.exe"

$dotnet = $null
foreach ($c in $cands) {
    if (-not (Test-Path $c)) { continue }
    $sdks = & $c --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $c; break }
}
if (-not $dotnet) {
    throw "找不到带 SDK 的 dotnet。请装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0），或把 SDK 解压到 $env:LOCALAPPDATA\dotnet-sdk\"
}

# 这里只传「命令名」，不要传完整路径：
# SDK 的 MSBuild 目标拼命令行时不给 $(PowershellBinaryName) 加引号，一旦路径里带空格
# （例如 C:\Program Files\PowerShell\7\pwsh.exe）就会被 cmd 从空格处拆开，
# 表现为 error MSB3073 ... exited with code 9009（GitHub Actions 上必踩）。传命令名则由 PATH 解析，稳。
$shell = $null
foreach ($name in @("pwsh", "powershell")) {
    if (Get-Command $name -ErrorAction SilentlyContinue) { $shell = $name; break }
}
if (-not $shell) { throw "找不到 pwsh / PowerShell" }

Write-Host "dotnet : $dotnet"
Write-Host "shell  : $shell"

& $dotnet build $proj -c $Configuration -p:CreateCipx=true -p:PowershellBinaryName="$shell"
if ($LASTEXITCODE -ne 0) { throw "打包失败" }

Get-ChildItem (Join-Path $PSScriptRoot "cipx") | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "好了，把 cipx\ClassSlideSync.Plugin.cipx 丢给 ClassIsland 的「导入插件包」即可。"
