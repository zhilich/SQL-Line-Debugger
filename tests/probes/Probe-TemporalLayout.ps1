$ErrorActionPreference = 'Stop'
$probeConnection = [System.Data.SqlClient.SqlConnection]::new('Server=(localdb)\mssqllocaldb;Database=Test;Integrated Security=True;TrustServerCertificate=True;Pooling=False')
try {
    $probeConnection.Open()
    $probeCommand = $probeConnection.CreateCommand()
    $probeCommand.CommandText = @'
CREATE TABLE #PhysicalProbe(Id int,Label nvarchar(40),D date,T time(7),DT2 datetime2(3),DTO datetimeoffset(7),DT datetime,SDT smalldatetime);
CREATE CLUSTERED INDEX IX_Probe ON #PhysicalProbe(Id);
INSERT #PhysicalProbe VALUES(1,N'first','20260102','03:04:05.1234567','2026-01-02T03:04:05.006','2026-01-02T03:04:05.1234567+05:30','2026-01-02T03:04:05.997','2026-01-02T03:04:00');
INSERT #PhysicalProbe(Id,Label) VALUES(1,N'second'),(2,N'unique'),(1,N'third');
SELECT c.name,pc.* FROM tempdb.sys.system_internals_partition_columns pc
LEFT JOIN tempdb.sys.columns c ON c.object_id=OBJECT_ID('tempdb..#PhysicalProbe') AND c.column_id=pc.partition_column_id
WHERE pc.partition_id IN(SELECT partition_id FROM tempdb.sys.partitions WHERE object_id=OBJECT_ID('tempdb..#PhysicalProbe') AND index_id=1);
DECLARE @p int,@f int,@sql nvarchar(200);
SELECT TOP(1) @p=allocated_page_page_id,@f=allocated_page_file_id FROM tempdb.sys.dm_db_database_page_allocations(2,OBJECT_ID('tempdb..#PhysicalProbe'),1,NULL,'DETAILED') WHERE page_type=1 AND is_allocated=1;
SET @sql=N'DBCC PAGE(tempdb,'+CONVERT(nvarchar(20),@f)+N','+CONVERT(nvarchar(20),@p)+N',3) WITH TABLERESULTS,NO_INFOMSGS;';
EXEC(@sql);
'@
    $probeData = [Data.DataSet]::new()
    $probeAdapter = [System.Data.SqlClient.SqlDataAdapter]::new($probeCommand)
    $null = $probeAdapter.Fill($probeData)
    foreach ($probeTable in $probeData.Tables) { $probeTable | Format-List | Out-String -Width 250 | Write-Output }
} finally { $probeConnection.Dispose() }
