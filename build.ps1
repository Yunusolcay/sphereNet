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
Write-Step 1 2 "Vue panel derleniyor (npm run build)..."

Push-Location "$root\panel"
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build basarisiz oldu." }
} finally {
    Pop-Location
}

# ── 2. C# cozumu ─────────────────────────────────────────────────────────────
Write-Step 2 2 "C# cozumu derleniyor ($Configuration)..."

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
