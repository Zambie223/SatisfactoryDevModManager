# Builds and tests the solution with the M: .NET SDK.
#   .\build.ps1            -> restore + build + test
#   .\build.ps1 -NoTest    -> restore + build
#   .\build.ps1 -Publish   -> also publish the GUI as a single self-contained exe to .\artifacts\publish
param(
    [switch]$NoTest,
    [switch]$Publish,
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\env.ps1')
Set-Location $PSScriptRoot

dotnet build SatisfactoryDevModManager.slnx -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $NoTest) {
    # global.json opts into Microsoft.Testing.Platform (required by xunit.v3 on the .NET 10 SDK).
    dotnet test --solution SatisfactoryDevModManager.slnx -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Publish) {
    $app = 'src\Sdmm.App\Sdmm.App.csproj'
    if (Test-Path $app) {
        # EnableCompressionInSingleFile shrinks the exe; the bundled files are decompressed in memory on each launch.
        dotnet publish $app -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true -o artifacts\publish
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } else {
        Write-Warning 'GUI project does not exist yet; nothing to publish.'
    }
}
