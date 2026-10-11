param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & .\native\build.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed. Install the Visual Studio C++ x64 build tools.' }
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    & dotnet build .\src\SqlLineDebugger\SqlLineDebugger.csproj -c Release --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Windows Forms build failed.' }
    if ($Publish) {
        & dotnet publish .\src\SqlLineDebugger\SqlLineDebugger.csproj -c Release --no-restore -o .\artifacts\SqlLineDebugger
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
} finally { Pop-Location }
