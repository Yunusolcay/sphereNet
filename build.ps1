<#
.SYNOPSIS
    SphereNet tam derleme scripti.
    Çıktı: bin\Debug\  veya  bin\Release\

.PARAMETER Configuration
    Debug (varsayılan) veya Release

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Write-Step($n, $total, $msg) {
    Write-Host ""
    Write-Host "[$n/$total] $msg" -ForegroundColor Cyan
}

# ── 1. Vue panel ─────────────────────────────────────────────────────────────
Write-Step 1 3 "Panel bagimliliklari kontrol ediliyor..."

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
    Write-Host "  node_modules mevcut, atlanıyor." -ForegroundColor DarkGray
}

Write-Step 2 3 "Vue panel derleniyor (npm run build)..."

Push-Location "$root\panel"
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build basarisiz oldu." }
} finally {
    Pop-Location
}

# ── 3. C# cozumu ─────────────────────────────────────────────────────────────
Write-Step 3 3 "C# cozumu derleniyor ($Configuration)..."

dotnet build "$root\sphereNet.sln" `
    --configuration $Configuration `
    --nologo `
    -p:nowarn=NU1507

if ($LASTEXITCODE -ne 0) { throw "dotnet build basarisiz oldu." }

# ── Sonuc ─────────────────────────────────────────────────────────────────────
$outDir = "$root\bin\$Configuration"
Write-Host ""
Write-Host "Derleme tamamlandi!" -ForegroundColor Green
Write-Host "Cikti: $outDir" -ForegroundColor Yellow
Write-Host ""
Write-Host "Baslatmak icin:" -ForegroundColor White
Write-Host "  $outDir\SphereNet.Host.exe" -ForegroundColor White
Write-Host ""
Write-Host "Not: sphere.ini ve scripts\ klasorunu" -ForegroundColor DarkGray
Write-Host "     $outDir\ altina koymay unutmayin." -ForegroundColor DarkGray
