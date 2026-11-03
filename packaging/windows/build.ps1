param(
    [string]$Version = "",
    [switch]$SkipInstaller,
    [switch]$SkipModelDownload
)

# Builds the Windows release: target\windows\publish (DynamiteTts.exe + the Phonon model), a
# portable zip, and the Inno Setup installer, all under target\windows.

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $repoRoot

if (-not $Version) {
    $Version = (Select-String -Path "Cargo.toml" -Pattern '^version = "(.+)"' | Select-Object -First 1).Matches.Groups[1].Value
}
$out = Join-Path $repoRoot "target\windows"
$publish = Join-Path $out "publish"

Write-Host "Building Dynamite TTS $Version (cargo build --release)..." -ForegroundColor Cyan
cargo build --release --locked
if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $publish | Out-Null
# Same file name as the earlier C# app, so an upgrade keeps its Start menu and Run-key entries.
Copy-Item "target\release\dynamite-tts.exe" (Join-Path $publish "DynamiteTts.exe")
Copy-Item "LICENSE" $publish

function Ensure-PhononModel([string]$PublishDir) {
    # Same pinned revision and hashes as MODEL_FILES in src/phonon.rs.
    $revision = "8c64b55ec301728e2d1695bfea87a08a267d0c2d"
    $files = @(
        @{ Name = "config.json";               Sha256 = "7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06" },
        @{ Name = "tokenizer.json";            Sha256 = "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936" },
        @{ Name = "model.q8.gguf";             Sha256 = "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae" },
        @{ Name = "default-voice.safetensors"; Sha256 = "f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18" }
    )
    $dir = Join-Path $PublishDir "models\phonon-31m-en"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    # Reuse the copy the app downloaded, when there is one.
    $cache = Join-Path $env:LOCALAPPDATA "DynamiteTts\models\phonon-31m-en"
    foreach ($f in $files) {
        $target = Join-Path $dir $f.Name
        $cached = Join-Path $cache $f.Name
        if ((Test-Path $cached) -and ((Get-FileHash $cached -Algorithm SHA256).Hash -ieq $f.Sha256)) {
            Copy-Item $cached $target -Force
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
            throw "Failed to obtain $($f.Name): $_"
        }
    }
    Write-Host "Phonon model bundled: $dir"
}

if (-not $SkipModelDownload) { Ensure-PhononModel $publish }

$zip = Join-Path $out "DynamiteTts-win-x64-v$Version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal

function Find-ISCC {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$setup = $null
if (-not $SkipInstaller) {
    $iscc = Find-ISCC
    if (-not $iscc) {
        Write-Host "Skipping the installer: Inno Setup 6 (ISCC.exe) is not installed (https://jrsoftware.org/isinfo.php)." -ForegroundColor Yellow
    }
    else {
        & $iscc "/DMyAppVersion=$Version" (Join-Path $PSScriptRoot "DynamiteTts.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
        $setup = Join-Path $out "DynamiteTts-Setup-v$Version.exe"
        if (-not (Test-Path $setup)) { throw "Expected installer not found: $setup" }
    }
}

Write-Host ""
Write-Host "Build succeeded" -ForegroundColor Green
Write-Host "  App:       $publish\DynamiteTts.exe"
Write-Host "  Zip:       $zip"
if ($setup) { Write-Host "  Installer: $setup" }
Write-Host "  Unsigned: SmartScreen may warn on download." -ForegroundColor Yellow
