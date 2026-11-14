# Builds a portable, self-contained OpenLine folder + zip. Run on Windows with the .NET 10 SDK.
param([ValidateSet("x64", "arm64")][string]$Arch = "x64")
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot   # works from any folder / "Run with PowerShell"
try {
    $platform = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
    $out = Join-Path $PSScriptRoot "dist/OpenLine-win-$Arch"
    Remove-Item $out, "$out.zip" -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $PSScriptRoot "src/OpenLine/OpenLine.csproj") -c Release -r "win-$Arch" -p:Platform=$platform -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Compress-Archive -Path "$out/*" -DestinationPath "$out.zip" -Force
    Write-Host "Portable build: $out.zip (unzip anywhere, run OpenLine.exe)"
}
finally {
    Pop-Location
}
