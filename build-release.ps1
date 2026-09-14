param(
    # 默认使用 PATH 上的 dotnet；本机若只把 .NET 8 SDK 装在自定义目录，可传：
    #   .\build-release.ps1 -DotNet "$env:USERPROFILE\.dotnet8\dotnet.exe"
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\WinCapture\WinCapture.csproj"
# 单文件发布：产物只有一个 WinCapture.exe，输出到独立目录，
# 不影响 publish\win-x64（那是旧的非单文件版本）。
$publishPath = Join-Path $projectRoot "publish\win-x64-single"

& $DotNet restore $projectPath --runtime win-x64

& $DotNet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath `
    -p:Platform=x64 `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

Write-Host "发布完成：$publishPath\WinCapture.exe（单文件，直接拷贝即可运行）" -ForegroundColor Green
