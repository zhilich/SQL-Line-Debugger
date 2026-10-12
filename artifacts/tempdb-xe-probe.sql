SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
DECLARE @xe sysname = N'SqlLineDebugger_Probe_' + CONVERT(nvarchar(10), @@SPID), @sql nvarchar(max);
SET @sql=N'CREATE EVENT SESSION '+QUOTENAME(@xe)+N' ON SERVER
ADD EVENT sqlserver.lock_acquired(ACTION(package0.event_sequence,sqlserver.session_id) WHERE(database_id=2 AND sqlserver.session_id='+CONVERT(nvarchar(10),@@SPID)+N')),
ADD EVENT sqlserver.sp_statement_starting(SET collect_statement=1 ACTION(package0.event_sequence,sqlserver.session_id)),
ADD EVENT sqlserver.sql_statement_starting(SET collect_statement=1 ACTION(package0.event_sequence,sqlserver.session_id))
ADD TARGET package0.ring_buffer(SET max_memory=1024) WITH(MAX_DISPATCH_LATENCY=1 SECONDS);
ALTER EVENT SESSION '+QUOTENAME(@xe)+N' ON SERVER STATE=START;';
EXEC(@sql);
DECLARE @a table(Id int,Value nvarchar(30));
DECLARE @b table(Id int,Value nvarchar(30));
CREATE TABLE #XeProbe(Id int,Value nvarchar(30));
INSERT @a VALUES(1,N'alpha');
INSERT @b VALUES(2,N'beta');
INSERT #XeProbe VALUES(3,N'temp');
SELECT a.Id FROM @a a JOIN @b b ON a.Id=b.Id;
SELECT a.Id FROM @a a;
SELECT CAST(name AS nvarchar(128)) name,object_id FROM tempdb.sys.tables WHERE name LIKE '#%';
DECLARE @xml xml;
SELECT @xml=CAST(t.target_data AS xml) FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address WHERE s.name=@xe;
SELECT e.n.value('@name','nvarchar(40)') event_name,
 e.n.value('(action[@name="event_sequence"]/value)[1]','bigint') sequence,
 e.n.value('(data[@name="object_id"]/value)[1]','nvarchar(30)') object_id,
 e.n.value('(data[@name="associated_object_id"]/value)[1]','nvarchar(30)') associated,
 e.n.value('(data[@name="resource_type"]/text)[1]','nvarchar(30)') resource_type,
 e.n.value('(data[@name="statement"]/value)[1]','nvarchar(500)') statement
FROM @xml.nodes('/RingBufferTarget/event') e(n) ORDER BY sequence;
SET @sql=N'DROP EVENT SESSION '+QUOTENAME(@xe)+N' ON SERVER;'; EXEC(@sql);
