$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'src\SqlLineDebugger\bin\Release\net8.0-windows\SqlLineDebugger.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run .\build-debugger.ps1 first.' }
Push-Location $PSScriptRoot
try {
    $editorProcess = Start-Process -FilePath $exe -ArgumentList '--editor-smoke --output artifacts' -WindowStyle Hidden -PassThru
    if (-not $editorProcess.WaitForExit(20000)) { Stop-Process -Id $editorProcess.Id; throw 'Editor scroll test exceeded 20 seconds.' }
    Get-Content artifacts\editor-scroll-tests.log
    if ($editorProcess.ExitCode -ne 0) { throw "Editor scroll tests failed: $($editorProcess.ExitCode)" }
    $testProcess = Start-Process -FilePath $exe -ArgumentList '--self-test --log debugger-tests.log' -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(60000)) { Stop-Process -Id $testProcess.Id; throw 'Native tests exceeded 60 seconds; see debugger-tests.log.' }
    Get-Content debugger-tests.log
    if ($testProcess.ExitCode -ne 0) { throw "Native tests failed: $($testProcess.ExitCode)" }
    $tempProcess = Start-Process -FilePath $exe -ArgumentList '--tempdb-test --log artifacts/tempdb-tests.log' -WindowStyle Hidden -PassThru
    if (-not $tempProcess.WaitForExit(60000)) { Stop-Process -Id $tempProcess.Id; throw 'TempDB tests exceeded 60 seconds.' }
    Get-Content artifacts\tempdb-tests.log
    if ($tempProcess.ExitCode -ne 0) { throw "TempDB tests failed: $($tempProcess.ExitCode)" }
    $uiProcess = Start-Process -FilePath $exe -ArgumentList '--ui-smoke --output artifacts' -WindowStyle Hidden -PassThru
    if (-not $uiProcess.WaitForExit(60000)) { Stop-Process -Id $uiProcess.Id; throw 'UI tests exceeded 60 seconds; see artifacts\ui-tests.log.' }
    Get-Content artifacts\ui-tests.log
    if ($uiProcess.ExitCode -ne 0) { throw "UI tests failed: $($uiProcess.ExitCode)" }
    $tempUiProcess = Start-Process -FilePath $exe -ArgumentList '--tempdb-ui-smoke --output artifacts' -WindowStyle Hidden -PassThru
    if (-not $tempUiProcess.WaitForExit(60000)) { Stop-Process -Id $tempUiProcess.Id; throw 'TempDB UI tests exceeded 60 seconds.' }
    Get-Content artifacts\tempdb-ui-tests.log
    if ($tempUiProcess.ExitCode -ne 0) { throw "TempDB UI tests failed: $($tempUiProcess.ExitCode)" }
    $typesUiProcess = Start-Process -FilePath $exe -ArgumentList '--tempdb-types-ui-smoke --output artifacts' -WindowStyle Hidden -PassThru
    if (-not $typesUiProcess.WaitForExit(60000)) { Stop-Process -Id $typesUiProcess.Id; throw 'Types snapshot UI tests exceeded 60 seconds.' }
    Get-Content artifacts\tempdb-types-ui-tests.log
    if ($typesUiProcess.ExitCode -ne 0) { throw "Types snapshot UI tests failed: $($typesUiProcess.ExitCode)" }
    $globalUiProcess = Start-Process -FilePath $exe -ArgumentList '--tempdb-global-ui-smoke --output artifacts' -WindowStyle Hidden -PassThru
    if (-not $globalUiProcess.WaitForExit(60000)) { Stop-Process -Id $globalUiProcess.Id; throw 'Global copy UI tests exceeded 60 seconds.' }
    Get-Content artifacts\tempdb-global-ui-tests.log
    if ($globalUiProcess.ExitCode -ne 0) { throw "Global copy UI tests failed: $($globalUiProcess.ExitCode)" }
} finally { Pop-Location }
