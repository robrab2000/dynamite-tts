param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = "artifacts/publish",
    [string]$Version = "",
    [switch]$SkipInstaller,
    [switch]$SkipModelDownload
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
New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
New-Item -ItemType Directory -Force -Path "artifacts" | Out-Null

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

# Drop debug / import-lib leftovers from the publish folder (not needed at runtime).
Get-ChildItem $OutputPath -Recurse -Include *.pdb, *.lib, *.exp, *.iobj, *.ipdb -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

function Ensure-KokoroModel {
    param([string]$PublishDir)

    $target = Join-Path $PublishDir "kokoro.onnx"
    if ((Test-Path $target) -and ((Get-Item $target).Length -gt 1MB)) {
        Write-Host "Kokoro model already present: $target"
        return
    }

    $repoModel = Join-Path $repoRoot "kokoro.onnx"
    if ((Test-Path $repoModel) -and ((Get-Item $repoModel).Length -gt 1MB)) {
        Copy-Item $repoModel $target -Force
        Write-Host "Copied kokoro.onnx from repo root into publish output."
        return
    }

    $url = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx"
    Write-Host "Downloading Kokoro model for a ready-to-run install..." -ForegroundColor Cyan
    Write-Host "  $url"
    $tmp = "$target.tmp"
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        if ((Get-Item $tmp).Length -lt 1MB) { throw "Downloaded kokoro.onnx looks too small." }
        Move-Item $tmp $target -Force
        Write-Host "Model saved to $target ($([math]::Round((Get-Item $target).Length / 1MB, 1)) MB)"
    }
    catch {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        throw "Failed to obtain kokoro.onnx for the installer: $_"
    }
}

if (-not $SkipModelDownload) {
    Ensure-KokoroModel -PublishDir $OutputPath
}

# Portable zip (same payload as the installer: exe + voices + model).
$zipName = "DynamiteTts-$Runtime-v$Version.zip"
$zipPath = Join-Path "artifacts" $zipName
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $OutputPath "*") -DestinationPath $zipPath -CompressionLevel Optimal

function Find-ISCC {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$setupPath = $null
if (-not $SkipInstaller) {
    $iscc = Find-ISCC
    if (-not $iscc) {
        Write-Host "Inno Setup (ISCC) not found — attempting winget install..." -ForegroundColor Yellow
        winget install --id JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements --disable-interactivity
        $iscc = Find-ISCC
    }

    if (-not $iscc) {
        Write-Host "Skipping installer: Inno Setup Compiler (ISCC.exe) is not available." -ForegroundColor Yellow
        Write-Host "Install from https://jrsoftware.org/isinfo.php then re-run ./publish.ps1"
    }
    else {
        Write-Host "Building installer with $iscc ..." -ForegroundColor Cyan
        $iss = Join-Path $repoRoot "installer\DynamiteTts.iss"
        & $iscc "/DMyAppVersion=$Version" $iss
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Inno Setup failed with exit code $LASTEXITCODE" -ForegroundColor Red
            exit $LASTEXITCODE
        }
        $setupPath = Join-Path "artifacts" "DynamiteTts-Setup-v$Version.exe"
        if (-not (Test-Path $setupPath)) {
            throw "Expected installer not found: $setupPath"
        }
    }
}

Write-Host ""
Write-Host "Publish succeeded!" -ForegroundColor Green
Write-Host "  Executable: $OutputPath\DynamiteTts.exe"
Write-Host "  Zip:        $zipPath"
if ($setupPath) {
    Write-Host "  Installer:  $setupPath"
}
Write-Host "  Note:       Build is unsigned; Windows SmartScreen may warn on download." -ForegroundColor Yellow
