param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
if (!$IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    else {
        $IsccPath = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
    throw 'Install Inno Setup 6, or supply -IsccPath pointing to ISCC.exe.'
}
$packageRoot = Join-Path $PSScriptRoot ('artifacts\installer-payload-' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'package.ps1') -Runtime win-x64 -OutputDirectory $packageRoot
$previousReport = $env:MONITOR_CHECK_REPORT
$env:MONITOR_CHECK_REPORT = "$packageRoot-check-results.txt"
try {
    $check = Start-Process -FilePath (Join-Path $packageRoot 'MonitorRemote.Desktop.exe') -ArgumentList '--check' -Wait -PassThru -WindowStyle Hidden
    if ($check.ExitCode -ne 0) { throw "Packaged launcher checks failed: $env:MONITOR_CHECK_REPORT" }
    Get-Content -LiteralPath $env:MONITOR_CHECK_REPORT
} finally { $env:MONITOR_CHECK_REPORT = $previousReport }
& $IsccPath "/DPackageRoot=$packageRoot" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'installer\MonitorRemote.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $PSScriptRoot "artifacts\MonitorRemote-$Version-win-x64-Setup.exe"
Get-FileHash -LiteralPath $installer -Algorithm SHA256 | Format-List
Write-Output "Installer: $installer"
