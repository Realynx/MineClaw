# Builds a self-contained linux-x64 MineClaw into deploy/out/mineclaw from Windows (same as publish-linux.sh).
#   pwsh deploy/publish-linux.ps1          # single-file binary + native libs next to it (default)
#   pwsh deploy/publish-linux.ps1 -Multi   # classic multi-file layout
# Copy deploy/out/mineclaw to the container (scp -r) and follow deploy/README.md.
param([switch]$Multi)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$out = 'deploy/out/mineclaw'
$single = if ($Multi) { 'false' } else { 'true' }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish src/MineClaw.App/MineClaw.App.csproj -c Release -r linux-x64 --self-contained true -m:1 -p:UseSharedCompilation=false `
  -p:PublishSingleFile=$single -p:DebugType=none -p:GenerateDocumentationFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Never ship the real (gitignored) config: secrets go in /etc/mineclaw (see deploy/README.md).
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $out 'appsettings.json')
New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
Copy-Item -Recurse docs/knowledge (Join-Path $out 'docs')
if (Test-Path data/seed) {
    New-Item -ItemType Directory -Force (Join-Path $out 'seed') | Out-Null
    Copy-Item -Recurse data/seed/* (Join-Path $out 'seed') -ErrorAction SilentlyContinue
}
Copy-Item deploy/mineclaw.service, deploy/mineclaw.env.example $out
Write-Host "published to $out (chmod +x MineClaw.App after copying to Linux if your copy tool drops the bit)"
Get-ChildItem $out
