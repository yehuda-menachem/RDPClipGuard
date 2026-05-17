# ============================================================
#  RDPClipGuard - Publish Script
# ============================================================

$ProjectName  = "RDPClipGuard"
$ProjectFile  = "RDPClipGuard.csproj"
$OutputDir    = ".\publish"
$Configuration = "Release"
$Runtime      = "win-x64"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  $ProjectName - dotnet publish" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# נקה את תיקיית הפלט הקודמת
if (Test-Path $OutputDir) {
    Write-Host "Cleaning output directory: $OutputDir" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $OutputDir
}

# הרץ את dotnet publish
Write-Host "Running dotnet publish..." -ForegroundColor Green
Write-Host ""

dotnet publish $ProjectFile `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $OutputDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true

# בדוק אם ההרצה הצליחה
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Build SUCCEEDED!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""

    # הצג את הקבצים שנוצרו
    Write-Host "Output files in '$OutputDir':" -ForegroundColor Cyan
    Get-ChildItem -Path $OutputDir | ForEach-Object {
        $size = "{0:N0} KB" -f ($_.Length / 1KB)
        Write-Host "  $($_.Name)  ($size)" -ForegroundColor White
    }

    Write-Host ""
    $exePath = Join-Path $OutputDir "$ProjectName.exe"
    if (Test-Path $exePath) {
        Write-Host "Executable: $((Resolve-Path $exePath).Path)" -ForegroundColor Green
    }
} else {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "  Build FAILED! (exit code: $LASTEXITCODE)" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    exit $LASTEXITCODE
}
