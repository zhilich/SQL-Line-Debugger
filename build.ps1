$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /platform:x64 /target:exe /r:System.Data.dll /out:"$output\NativeProbe.exe" "$PSScriptRoot\Runner.cs"
if ($LASTEXITCODE -ne 0) { throw 'Native probe compilation failed.' }
Push-Location $PSScriptRoot
try { & .\build-native.cmd; if ($LASTEXITCODE -ne 0) { throw 'Native COM host compilation failed.' } }
finally { Pop-Location }
