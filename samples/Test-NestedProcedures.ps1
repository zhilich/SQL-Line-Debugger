param([string]$Server = '(localdb)\mssqllocaldb')
$ErrorActionPreference = 'Stop'
$testLog = [Collections.Generic.List[string]]::new()
function Assert-Sample([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
}
function Invoke-Sample([int]$Seed, [int]$Count, [string]$Scenario, [bool]$Rethrow = $false) {
    $sampleConnection = [System.Data.SqlClient.SqlConnection]::new(
        "Server=$Server;Database=Test;Integrated Security=True;TrustServerCertificate=True;Pooling=False")
    try {
        $sampleConnection.Open()
        $sampleCommand = $sampleConnection.CreateCommand()
        $sampleCommand.CommandType = [Data.CommandType]::StoredProcedure
        $sampleCommand.CommandText = 'SqlDebugTest.Run'
        $sampleCommand.CommandTimeout = 15
        $null = $sampleCommand.Parameters.AddWithValue('@Seed', $Seed)
        $null = $sampleCommand.Parameters.AddWithValue('@OrderCount', $Count)
        $null = $sampleCommand.Parameters.AddWithValue('@Scenario', $Scenario)
        $null = $sampleCommand.Parameters.AddWithValue('@Verbose', $false)
        $null = $sampleCommand.Parameters.AddWithValue('@Rethrow', $Rethrow)
        $returnParameter = $sampleCommand.Parameters.Add('@RETURN_VALUE', [Data.SqlDbType]::Int)
        $returnParameter.Direction = [Data.ParameterDirection]::ReturnValue
        $sampleData = [Data.DataSet]::new()
        $sampleAdapter = [System.Data.SqlClient.SqlDataAdapter]::new($sampleCommand)
        try { $null = $sampleAdapter.Fill($sampleData) }
        finally { $sampleAdapter.Dispose(); $sampleCommand.Dispose() }
        return [pscustomobject]@{ Data = $sampleData; ReturnCode = $returnParameter.Value }
    } finally { $sampleConnection.Dispose() }
}
function Assert-Totals($Result, [int]$Orders) {
    Assert-Sample ($Result.Data.Tables.Count -eq 4) 'Four result sets'
    Assert-Sample ($Result.ReturnCode -eq 0) 'Success RETURN code'
    $summary = $Result.Data.Tables[0].Rows[0]
    Assert-Sample ($summary.CompletedOrders -eq $Orders) 'Completed order count'
    Assert-Sample ($summary.CreatedLines -eq ($Orders * 3)) 'Created line count'
    Assert-Sample ($summary.ErrorNumber -is [DBNull]) 'No caught error'
    $orderRows = $Result.Data.Tables[1].Rows
    $lineRows = $Result.Data.Tables[2].Rows
    Assert-Sample ($orderRows.Count -eq $Orders) 'Order result rows'
    Assert-Sample ($lineRows.Count -eq ($Orders * 3)) 'Line result rows'
    [decimal]$grandTotal = 0
    foreach ($orderRow in $orderRows) {
        [decimal]$lineSum = 0
        foreach ($lineRow in $lineRows) {
            if ($lineRow.OrderId -eq $orderRow.OrderId) { $lineSum += $lineRow.LineTotal }
            Assert-Sample ($lineRow.Net -eq ($lineRow.Gross - $lineRow.DiscountAmount)) 'Net arithmetic'
            Assert-Sample ($lineRow.LineTotal -eq ($lineRow.Net + $lineRow.Tax)) 'Line total arithmetic'
            Assert-Sample ($lineRow.DiscountRate -ge 0 -and $lineRow.DiscountRate -le 0.1) 'Discount cap'
        }
        Assert-Sample ($orderRow.Total -eq ($lineSum + $orderRow.Shipping)) 'Order total arithmetic'
        $grandTotal += $orderRow.Total
    }
    Assert-Sample ($summary.GrandTotal -eq $grandTotal) 'Grand total arithmetic'
    $deepRows = @($Result.Data.Tables[3].Rows | Where-Object ModuleName -EQ AssessRisk)
    Assert-Sample ($deepRows.Count -eq ($Orders * 3)) 'Deepest procedure called for every line'
}
try {
    $normal = Invoke-Sample 7 3 normal
    Assert-Totals $normal 3
    $testLog.Add("PASS normal: 3 orders, 9 lines, 4 result sets, GrandTotal=$($normal.Data.Tables[0].Rows[0].GrandTotal)")
    $edge = Invoke-Sample 7 2 edge
    Assert-Totals $edge 2
    $zeroLines = @($edge.Data.Tables[2].Rows | Where-Object { $_.OrderId -eq 1 -and $_.LineNumber -in 1, 2 })
    Assert-Sample ($zeroLines.Count -eq 2) 'Two zero-value edge lines'
    foreach ($zeroLine in $zeroLines) { Assert-Sample ($zeroLine.LineTotal -eq 0) 'Zero quantity/price totals' }
    $testLog.Add('PASS edge: zero quantity, zero unit price, high quantity')
    $coverage = Invoke-Sample 0 12 normal
    Assert-Totals $coverage 12
    $decisions = @($coverage.Data.Tables[2].Rows.Decision | Sort-Object -Unique)
    Assert-Sample ('Accept' -in $decisions -and 'Limited' -in $decisions -and 'Review' -in $decisions) 'All risk branches'
    $testLog.Add('PASS branch coverage: Accept, Limited, Review; 12 orders, 36 lines')
    $caught = Invoke-Sample 7 2 error
    Assert-Sample ($caught.ReturnCode -eq 1) 'Caught error RETURN code'
    Assert-Sample ($caught.Data.Tables.Count -eq 4) 'Caught error preserves all result sets'
    Assert-Sample ($caught.Data.Tables[0].Rows[0].ErrorNumber -eq 51002) 'Expected nested error number'
    Assert-Sample ($caught.Data.Tables[0].Rows[0].ErrorProcedure -eq 'SqlDebugTest.AssessRisk') 'Deepest error procedure'
    Assert-Sample ($caught.Data.Tables[1].Rows[0].Status -eq 'Failed') 'Failed order status'
    $testLog.Add('PASS error: deepest THROW 51002 reaches Run CATCH and returns partial results')
    $expectedThrow = $false
    try { $null = Invoke-Sample 7 2 error $true }
    catch {
        $sqlError = $_.Exception
        while ($sqlError.InnerException) { $sqlError = $sqlError.InnerException }
        if ($sqlError -is [System.Data.SqlClient.SqlException] -and $sqlError.Number -eq 51002) { $expectedThrow = $true }
        else { throw }
    }
    Assert-Sample $expectedThrow 'Rethrow propagates SQL exception'
    $testLog.Add('PASS Rethrow=1: SQL exception 51002 propagated')
    $testLog.Add('ALL SAMPLE TESTS PASSED')
} finally {
    $logPath = Join-Path $PSScriptRoot '..\artifacts\nested-sample-tests.log'
    [IO.File]::WriteAllLines($logPath, $testLog)
    $testLog | Write-Output
}
