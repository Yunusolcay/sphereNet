<#
.SYNOPSIS
    SphereNet tam derleme scripti.
    Debug:   bin\Debug\   (hizli derleme, DLL'ler ayri)
    Release: bin\Release\ (tek EXE, tum DLL'ler gomulu)

.PARAMETER Configuration
    Debug (varsayilan) veya Release

.PARAMETER Runtime
    Hedef platform (Release single-file icin).
    Varsayilan: win-x64
    Ornekler:   win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64

.PARAMETER Clean
    Onceki derleme ciktisini temizle.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Release
    .\build.ps1 -Configuration Release -Runtime linux-x64
    .\build.ps1 -Configuration Release -Clean
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Runtime = "win-x64",

    [switch]$Clean
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$outDir = "$root\bin\$Configuration"

function Write-Step($n, $total, $msg) {
    Write-Host ""
    Write-Host "[$n/$total] $msg" -ForegroundColor Cyan
}

# ── 0. Temizlik ──────────────────────────────────────────────────────────────
if ($Clean -and (Test-Path $outDir)) {
    Write-Host "Onceki cikti temizleniyor: $outDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $outDir -Confirm:$false
}

# ── 1. Vue panel ─────────────────────────────────────────────────────────────
Write-Step 1 4 "Panel bagimliliklari kontrol ediliyor..."

if (-not (Test-Path "$root\panel\node_modules")) {
    Write-Host "  node_modules bulunamadi, npm install calisiyor..." -ForegroundColor Yellow
    Push-Location "$root\panel"
    try {
        npm install
        if ($LASTEXITCODE -ne 0) { throw "npm install basarisiz oldu." }
    } finally {
        Pop-Location
    }
} else {
    Write-Host "  node_modules mevcut, atlaniyor." -ForegroundColor DarkGray
}

Write-Step 2 4 "Vue panel derleniyor (npm run build)..."

Push-Location "$root\panel"
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build basarisiz oldu." }
} finally {
    Pop-Location
}

# ── 3. C# cozumu ─────────────────────────────────────────────────────────────
if ($Configuration -eq "Release") {

    $publishArgs = @(
        "--configuration", "Release",
        "--runtime", $Runtime,
        "--nologo",
        "-p:SelfContained=false",
        "-p:PublishSingleFile=true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:nowarn=NU1507"
    )

    # 3a. SphereNet.Server (oyun sunucusu — tum game/network/scripting DLL'leri gomulu)
    Write-Step 3 4 "SphereNet.Server derleniyor (single-file, $Runtime)..."

    dotnet publish "$root\src\SphereNet.Server\SphereNet.Server.csproj" `
        @publishArgs `
        --output $outDir

    if ($LASTEXITCODE -ne 0) { throw "SphereNet.Server publish basarisiz oldu." }

    # 3b. SphereNet.Host (panel + launcher — ayri single-file)
    Write-Step 4 4 "SphereNet.Host derleniyor (single-file, $Runtime)..."

    # Host'u gecici bir klasore publish et, sonra sadece EXE'yi kopyala
    $hostTmp = "$root\bin\_host_tmp"
    if (Test-Path $hostTmp) { Remove-Item -Recurse -Force $hostTmp -Confirm:$false }

    dotnet publish "$root\src\SphereNet.Host\SphereNet.Host.csproj" `
        @publishArgs `
        --output $hostTmp

    if ($LASTEXITCODE -ne 0) { throw "SphereNet.Host publish basarisiz oldu." }

    # Host EXE ve runtimeconfig'i ana ciktiya kopyala
    $exeExt = if ($Runtime -like "win-*") { ".exe" } else { "" }
    Copy-Item "$hostTmp\SphereNet.Host$exeExt" $outDir -Force
    Copy-Item "$hostTmp\SphereNet.Host.runtimeconfig.json" $outDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$hostTmp\SphereNet.Host.deps.json" $outDir -Force -ErrorAction SilentlyContinue

    # panel/ klasoru Host publish'ten gelir, Server'da yoksa kopyala
    if ((Test-Path "$hostTmp\panel") -and -not (Test-Path "$outDir\panel")) {
        Copy-Item "$hostTmp\panel" $outDir -Recurse -Force
    }

    # Gecici klasoru temizle
    Remove-Item -Recurse -Force $hostTmp -Confirm:$false

    # Ara build ciktilari temizle — single-file EXE icinde gomulu, ayrica gerekmez
    # Kalacaklar: Host.exe, Server.exe, Host/Server .deps.json + .runtimeconfig.json,
    #             e_sqlite3.dll (native), panel/ klasoru
    $keep = @(
        "SphereNet.Host.exe", "SphereNet.Host", # Linux'ta uzantisiz
        "SphereNet.Server.exe", "SphereNet.Server",
        "SphereNet.Host.deps.json", "SphereNet.Host.runtimeconfig.json",
        "SphereNet.Server.deps.json", "SphereNet.Server.runtimeconfig.json",
        "e_sqlite3.dll", "libe_sqlite3.so", "libe_sqlite3.dylib"
    )
    Get-ChildItem $outDir -File | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Force

} else {
    Write-Step 3 4 "C# cozumu derleniyor (Debug)..."

    dotnet build "$root\sphereNet.sln" `
        --configuration Debug `
        --nologo `
        -p:nowarn=NU1507

    if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu." }

    Write-Step 4 4 "Debug derleme tamamlandi."
}

# ── Sonuc ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Derleme tamamlandi! ($Configuration)" -ForegroundColor Green
Write-Host "Cikti: $outDir" -ForegroundColor Yellow

if ($Configuration -eq "Release") {
    $fileCount = (Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | Measure-Object).Count
    $dirCount = (Get-ChildItem $outDir -Directory -ErrorAction SilentlyContinue | Measure-Object).Count
    $totalSize = "{0:N1} MB" -f ((Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum / 1MB)

    Write-Host ""
    Write-Host "  Kök dosya: $fileCount, Klasor: $dirCount" -ForegroundColor DarkGray
    Write-Host "  Toplam boyut: $totalSize" -ForegroundColor DarkGray
    Write-Host ""
    Get-ChildItem $outDir -File | ForEach-Object {
        $sizeMB = "{0,8:N2} MB" -f ($_.Length / 1MB)
        Write-Host "  $sizeMB  $($_.Name)" -ForegroundColor DarkGray
    }
    Get-ChildItem $outDir -Directory | ForEach-Object {
        $dirSize = "{0,8:N2} MB" -f ((Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB)
        Write-Host "  $dirSize  $($_.Name)\" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Baslatmak icin:" -ForegroundColor White
Write-Host "  $outDir\SphereNet.Host.exe" -ForegroundColor White
Write-Host ""
Write-Host "Not: sphere.ini ve scripts\ klasorunu" -ForegroundColor DarkGray
Write-Host "     $outDir\ altina koymayi unutmayin." -ForegroundColor DarkGray
