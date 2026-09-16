param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = "artifacts/publish",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Publishing Dynamite TTS ($Configuration / $Runtime)" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$projectPath = "src/DynamiteTts/DynamiteTts.csproj"
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

if (-not $Version) {
    $Version = (Select-Xml -Path $projectPath -XPath "//Version" | Select-Object -ExpandProperty Node).InnerText
    if (-not $Version) { $Version = "0.0.0" }
}

if (Test-Path $OutputPath) {
    Remove-Item -Recurse -Force $OutputPath
}

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutputPath

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$zipName = "DynamiteTts-$Runtime-v$Version.zip"
$zipPath = Join-Path "artifacts" $zipName
New-Item -ItemType Directory -Force -Path "artifacts" | Out-Null
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

# Compress the publish folder contents (exe + voices + optional onnx), not the folder itself.
Compress-Archive -Path (Join-Path $OutputPath "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host "Publish succeeded!" -ForegroundColor Green
Write-Host "  Executable: $OutputPath\DynamiteTts.exe"
Write-Host "  Zip:        $zipPath"
Write-Host "  Note:       Build is unsigned; Windows SmartScreen may warn on download." -ForegroundColor Yellow
