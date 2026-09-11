$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\WinCapture\WinCapture.csproj"
$publishPath = Join-Path $projectRoot "publish\win-x64"

dotnet restore $projectPath --runtime win-x64
dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishPath `
    -p:Platform=x64 `
    -p:PublishReadyToRun=false `
    -p:PublishSingleFile=false

Write-Host "发布完成：$publishPath" -ForegroundColor Green
