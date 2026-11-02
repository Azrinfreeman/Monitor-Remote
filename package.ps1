param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$FrameworkDependent, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $PSScriptRoot ('artifacts\MonitorRemote-' + $Runtime + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
if ($OutputDirectory) { $packageRoot = [IO.Path]::GetFullPath($OutputDirectory) }
if (Test-Path -LiteralPath $packageRoot) { throw 'Package output must be a new directory.' }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
New-Item -ItemType Directory -Path $packageRoot | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'MonitorRemote.csproj') -c Release -r $Runtime --self-contained $selfContained -o (Join-Path $packageRoot 'server')
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed. Partial output retained.' }
# The WinUI launcher is built rather than published: unpackaged WinUI publish omits the compiled XAML (.xbf) and resource index (.pri).
dotnet build (Join-Path $PSScriptRoot 'desktop\MonitorRemote.Desktop.csproj') -c Release -r $Runtime --self-contained $selfContained -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed. Partial output retained.' }
# The XAML build targets concatenate an output path without a separator when -o is used.
# Copy the compiler's actual XBF outputs explicitly so a successful build is launchable.
$xamlOutput = Join-Path $PSScriptRoot "desktop\obj\Release\net10.0-windows10.0.26100.0\$Runtime\output.json"
$xamlFiles = (Get-Content -LiteralPath $xamlOutput -Raw | ConvertFrom-Json).GeneratedXbfFiles
if (!$xamlFiles) { throw 'No compiled XAML found.' }
foreach ($file in $xamlFiles) { Copy-Item -LiteralPath $file -Destination $packageRoot }
foreach ($required in @('App.xbf', 'MainWindow.xbf', 'MonitorRemote.Desktop.pri', 'MonitorRemote.Desktop.exe', 'server\MonitorRemote.exe', 'server\wwwroot\index.html')) {
    if (!(Test-Path -LiteralPath (Join-Path $packageRoot $required))) { throw "Package is missing $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageRoot
Write-Output "Ready: $packageRoot\MonitorRemote.Desktop.exe"
