param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
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

# 每个架构一个友好文件名，避免同一文件复制多份
$FriendlyName = switch ($Runtime) {
    "win-x64" { "小申陪伴-x64.exe" }
    "win-arm64" { "小申陪伴-arm64.exe" }
    "win-x86" { "小申陪伴-x86.exe" }
    default { "小申陪伴.exe" }
}
$EnglishName = switch ($Runtime) {
    "win-x64" { "BunnyCompanion-win-x64.exe" }
    "win-arm64" { "BunnyCompanion-win-arm64.exe" }
    "win-x86" { "BunnyCompanion-win-x86.exe" }
    default { "BunnyCompanion.exe" }
}

Write-Host "正在清理 $Runtime ……" -ForegroundColor DarkGray
Remove-Item $PublishDirectory -Recurse -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $DeliveryDirectory)) {
    New-Item $DeliveryDirectory -ItemType Directory -Force | Out-Null
}
New-Item $PublishDirectory -ItemType Directory -Force | Out-Null

Write-Host "正在发布 $Runtime 自包含单文件……" -ForegroundColor Cyan
& dotnet publish $ProjectFile `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $PublishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:RuntimeIdentifier=$Runtime

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败。"
}

$PublishedExe = Join-Path $PublishDirectory "BunnyCompanion.exe"
if (-not (Test-Path $PublishedExe)) {
    throw "未找到 BunnyCompanion.exe。"
}

$UnexpectedFiles = @(Get-ChildItem $PublishDirectory -File -Recurse | Where-Object {
    $_.FullName -ne $PublishedExe -and $_.Extension -ne ".pdb"
})
if ($UnexpectedFiles.Count -gt 0) {
    $Names = ($UnexpectedFiles | Select-Object -ExpandProperty Name) -join "、"
    throw "发布目录仍有未嵌入文件：$Names"
}

$SizeMb = [Math]::Round(((Get-Item -LiteralPath $PublishedExe).Length / 1048576.0), 2)
if ($SizeMb -lt 40) {
    throw "产物过小（$SizeMb MB），疑似不是自包含包。"
}

$FriendlyExe = Join-Path $DeliveryDirectory $FriendlyName
$EnglishExe = Join-Path $DeliveryDirectory $EnglishName
Copy-Item $PublishedExe $FriendlyExe -Force
Copy-Item $PublishedExe $EnglishExe -Force

# 说明与校验（多架构时追加写入）
$ReadmeSrc = Join-Path $ProjectRoot "使用说明.txt"
if (Test-Path $ReadmeSrc) {
    Copy-Item $ReadmeSrc (Join-Path $DeliveryDirectory "使用说明.txt") -Force
}

$Hash = (Get-FileHash $EnglishExe -Algorithm SHA256).Hash
$CheckPath = Join-Path $DeliveryDirectory "版本校验.txt"
$Line = @"
[$Runtime]
文件（中文名）：$FriendlyName
文件（英文名）：$EnglishName
大小：$SizeMb MB
SHA256：$Hash
构建时间：$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

"@
if (Test-Path $CheckPath) {
    Add-Content -LiteralPath $CheckPath -Value $Line -Encoding UTF8
} else {
    # 版本号从 csproj 读取，避免与程序内不一致
    $Csproj = Join-Path $ProjectRoot "BunnyCompanion\BunnyCompanion.csproj"
    $Ver = "0.0.0"
    if (Test-Path $Csproj) {
        $m = [regex]::Match((Get-Content $Csproj -Raw), '<Version>([^<]+)</Version>')
        if ($m.Success) { $Ver = $m.Groups[1].Value }
    }
    $Header = "小申陪伴 $Ver（自包含 · 免装 .NET）`n每架构一个 EXE，请按电脑 CPU 选择下载。`n`n"
    Set-Content -LiteralPath $CheckPath -Value ($Header + $Line) -Encoding UTF8
}

Write-Host "完成：$FriendlyExe / $EnglishExe （$SizeMb MB）" -ForegroundColor Green
