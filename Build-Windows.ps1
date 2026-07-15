param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ProjectRoot "BunnyCompanion\BunnyCompanion.csproj"
$PublishDirectory = Join-Path $ProjectRoot "发布\$Runtime"
$DeliveryDirectory = Join-Path $ProjectRoot "可直接发送"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 .NET SDK。请安装 .NET 8 SDK 或 Visual Studio 2022 的 .NET 桌面开发工作负载。"
}

$SdkVersion = & dotnet --version
$SdkMajor = [int]($SdkVersion.Split('.')[0])
if ($SdkMajor -lt 8) {
    throw "当前 .NET SDK 版本为 $SdkVersion，需要 .NET 8 或更高版本。"
}

Write-Host "正在清理旧文件……" -ForegroundColor DarkGray
Remove-Item $PublishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DeliveryDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $PublishDirectory -ItemType Directory -Force | Out-Null
New-Item $DeliveryDirectory -ItemType Directory -Force | Out-Null

Write-Host "正在发布 $Runtime 框架依赖单文件版本（小体积，约 10MB 级）……" -ForegroundColor Cyan
& dotnet publish $ProjectFile `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output $PublishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，请查看上方编译错误。"
}

$PublishedExe = Join-Path $PublishDirectory "BunnyCompanion.exe"
if (-not (Test-Path $PublishedExe)) {
    throw "发布完成但未找到 BunnyCompanion.exe。"
}

$UnexpectedFiles = @(Get-ChildItem $PublishDirectory -File -Recurse | Where-Object {
    $_.FullName -ne $PublishedExe -and $_.Extension -ne ".pdb"
})
if ($UnexpectedFiles.Count -gt 0) {
    $Names = ($UnexpectedFiles | Select-Object -ExpandProperty Name) -join "、"
    throw "发布目录仍存在未嵌入的运行文件：$Names。"
}

$SizeBytes = (Get-Item -LiteralPath $PublishedExe).Length
$SizeMb = [Math]::Round(($SizeBytes / 1048576.0), 2)
# 框架依赖包通常数 MB～20MB；过大说明误开了自包含
if ($SizeMb -gt 25) {
    throw "产物过大（$SizeMb MB），疑似仍为自包含或资源膨胀，已停止交付。"
}
if ($SizeMb -lt 1) {
    throw "产物过小（$SizeMb MB），可能发布失败。"
}

$FriendlyExe = Join-Path $DeliveryDirectory "小申陪伴.exe"
Copy-Item $PublishedExe $FriendlyExe
Copy-Item (Join-Path $ProjectRoot "使用说明.txt") $DeliveryDirectory

$Hash = (Get-FileHash $FriendlyExe -Algorithm SHA256).Hash
$BuildInfo = @"
小申陪伴 1.1（框架依赖 · 小体积）
构建时间：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
运行架构：$Runtime
文件大小：$SizeMb MB
SHA256：$Hash
说明：需安装 .NET 8 Desktop Runtime（x64）
"@
$BuildInfo | Set-Content (Join-Path $DeliveryDirectory "版本校验.txt") -Encoding UTF8

Write-Host ""
Write-Host "构建成功：$FriendlyExe （$SizeMb MB）" -ForegroundColor Green
Write-Host "目标电脑需安装 .NET 8 Desktop Runtime：https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
