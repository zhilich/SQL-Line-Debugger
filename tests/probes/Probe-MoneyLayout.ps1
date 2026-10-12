$ErrorActionPreference = 'Stop'
$probeConnection = [System.Data.SqlClient.SqlConnection]::new('Server=(localdb)\mssqllocaldb;Database=Test;Integrated Security=True;TrustServerCertificate=True;Pooling=False')
try {
    $probeConnection.Open()
    $probeCommand = $probeConnection.CreateCommand()
    $probeCommand.CommandText = @'
CREATE TABLE #MoneyProbe(Id int, Amount money, SmallAmount smallmoney);
INSERT #MoneyProbe VALUES(1,123456.7890,123456.7890),(2,-123456.7890,-123456.7890),
    (3,-922337203685477.5808,-214748.3648),(4,922337203685477.5807,214748.3647),(5,0.0001,-0.0001);
SELECT c.name,pc.leaf_offset,pc.max_length FROM tempdb.sys.system_internals_partition_columns pc
JOIN tempdb.sys.columns c ON c.object_id=OBJECT_ID('tempdb..#MoneyProbe') AND c.column_id=pc.partition_column_id
WHERE pc.partition_id IN(SELECT partition_id FROM tempdb.sys.partitions WHERE object_id=OBJECT_ID('tempdb..#MoneyProbe'));
DECLARE @p int,@f int,@sql nvarchar(200);
SELECT TOP(1) @p=allocated_page_page_id,@f=allocated_page_file_id FROM tempdb.sys.dm_db_database_page_allocations(2,OBJECT_ID('tempdb..#MoneyProbe'),0,NULL,'DETAILED') WHERE page_type=1 AND is_allocated=1;
SET @sql=N'DBCC PAGE(tempdb,'+CONVERT(nvarchar(20),@f)+N','+CONVERT(nvarchar(20),@p)+N',3) WITH TABLERESULTS,NO_INFOMSGS;';
EXEC(@sql);
'@
    $probeData = [Data.DataSet]::new()
    $probeAdapter = [System.Data.SqlClient.SqlDataAdapter]::new($probeCommand)
    $null = $probeAdapter.Fill($probeData)
    $probeData.Tables[0] | Format-Table | Out-String -Width 200 | Write-Output
    $probeData.Tables[1] | Where-Object { $_.Object -like '*Memory Dump*' -or $_.Field -in 'Id','Amount','SmallAmount' } | Format-List | Out-String -Width 200 | Write-Output
} finally { $probeConnection.Dispose() }
