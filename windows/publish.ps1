param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = "artifacts/publish",
    [string]$Version = "",
    [switch]$SkipInstaller,
    [switch]$SkipModelDownload,
    [switch]$SkipPhonon
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

# Phonon engine shim (Rust). Built before `dotnet publish` so the csproj's Exists() check picks the DLL up.
$phononCrate = Join-Path $repoRoot "native\phonon"
$phononDll = Join-Path $phononCrate "target\release\dynamite_phonon.dll"
if (-not $SkipPhonon) {
    $cargo = Get-Command cargo -ErrorAction SilentlyContinue
    if ($cargo) {
        Write-Host "Building the Phonon engine shim (cargo build --release)..." -ForegroundColor Cyan
        Push-Location $phononCrate
        try {
            cargo build --release
            if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }
    }
    elseif (Test-Path $phononDll) {
        Write-Host "cargo not found; using the existing $phononDll" -ForegroundColor Yellow
    }
    else {
        Write-Host "cargo not found and no prebuilt dynamite_phonon.dll: the Phonon engine will be unavailable in this build." -ForegroundColor Yellow
        Write-Host "Install Rust (https://rustup.rs) and re-run, or pass -SkipPhonon to silence this."
    }
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

# Kokoro's model (325 MB) is not shipped: the app downloads it the first time Kokoro is chosen.
# Drop any copy left in the publish folder by older builds.
Get-ChildItem $OutputPath -Filter "kokoro*.onnx" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

function Ensure-PhononModel {
    param([string]$PublishDir)

    # Same pinned revision and hashes as PhononTtsService.ModelFiles.
    $revision = "8c64b55ec301728e2d1695bfea87a08a267d0c2d"
    $files = @(
        @{ Name = "config.json";               Sha256 = "7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06" },
        @{ Name = "tokenizer.json";            Sha256 = "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936" },
        @{ Name = "model.q8.gguf";             Sha256 = "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae" },
        @{ Name = "default-voice.safetensors"; Sha256 = "f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18" }
    )
    $dir = Join-Path $PublishDir "models\phonon-31m-en"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $repoModel = Join-Path (Split-Path $repoRoot -Parent) "models\phonon-31m-en"
    foreach ($f in $files) {
        $target = Join-Path $dir $f.Name
        if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -ieq $f.Sha256)) { continue }
        $local = Join-Path $repoModel $f.Name
        if ((Test-Path $local) -and ((Get-FileHash $local -Algorithm SHA256).Hash -ieq $f.Sha256)) {
            Copy-Item $local $target -Force
            continue
        }
        $url = "https://huggingface.co/gradium/phonon-31m-en/resolve/$revision/$($f.Name)"
        Write-Host "Downloading Phonon model file $($f.Name)..." -ForegroundColor Cyan
        $tmp = "$target.tmp"
        try {
            Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
            $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
            if ($hash -ine $f.Sha256) { throw "$($f.Name) failed its checksum ($hash)" }
            Move-Item $tmp $target -Force
        }
        catch {
            if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
            throw "Failed to obtain $($f.Name) for the installer: $_"
        }
    }
    Write-Host "Phonon model present: $dir"
}

if (-not $SkipModelDownload -and -not $SkipPhonon) {
    Ensure-PhononModel -PublishDir $OutputPath
}

# Portable zip (same payload as the installer: exe + Kokoro voices + Phonon shim and model).
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
