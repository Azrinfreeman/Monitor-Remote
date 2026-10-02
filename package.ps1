param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $PSScriptRoot ('artifacts\MonitorRemote-' + $Runtime + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'MonitorRemote.csproj') -c Release -r $Runtime --self-contained $selfContained -o (Join-Path $packageRoot 'server')
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed. Partial output retained.' }
dotnet publish (Join-Path $PSScriptRoot 'desktop\MonitorRemote.Desktop.csproj') -c Release -r $Runtime --self-contained $selfContained -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed. Partial output retained.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageRoot
Write-Output "Ready: $packageRoot\MonitorRemote.Desktop.exe"
