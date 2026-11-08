# Dot-source this before running dotnet: . .\tools\env.ps1
# Keeps the SDK, NuGet caches and test temp files on a chosen drive instead of the user profile.
#
#   SDMM_DOTNET_ROOT  .NET SDK folder to use. Default: 'M:\Program Files\dotnet10' if it exists, else dotnet on PATH.
#   SDMM_CACHE        Cache root.          Default: 'M:\DevCache' if it exists, else <repo>\.cache.

$repoRoot = Split-Path -Parent $PSScriptRoot

$script:SdmmDotnetRoot = if ($env:SDMM_DOTNET_ROOT) { $env:SDMM_DOTNET_ROOT }
                        elseif (Test-Path 'M:\Program Files\dotnet10\dotnet.exe') { 'M:\Program Files\dotnet10' }
                        else { $null }
$script:SdmmCache      = if ($env:SDMM_CACHE) { $env:SDMM_CACHE }
                        elseif (Test-Path 'M:\DevCache') { 'M:\DevCache' }
                        else { Join-Path $repoRoot '.cache' }

if ($SdmmDotnetRoot) {
    $env:DOTNET_ROOT              = $SdmmDotnetRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    # Put this SDK first so `dotnet` resolves to it in this shell only.
    if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $SdmmDotnetRoot })) {
        $env:PATH = "$SdmmDotnetRoot;$env:PATH"
    }
}

$env:DOTNET_CLI_HOME                   = Join-Path $SdmmCache 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT  = '1'   # Microsoft.Testing.Platform (used by xunit.v3) collects usage telemetry by default
$env:DOTNET_NOLOGO                     = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES                    = Join-Path $SdmmCache 'nuget\packages'
$env:NUGET_HTTP_CACHE_PATH             = Join-Path $SdmmCache 'nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH          = Join-Path $SdmmCache 'nuget\plugins-cache'
$env:SDMM_TEST_TEMP                    = Join-Path $SdmmCache 'temp\SDMM-tests'
