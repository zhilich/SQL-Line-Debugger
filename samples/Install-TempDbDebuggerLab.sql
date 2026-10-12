-- Install once with sqlcmd -S "(localdb)\mssqllocaldb" -E -b -i this-file.sql
-- Dedicated fixture: refuses to overwrite any existing SqlDebugLab schema.
USE [Test];
GO
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF SCHEMA_ID(N'SqlDebugLab') IS NOT NULL
    THROW 51100, 'SqlDebugLab already exists; installation will not overwrite it.', 1;
EXEC(N'CREATE SCHEMA SqlDebugLab AUTHORIZATION dbo;');
GO

CREATE FUNCTION SqlDebugLab.AdjustScore(@Score int, @RowId int)
RETURNS int
AS
BEGIN
    DECLARE @components table(Component nvarchar(32), Amount int);
    DECLARE @result int;
    INSERT @components VALUES(N'base', @Score);
    INSERT @components VALUES(N'row adjustment', @RowId % 3);
    -- BREAK F1: inspect @components while paused inside a scalar function.
    SELECT @result = SUM(Amount) FROM @components;
    RETURN @result;
END;
GO

CREATE FUNCTION SqlDebugLab.MakeTags(@RowId int)
RETURNS @tags table(TagId int, Label nvarchar(40))
AS
BEGIN
    INSERT @tags VALUES(1, CONCAT(N'row-', @RowId));
    INSERT @tags VALUES(2, CASE WHEN @RowId % 2 = 0 THEN N'even' ELSE N'odd' END);
    -- BREAK F2: inspect the return table variable before function return.
    RETURN;
END;
GO

CREATE PROCEDURE SqlDebugLab.ScoreRow
    @RowId int, @Amount decimal(18,2), @Scenario nvarchar(20), @Score int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @components table(Component nvarchar(32), Amount int);
    DECLARE @base int = CONVERT(int, @Amount) % 17;
    DECLARE @status nvarchar(20) = N'scoring';
    INSERT @components VALUES(N'base', @base), (N'row', @RowId % 5);
    SELECT @Score = SUM(Amount) FROM @components;
    -- BREAK D1: deepest procedure frame; inspect @components and caller #Work.
    PRINT CONCAT(N'D1 ScoreRow: row=', @RowId, N'; preliminary score=', @Score);
    IF @Scenario = N'error' AND @RowId = 2
        THROW 51102, 'Intentional deepest-procedure error at row 2.', 1;
    SET @Score = SqlDebugLab.AdjustScore(@Score, @RowId);
    SET @status = CASE WHEN @Score >= 12 THEN N'review' ELSE N'accepted' END;
    INSERT #Trace(ModuleName, Marker, RowId, Detail)
    VALUES(N'ScoreRow', N'D2 score complete', @RowId, CONCAT(@status, N'; score=', @Score));
    RETURN @Score;
END;
GO

CREATE PROCEDURE SqlDebugLab.EvaluateRow
    @RowId int, @Scenario nvarchar(20), @Score int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @amount decimal(18,2), @returnCode int;
    DECLARE @tags table(TagId int, Label nvarchar(40));
    SELECT @amount = Amount FROM #Work WHERE RowId = @RowId;
    -- BREAK E1: F11 enters the multi-statement table-valued function.
    INSERT @tags SELECT TagId, Label FROM SqlDebugLab.MakeTags(@RowId);
    PRINT CONCAT(N'E2 EvaluateRow: tags created for row ', @RowId);
    EXEC @returnCode = SqlDebugLab.ScoreRow
        @RowId = @RowId, @Amount = @amount, @Scenario = @Scenario, @Score = @Score OUTPUT;
    -- BREAK E3: inspect scalar OUTPUT / RETURN values and @tags after the nested call.
    UPDATE #Work SET Score = @Score,
        State = CASE WHEN @Score >= 12 THEN N'Review' ELSE N'Accepted' END
    WHERE RowId = @RowId;
    INSERT #Trace(ModuleName, Marker, RowId, Detail)
    VALUES(N'EvaluateRow', N'E3 output values', @RowId, CONCAT(N'return=', @returnCode, N'; score=', @Score));
END;
GO

CREATE PROCEDURE SqlDebugLab.ProcessWork
    @Scenario nvarchar(20), @Processed int OUTPUT, @ScoreTotal int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @queue table(RowId int PRIMARY KEY, Done bit);
    DECLARE @RowId int, @Score int;
    SET @Processed = 0;
    SET @ScoreTotal = 0;
    INSERT @queue SELECT RowId, 0 FROM #Work;
    WHILE EXISTS(SELECT 1 FROM @queue WHERE Done = 0)
    BEGIN
        SELECT @RowId = MIN(RowId) FROM @queue WHERE Done = 0;
        -- BREAK P1: loop, repeated nested calls, inherited temp tables, local @queue.
        PRINT CONCAT(N'P1 ProcessWork: row ', @RowId);
        EXEC SqlDebugLab.EvaluateRow @RowId, @Scenario, @Score OUTPUT;
        UPDATE @queue SET Done = 1 WHERE RowId = @RowId;
        SET @Processed += 1;
        SET @ScoreTotal += @Score;
    END;
    INSERT #Trace(ModuleName, Marker, Detail)
    VALUES(N'ProcessWork', N'P2 done', CONCAT(N'processed=', @Processed, N'; total=', @ScoreTotal));
END;
GO

CREATE PROCEDURE SqlDebugLab.BuildWork @RowCount int, @Seed int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @numbers table(RowId int PRIMARY KEY);
    DECLARE @i int = 1;
    WHILE @i <= @RowCount
    BEGIN
        INSERT @numbers VALUES(@i);
        SET @i += 1;
    END;
    -- BREAK B1: @numbers is empty for @RowCount=0, otherwise holds the generated keys.
    PRINT N'B1 BuildWork: numbers ready';
    INSERT #Work(RowId, Label, Amount, State, Score)
    SELECT RowId, CONCAT(N'item-', RowId, N' / seed-', @Seed),
        CONVERT(decimal(18,2), (RowId * 7 + @Seed) % 100 + 0.25), N'Pending', NULL
    FROM @numbers;
    INSERT #Trace(ModuleName, Marker, Detail)
    VALUES(N'BuildWork', N'B2 populated', CONCAT(N'rows=', @RowCount));
END;
GO

CREATE PROCEDURE SqlDebugLab.ScopeChild @Call int, @Seen int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    -- Same table-variable identifier as the caller; these are distinct objects.
    DECLARE @rows table(Id int, ScopeName nvarchar(40));
    CREATE TABLE #ChildTemp(Id int, Note nvarchar(80));
    INSERT @rows VALUES(200 + @Call, N'child');
    INSERT #ChildTemp VALUES(@Call, N'disappears when this procedure returns');
    SELECT @Seen = COUNT(*) FROM #Work;
    INSERT #Trace(ModuleName, Marker, Detail)
    VALUES(N'ScopeChild', N'S1 inherited parent', CONCAT(N'call=', @Call, N'; #Work rows=', @Seen));
    -- BREAK S1: parent and child @rows coexist; #Work belongs to the caller.
    PRINT CONCAT(N'S1 ScopeChild: call ', @Call, N'; inspect both @rows and #ChildTemp');
    SELECT @Seen = COUNT(*) FROM @rows;
END;
GO

CREATE PROCEDURE SqlDebugLab.Recurse @Depth int, @Seen int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @rows table(Depth int, Label nvarchar(40));
    DECLARE @childSeen int = 0;
    INSERT @rows VALUES(@Depth, CONCAT(N'recursive frame ', @Depth));
    -- BREAK R1: each recursive call has its own @rows and scalar @Depth.
    PRINT CONCAT(N'R1 Recurse: depth=', @Depth);
    IF @Depth > 0
    BEGIN
        DECLARE @nextDepth int = @Depth - 1;
        EXEC SqlDebugLab.Recurse @nextDepth, @childSeen OUTPUT;
    END;
    SELECT @Seen = COUNT(*) + @childSeen FROM @rows;
    INSERT #Trace(ModuleName, Marker, Detail)
    VALUES(N'Recurse', N'R2 unwind', CONCAT(N'depth=', @Depth, N'; seen=', @Seen));
    RETURN @Depth;
END;
GO

CREATE PROCEDURE SqlDebugLab.Lifecycle
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #Lifecycle__(Id int, Label nvarchar(80));
    INSERT #Lifecycle__ VALUES(1, N'alpha'), (1, N'alpha'), (2, NULL), (3, N'delete me');
    -- BREAK L1: duplicate rows and NULL; open and close a snapshot before continuing.
    PRINT N'L1 Lifecycle: original rows';
    UPDATE #Lifecycle__ SET Label = N'changed' WHERE Id = 1;
    DELETE #Lifecycle__ WHERE Id = 3;
    -- BREAK L2: a new inspection shows 3 rows with changed labels.
    PRINT N'L2 Lifecycle: updated and deleted rows';
    TRUNCATE TABLE #Lifecycle__;
    -- BREAK L3: zero rows, but the snapshot must retain two columns.
    PRINT N'L3 Lifecycle: truncated, inspect empty schema';
    DROP TABLE #Lifecycle__;
    -- BREAK L4: #Lifecycle__ disappears from the list.
    PRINT N'L4 Lifecycle: dropped';
    EXEC sys.sp_executesql N'
        CREATE TABLE #Recreated(Id int, Generation nvarchar(40));
        INSERT #Recreated VALUES(10, N''first'');
        PRINT N''L5 Lifecycle: first #Recreated'';
        DROP TABLE #Recreated;
        EXEC sys.sp_executesql N''
            CREATE TABLE #Recreated(Id int, Generation nvarchar(40));
            INSERT #Recreated VALUES(20, N''''second'''');
            PRINT N''''L6 Lifecycle: replacement #Recreated in a new dynamic scope'''';
            SELECT COUNT(*) AS RecreatedCount FROM #Recreated;
        '';
    ';
    INSERT #Snapshots(ModuleName, [Checkpoint], [RowCount], Note)
    VALUES(N'Lifecycle', N'L7 scope complete', 0, N'Trailing underscores, DML, truncate, drop, recreate');
END;
GO

CREATE PROCEDURE SqlDebugLab.TransactionLab
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @before decimal(18,2), @after decimal(18,2);
    SELECT @before = SUM(Amount) FROM #Work;
    BEGIN TRY
        BEGIN TRANSACTION;
        CREATE TABLE #Uncommitted(Id int PRIMARY KEY, Note nvarchar(80));
        INSERT #Uncommitted VALUES(1, N'uncommitted insert'), (2, N'rollback removes this table');
        UPDATE #Work SET Amount = Amount + 100;
        -- BREAK T1: inspect #Uncommitted and modified #Work from the other connection.
        PRINT N'T1 TransactionLab: uncommitted rows';
        ROLLBACK TRANSACTION;
        -- BREAK T2: table created inside the transaction disappears; #Work is restored.
        PRINT N'T2 TransactionLab: rollback complete';
        SELECT @after = SUM(Amount) FROM #Work;
        IF ISNULL(@before, 0) <> ISNULL(@after, 0)
            THROW 51103, 'Fixture rollback did not restore #Work.', 1;
        BEGIN TRANSACTION;
        CREATE TABLE #Committed(Id int, Note nvarchar(80));
        INSERT #Committed VALUES(3, N'committed, still scoped to this procedure');
        COMMIT TRANSACTION;
        PRINT N'T3 TransactionLab: committed rows';
        INSERT #Snapshots(ModuleName, [Checkpoint], [RowCount], Note)
        VALUES(N'TransactionLab', N'T3 commit and rollback', 1, N'#Work amounts restored');
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE SqlDebugLab.TypesLab @IncludeUnsupported bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #Types(Id int PRIMARY KEY, SmallValue smallint, TinyValue tinyint,
        BigValue bigint, Flag bit, Amount decimal(18,4), RealValue real, FloatValue float,
        GuidValue uniqueidentifier, FixedText nchar(8), AnsiText varchar(40),
        UnicodeText nvarchar(max), Bytes varbinary(max), WideValue decimal(38,0));
    INSERT #Types VALUES(1, -123, 255, 9223372036854775806, 1, -123456.7890, 1.25, -2.5,
        '00112233-4455-6677-8899-AABBCCDDEEFF', N'fixed', 'ASCII text',
        REPLICATE(CONVERT(nvarchar(max), NCHAR(937)), 10000), 0x001122FF,
        99999999999999999999999999999999999999);
    INSERT #Types(Id) VALUES(2);
    DECLARE @binary table(Id int, FixedBytes binary(4), ShortText nvarchar(80));
    INSERT @binary VALUES(1, 0x00FF1122, N'Unicode: ' + NCHAR(937) + N' ' + NCHAR(28450));
    -- BREAK Y1: numeric widths, NULL, GUID, precision 38, binary and off-row Unicode LOB.
    PRINT N'Y1 TypesLab: supported physical layouts';
    CREATE TABLE #ManyRows(Id int PRIMARY KEY, Payload nvarchar(120));
    DECLARE @i int = 1;
    WHILE @i <= 200
    BEGIN
        INSERT #ManyRows VALUES(@i, REPLICATE(N'x', 100));
        SET @i += 1;
    END;
    PRINT N'Y2 TypesLab: 200 rows across multiple pages';
    IF @IncludeUnsupported = 1
    BEGIN
        CREATE TABLE #UnsupportedDate(Id int, Value datetime2(3));
        INSERT #UnsupportedDate VALUES(1, CONVERT(datetime2(3), '2026-01-02T03:04:05.006'));
        -- BREAK Y3: Inspect should show the current reader's unsupported-type error.
        PRINT N'Y3 TypesLab: deliberately unsupported datetime2';
        CREATE TABLE #NonUniqueClustered(Id int, Label nvarchar(40));
        CREATE CLUSTERED INDEX IX_LabNonUnique ON #NonUniqueClustered(Id);
        INSERT #NonUniqueClustered VALUES(1, N'first'), (1, N'second');
        -- BREAK Y4: hidden uniquifier is deliberately outside the current reader's support.
        PRINT N'Y4 TypesLab: deliberately unsupported hidden uniquifier';
    END;
    INSERT #Snapshots(ModuleName, [Checkpoint], [RowCount], Note)
    SELECT N'TypesLab', N'Y2 multiple pages', COUNT(*), N'Inspect types before procedure return' FROM #ManyRows;
END;
GO

CREATE PROCEDURE SqlDebugLab.DynamicLab
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE #DynamicShared(Id int, Label nvarchar(60));
    INSERT #DynamicShared VALUES(1, N'created by static procedure');
    DECLARE @sql nvarchar(max) = N'
        DECLARE @dynamicRows table(Id int, Label nvarchar(60));
        INSERT @dynamicRows VALUES(20, N''local to dynamic batch'');
        CREATE TABLE #DynamicLocal(Id int, Label nvarchar(60));
        INSERT #DynamicLocal VALUES(30, N''disappears after dynamic batch'');
        INSERT #DynamicShared VALUES(@Id, N''inserted by sp_executesql'');
        PRINT N''Q1 DynamicLab: inspect dynamic and inherited scopes'';
        SELECT COUNT(*) AS DynamicVariableRows FROM @dynamicRows;
        SELECT COUNT(*) AS DynamicTempRows FROM #DynamicLocal;
    ';
    -- BREAK Q0: F11 enters a parameterized dynamic SQL batch.
    EXEC sys.sp_executesql @sql, N'@Id int', @Id = 2;
    -- BREAK Q2: only the procedure's #DynamicShared remains from this branch.
    PRINT N'Q2 DynamicLab: dynamic batch returned';
    INSERT #Snapshots(ModuleName, [Checkpoint], [RowCount], Note)
    SELECT N'DynamicLab', N'Q2 inherited temp', COUNT(*), N'Dynamic-local objects have exited scope' FROM #DynamicShared;
END;
GO

CREATE PROCEDURE SqlDebugLab.Run
    @RowCount int = 3, @Seed int = 7, @Scenario nvarchar(20) = N'normal',
    @RecursionDepth int = 3, @IncludeUnsupported bit = 0, @Rethrow bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @@TRANCOUNT <> 0
        THROW 51100, 'Run the fixture outside an existing transaction.', 1;
    IF @RowCount IS NULL OR @RowCount < 0 OR @RowCount > 1000
        THROW 51100, '@RowCount must be between 0 and 1000.', 1;
    IF @Seed IS NULL OR @Seed < 0 OR @Seed > 1000000
        THROW 51100, '@Seed must be between 0 and 1000000.', 1;
    IF @RecursionDepth IS NULL OR @RecursionDepth < 0 OR @RecursionDepth > 8
        THROW 51100, '@RecursionDepth must be between 0 and 8.', 1;
    IF @Scenario IS NULL OR @Scenario NOT IN(N'normal', N'error')
        THROW 51100, '@Scenario must be normal or error.', 1;

    CREATE TABLE #Work(RowId int PRIMARY KEY, Label nvarchar(80), Amount decimal(18,2),
        State nvarchar(20), Score int);
    CREATE TABLE #Trace(Sequence int IDENTITY PRIMARY KEY, ModuleName nvarchar(40),
        Marker nvarchar(80), RowId int NULL, Detail nvarchar(160));
    CREATE TABLE #Snapshots(Sequence int IDENTITY PRIMARY KEY, ModuleName nvarchar(40),
        [Checkpoint] nvarchar(80), [RowCount] int, Note nvarchar(160));
    DECLARE @Left table(Id int, Label nvarchar(80));
    DECLARE @Right table(Id int, Label nvarchar(80));
    DECLARE @rows table(Id int, ScopeName nvarchar(40));
    DECLARE @Joined int, @LeftCount int, @RightCount int, @Seen int,
        @Processed int = 0, @ScoreTotal int = 0, @RecursiveSeen int = 0,
        @ErrorNumber int = NULL, @ErrorLine int = NULL, @ErrorProcedure nvarchar(128) = NULL,
        @ErrorMessage nvarchar(2048) = NULL;

    -- First use references two table variables: mapping is initially ambiguous.
    SELECT @Joined = COUNT(*) FROM @Left l CROSS JOIN @Right r;
    -- BREAK A1: @Left/@Right may appear with internal names; both are empty.
    PRINT N'A1 Run: ambiguous pair before any single-variable use';
    SELECT @LeftCount = COUNT(*) FROM @Left;
    -- BREAK A2: a single-variable use resolves the ambiguous pair progressively.
    PRINT N'A2 Run: after single-variable use';
    INSERT @Left VALUES(1, N'alpha'), (1, N'alpha'), (2, NULL);
    INSERT @Right VALUES(10, N'right');
    INSERT @rows VALUES(100, N'root caller');
    SELECT @RightCount = COUNT(*) FROM @Right;
    -- BREAK A3: three left rows (duplicates + NULL), one right row, root @rows=100.
    PRINT N'A3 Run: root variables populated';

    BEGIN TRY
        EXEC SqlDebugLab.BuildWork @RowCount, @Seed;
        EXEC SqlDebugLab.ProcessWork @Scenario, @Processed OUTPUT, @ScoreTotal OUTPUT;
        EXEC SqlDebugLab.ScopeChild @Call = 1, @Seen = @Seen OUTPUT;
        -- BREAK S2: child objects have left scope; root @rows remains.
        PRINT N'S2 Run: first child returned';
        EXEC SqlDebugLab.ScopeChild @Call = 2, @Seen = @Seen OUTPUT;
        PRINT N'S3 Run: repeated child returned, exercise cached object reuse';
        EXEC SqlDebugLab.Recurse @RecursionDepth, @RecursiveSeen OUTPUT;
        EXEC SqlDebugLab.Lifecycle;
        EXEC SqlDebugLab.TransactionLab;
        EXEC SqlDebugLab.TypesLab @IncludeUnsupported;
        EXEC SqlDebugLab.DynamicLab;
    END TRY
    BEGIN CATCH
        SELECT @ErrorNumber = ERROR_NUMBER(), @ErrorLine = ERROR_LINE(),
            @ErrorProcedure = ERROR_PROCEDURE(), @ErrorMessage = ERROR_MESSAGE();
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        INSERT #Trace(ModuleName, Marker, Detail)
        VALUES(N'Run', N'X1 caught error', LEFT(@ErrorMessage, 160));
        -- BREAK X1: error propagation, partial data, and caller locals.
        PRINT CONCAT(N'X1 Run CATCH: ', @ErrorNumber, N' / ', @ErrorProcedure, N': ', @ErrorMessage);
        IF @Rethrow = 1 THROW;
    END CATCH;

    -- BREAK Z1: all child scopes ended; only root-owned objects should remain.
    PRINT N'Z1 Run: final root snapshots and result sets';
    SELECT @Scenario AS Scenario, @RowCount AS RequestedRows, @Processed AS Processed,
        @ScoreTotal AS ScoreTotal, @RecursiveSeen AS RecursiveFrames,
        @ErrorNumber AS ErrorNumber, @ErrorProcedure AS ErrorProcedure,
        @ErrorLine AS ErrorLine, @ErrorMessage AS ErrorMessage;
    SELECT RowId, Label, Amount, State, Score FROM #Work ORDER BY RowId;
    SELECT Sequence, ModuleName, Marker, RowId, Detail FROM #Trace ORDER BY Sequence;
    SELECT Sequence, ModuleName, [Checkpoint], [RowCount], Note FROM #Snapshots ORDER BY Sequence;
    SELECT Id, Label FROM @Left ORDER BY Id;
    SELECT Id, Label FROM @Right ORDER BY Id;
    SELECT Id, ScopeName FROM @rows;
    RETURN CASE WHEN @ErrorNumber IS NULL THEN 0 ELSE 1 END;
END;
GO

COMMIT TRANSACTION;
SELECT s.name AS SchemaName, o.name AS ModuleName, o.type_desc AS ModuleType,
    DATALENGTH(m.definition) AS DefinitionBytes
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE s.name = N'SqlDebugLab'
ORDER BY o.type_desc, o.name;
GO
