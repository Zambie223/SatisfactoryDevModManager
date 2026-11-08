# Opens this repo in a dedicated VS Code profile set up for SDMM development.
#
# Why a separate profile: the C# tools need the .NET 10 SDK, and the profile's extensions and settings live under the
# cache root from tools\env.ps1 (M:\DevCache when present) instead of the user profile on C:. Your normal VS Code is
# not changed.
#
#   .\tools\open-vscode.ps1            open the repo (installs the C# extensions on first run)
#   .\tools\open-vscode.ps1 -Setup     only install/update the extensions
param([switch]$Setup)
. (Join-Path $PSScriptRoot 'env.ps1')   # puts the .NET 10 SDK first on PATH for VS Code and its terminals

$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $SdmmCache 'vscode-sdmm'
$userData = Join-Path $root 'data'
$extensions = Join-Path $root 'extensions'
$code = (Get-Command code -ErrorAction SilentlyContinue).Source
if (-not $code) { Write-Error "The 'code' command was not found. Install VS Code and make sure it is on PATH."; exit 1 }

# VS Code's CLI prints Node deprecation warnings on stderr; check exit codes instead of treating stderr as failure.
$profileArgs = @('--user-data-dir', $userData, '--extensions-dir', $extensions)
$wanted = 'ms-dotnettools.csharp', 'ms-dotnettools.csdevkit'
$installed = & $code @profileArgs --list-extensions 2>$null
foreach ($id in $wanted) {
    if ($installed -notcontains $id) {
        Write-Host "Installing $id into $extensions"
        & $code @profileArgs --install-extension $id 2>$null | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "Installing $id failed (exit code $LASTEXITCODE)."; exit 1 }
    }
}
if ($Setup) { return }

& $code @profileArgs $repo 2>$null
