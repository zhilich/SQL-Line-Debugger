-- Inputs: @table_name nvarchar(776), @max_pages int, @global_name nvarchar(128) (NULL for inspection).
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
BEGIN
    -- Experimental SQL Server 2019 tempdb page reader. No SQL runs in the owning session.
    -- DBCC PAGE and system_internals_* are undocumented and version dependent.
    -- Run from a separate, autocommit connection while the debuggee is paused.
    SET NOCOUNT ON;
    IF @global_name IS NOT NULL AND (LEFT(@global_name,2)<>N'##' OR LEN(@global_name)<=2 OR DATALENGTH(@global_name)>256)
        THROW 51000, 'Supply a global temporary table name beginning with ##, at most 128 characters.', 1;
    IF @@TRANCOUNT <> 0 OR (@@OPTIONS & 2) <> 0
        THROW 51000, 'Run the inspector in autocommit, with IMPLICIT_TRANSACTIONS OFF.', 1;
    IF @max_pages IS NULL OR @max_pages < 1 OR @max_pages > 10000
        THROW 51000, '@max_pages must be between 1 and 10000.', 1;
    IF @table_name IS NULL OR @table_name=N''
        THROW 51000, 'Supply the full internal table name from tempdb.sys.tables.name.', 1;
    -- Accept a full 128-character internal name with an optional tempdb.. prefix.
    -- A sysname parameter would truncate the suffix before normalization.
    IF LEFT(@table_name,8)=N'tempdb..' SET @table_name=SUBSTRING(@table_name,9,776);
    IF LEFT(@table_name,1)=N'[' AND RIGHT(@table_name,1)=N']'
        SET @table_name=REPLACE(SUBSTRING(@table_name,2,LEN(@table_name)-2),N']]',N']');
    IF DATALENGTH(@table_name)>256
        THROW 51000, 'The unqualified internal table name must be at most 128 characters.', 1;
    IF @table_name IS NOT NULL AND LEFT(@table_name,1) <> N'#'
        THROW 51000, '@table_name must be a temporary table name beginning with #.', 1;

    -- These SET options apply only to the dedicated inspection connection.
    SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
    SET LOCK_TIMEOUT 1000;
    BEGIN TRY
        DECLARE @object_id int, @matches int, @partition bigint, @hobt bigint, @compression int,
            @allocation bigint, @iam_allocation bigint, @iam binary(6), @file int, @page int,
            @sql nvarchar(max), @message nvarchar(2048);
        SELECT t.object_id INTO #spr_matches
        FROM tempdb.sys.tables t WITH(NOLOCK)
        WHERE t.name = @table_name;
        SELECT @matches = COUNT(*), @object_id = MIN(object_id) FROM #spr_matches;
        IF @matches <> 1
        BEGIN
            SET @message = N'Found '+CONVERT(nvarchar(20),@matches)+N' matching tables; pass the exact internal name from tempdb.sys.tables.name.';
            THROW 51000, @message, 1;
        END;
        SELECT partition_id, hobt_id, data_compression INTO #spr_partitions
        FROM tempdb.sys.partitions WITH(NOLOCK) WHERE object_id=@object_id AND index_id IN (0,1);
        IF (SELECT COUNT(*) FROM #spr_partitions) <> 1 OR EXISTS(SELECT 1 FROM #spr_partitions WHERE data_compression<>0)
            THROW 51000, 'Only a single uncompressed rowstore partition is supported.', 1;
        SELECT @partition=partition_id, @hobt=hobt_id FROM #spr_partitions;

        SELECT c.name, c.column_id, c.system_type_id, c.max_length, c.precision, c.scale, c.collation_name,
            c.is_sparse, c.is_computed, c.encryption_type,
            pc.leaf_offset, pc.leaf_null_bit, pc.leaf_bit_position, pc.is_dropped, pc.is_uniqueifier
        INTO #spr_columns
        FROM tempdb.sys.columns c WITH(NOLOCK)
        JOIN tempdb.sys.system_internals_partition_columns pc WITH(NOLOCK)
          ON pc.partition_id=@partition AND pc.partition_column_id=c.column_id
        WHERE c.object_id=@object_id;
        IF NOT EXISTS(SELECT 1 FROM #spr_columns) OR
            (SELECT COUNT(*) FROM #spr_columns) <>
            (SELECT COUNT(*) FROM tempdb.sys.system_internals_partition_columns WITH(NOLOCK) WHERE partition_id=@partition AND is_uniqueifier=0)
            THROW 51000, 'Hidden/dropped columns or physical layout changes are unsupported.', 1;
        -- A non-unique clustered index adds an internal four-byte uniquifier.
        -- User-column offsets/null bits already account for it; do not project it.
        IF EXISTS(SELECT 1 FROM tempdb.sys.system_internals_partition_columns WITH(NOLOCK)
            WHERE partition_id=@partition AND is_uniqueifier=1 AND
                (system_type_id<>56 OR max_length<>4 OR is_dropped=1))
            THROW 51000, 'Unsupported clustered uniquifier layout.', 1;
        IF EXISTS(SELECT 1 FROM #spr_columns WHERE is_sparse=1 OR is_computed=1 OR encryption_type IS NOT NULL
            OR is_dropped=1 OR is_uniqueifier=1
            OR system_type_id NOT IN (48,52,56,127,104,59,62,60,122,106,108,231,239,167,175,165,173,36,40,41,42,43,58,61))
        BEGIN
            SELECT TOP(1) @message=N'Unsupported column '+QUOTENAME(name)+N' ('+TYPE_NAME(system_type_id)+N') or physical layout (sparse/computed/encrypted/dropped).'
            FROM #spr_columns WHERE is_sparse=1 OR is_computed=1 OR encryption_type IS NOT NULL
                OR is_dropped=1 OR is_uniqueifier=1
                OR system_type_id NOT IN (48,52,56,127,104,59,62,60,122,106,108,231,239,167,175,165,173,36,40,41,42,43,58,61)
            ORDER BY column_id;
            THROW 51000, @message, 1;
        END;
        SELECT type,allocation_unit_id,first_iam_page INTO #spr_allocations
        FROM tempdb.sys.system_internals_allocation_units WITH(NOLOCK)
        WHERE (container_id=@hobt AND type=1) OR (container_id=@partition AND type=2);
        IF (SELECT COUNT(*) FROM #spr_allocations WHERE type=1) <> 1
            THROW 51000, 'Expected one in-row allocation unit.', 1;
        SELECT @allocation=allocation_unit_id FROM #spr_allocations WHERE type=1;
        SELECT allocation_unit_id,first_iam_page INTO #spr_iam_queue FROM #spr_allocations;

        CREATE TABLE #spr_dump(ParentObject nvarchar(255), [Object] nvarchar(255), Field nvarchar(255), [VALUE] nvarchar(max));
        CREATE TABLE #spr_pages(file_id int NOT NULL, page_id int NOT NULL, PRIMARY KEY(file_id,page_id));
        CREATE TABLE #spr_visited(file_id int NOT NULL, page_id int NOT NULL, PRIMARY KEY(file_id,page_id));
        CREATE TABLE #spr_addresses(id int IDENTITY PRIMARY KEY, address_text nvarchar(255), is_extent bit);
        DECLARE @address nvarchar(255), @is_extent bit, @address_id int,
            @f int, @p int, @end int, @colon int, @close int, @second int, @f2 int;
        WHILE EXISTS(SELECT 1 FROM #spr_iam_queue)
        BEGIN
        SELECT TOP(1) @iam=COALESCE(first_iam_page,0x000000000000),@iam_allocation=allocation_unit_id FROM #spr_iam_queue ORDER BY allocation_unit_id;
        DELETE #spr_iam_queue WHERE allocation_unit_id=@iam_allocation;
        SELECT @page=CONVERT(int,SUBSTRING(@iam,4,1)+SUBSTRING(@iam,3,1)+SUBSTRING(@iam,2,1)+SUBSTRING(@iam,1,1)),
            @file=CONVERT(int,SUBSTRING(@iam,6,1)+SUBSTRING(@iam,5,1));
        WHILE @page<>0
        BEGIN
            IF EXISTS(SELECT 1 FROM #spr_visited WHERE file_id=@file AND page_id=@page)
                THROW 51000, 'IAM chain cycle detected.', 1;
            IF (SELECT COUNT(*) FROM #spr_visited)>=100
                THROW 51000, 'IAM chain safety limit exceeded.', 1;
            INSERT #spr_visited VALUES(@file,@page);
            TRUNCATE TABLE #spr_dump;
            SET @sql=N'DBCC PAGE(tempdb,'+CONVERT(nvarchar(20),@file)+N','+CONVERT(nvarchar(20),@page)+N',3) WITH TABLERESULTS,NO_INFOMSGS;';
            INSERT #spr_dump EXEC(@sql);
            TRUNCATE TABLE #spr_addresses;
            INSERT #spr_addresses(address_text,is_extent)
            SELECT [VALUE],0 FROM #spr_dump WHERE [Object] LIKE '%IAM: Single Page Allocations%'
            UNION ALL
            SELECT Field,1 FROM #spr_dump WHERE [Object] LIKE '%IAM: Extent Alloc Status%' AND LTRIM(RTRIM([VALUE]))=N'ALLOCATED';
            WHILE EXISTS(SELECT 1 FROM #spr_addresses)
            BEGIN
                SELECT TOP(1) @address_id=id,@address=LTRIM(RTRIM(address_text)),@is_extent=is_extent FROM #spr_addresses ORDER BY id;
                SELECT @colon=CHARINDEX(':',@address),@close=CHARINDEX(')',@address);
                IF LEFT(@address,1)<>'(' OR @colon<3 OR @close<=@colon+1
                    THROW 51000, 'Unexpected IAM allocation address.', 1;
                SELECT @f=TRY_CONVERT(int,SUBSTRING(@address,2,@colon-2)),
                    @p=TRY_CONVERT(int,SUBSTRING(@address,@colon+1,@close-@colon-1)),
                    @second=CHARINDEX('(',@address,@close+1);
                SET @end=@p;
                IF @second>0
                BEGIN
                    SET @address=SUBSTRING(@address,@second,255);
                    SELECT @colon=CHARINDEX(':',@address),@close=CHARINDEX(')',@address);
                    IF @colon<3 OR @close<=@colon+1 THROW 51000, 'Unexpected IAM extent range.', 1;
                    SELECT @f2=TRY_CONVERT(int,SUBSTRING(@address,2,@colon-2)),
                        @end=TRY_CONVERT(int,SUBSTRING(@address,@colon+1,@close-@colon-1));
                    IF @f2<>@f THROW 51000, 'IAM range crosses files.', 1;
                END;
                IF @f IS NULL OR @p IS NULL OR @end IS NULL OR @p<0 OR @end<@p OR (@p>0 AND @f<1)
                    THROW 51000, 'Invalid IAM allocation range.', 1;
                IF @is_extent=1 SET @end=@end+7;
                WHILE @p>0 AND @p<=@end
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM #spr_pages WHERE file_id=@f AND page_id=@p) INSERT #spr_pages VALUES(@f,@p);
                    IF (SELECT COUNT(*) FROM #spr_pages)>@max_pages
                        THROW 51000, 'Allocation exceeds @max_pages; increase it explicitly.', 1;
                    SET @p=@p+1;
                END;
                DELETE #spr_addresses WHERE id=@address_id;
            END;
            IF (SELECT COUNT(*) FROM #spr_dump WHERE Field='m_nextPage')<>1
                THROW 51000, 'Missing IAM next-page pointer.', 1;
            SELECT @address=[VALUE] FROM #spr_dump WHERE Field='m_nextPage';
            SELECT @colon=CHARINDEX(':',@address),@close=CHARINDEX(')',@address);
            IF LEFT(@address,1)<>'(' OR @colon<3 OR @close<=@colon+1 THROW 51000, 'Invalid IAM next-page pointer.', 1;
            SELECT @file=TRY_CONVERT(int,SUBSTRING(@address,2,@colon-2)),
                @page=TRY_CONVERT(int,SUBSTRING(@address,@colon+1,@close-@colon-1));
            IF @file IS NULL OR @page IS NULL OR @page<0 OR (@page>0 AND @file<1)
                THROW 51000, 'Invalid IAM next-page pointer.', 1;
        END;
        END;

        CREATE TABLE #spr_chunks(address binary(8) PRIMARY KEY, bytes varbinary(20));
        CREATE TABLE #spr_values(record_id bigint NOT NULL, column_id int NOT NULL, text_value nvarchar(max), PRIMARY KEY(record_id,column_id));
        DECLARE @bytes varbinary(max),
            @base_address bigint, @page_allocation bigint, @slots int, @slot int, @offset int, @status int,
            @null_offset int, @count int, @null_bytes int, @variable_header int, @variable_count int,
            @column_id int, @name sysname, @type int, @length int, @precision int, @scale int,
            @collation sysname, @leaf_offset int, @null_bit int, @bit_position int,
            @start int, @ordinal int, @value_end int, @raw varbinary(max), @reversed varbinary(8000), @offrow bit,
            @value nvarchar(max), @i int, @n decimal(38,0), @u bigint, @fraction bigint,
            @exponent int, @sign int, @float float, @record_id bigint=0, @type_sql nvarchar(100), @projection nvarchar(max)=N'';
        DECLARE @date_days int, @time_units bigint, @time_factor bigint, @time_length int,
            @zone_minutes int, @temporal datetime2(7), @legacy datetime;
        DECLARE column_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT column_id,name,system_type_id,max_length,precision,scale,collation_name,leaf_offset,leaf_null_bit-1,leaf_bit_position
            FROM #spr_columns ORDER BY column_id;
        -- Binary -> varchar must use the destination column's code page directly.
        -- Casting under the database default and then applying COLLATE transcodes the bytes.
        CREATE TABLE #spr_varchars(dummy bit);
        OPEN column_cursor;
        FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
        WHILE @@FETCH_STATUS=0
        BEGIN
            IF @type IN(167,175)
            BEGIN
                SET @sql=N'ALTER TABLE #spr_varchars ADD [c'+CONVERT(nvarchar(20),@column_id)+N'] varchar(max) COLLATE '+@collation+N' NULL;';
                EXEC(@sql);
                IF COL_LENGTH('tempdb..#spr_varchars','dummy') IS NOT NULL ALTER TABLE #spr_varchars DROP COLUMN dummy;
            END;
            FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
        END;
        CLOSE column_cursor;
        CREATE TABLE #spr_pagecache(file_id int,page_id int,allocation_unit_id bigint,page_type int,bytes varbinary(max),PRIMARY KEY(file_id,page_id));
        CREATE TABLE #spr_lob_work(id int IDENTITY PRIMARY KEY,file_id int,page_id int,slot_id int,start_offset bigint,end_offset bigint,depth int);
        CREATE TABLE #spr_lob_seen(file_id int,page_id int,slot_id int,PRIMARY KEY(file_id,page_id,slot_id));
        CREATE TABLE #spr_lob_pieces(start_offset bigint PRIMARY KEY,bytes varbinary(max));
        DECLARE @lob_pointer varbinary(8000),@lob_blob binary(8),@lob_total bigint,@lob_id int,@lob_file int,@lob_page int,@lob_slot int,
            @lob_start bigint,@lob_end bigint,@lob_depth int,@lob_bytes varbinary(max),@lob_record varbinary(max),
            @lob_offset int,@lob_slots int,@lob_length int,@lob_type int,@lob_links int,@lob_max_links int,
            @lob_link_size int,@lob_link_start int,@lob_at int,@lob_prev bigint,@lob_next bigint,@lob_base bigint,@lob_last bigint,
            @lob_piece varbinary(max),@lob_piece_length int,@lob_level int;
        WHILE EXISTS(SELECT 1 FROM #spr_pages)
        BEGIN
            SELECT TOP(1) @file=file_id,@page=page_id FROM #spr_pages ORDER BY file_id,page_id;
            TRUNCATE TABLE #spr_dump;
            SET @sql=N'DBCC PAGE(tempdb,'+CONVERT(nvarchar(20),@file)+N','+CONVERT(nvarchar(20),@page)+N',2) WITH TABLERESULTS,NO_INFOMSGS;';
            INSERT #spr_dump EXEC(@sql);
            TRUNCATE TABLE #spr_chunks;
            INSERT #spr_chunks(address,bytes)
            SELECT CONVERT(binary(8),RIGHT(REPLICATE('0',16)+LEFT([VALUE],CHARINDEX(':',[VALUE])-1),16),2),
                CONVERT(varbinary(20),REPLACE(LEFT(LTRIM(SUBSTRING([VALUE],CHARINDEX(':',[VALUE])+1,200)),44),' ',''),2)
            FROM #spr_dump WHERE ParentObject='DATA:' AND [Object] LIKE 'Memory Dump%';
            IF NOT EXISTS(SELECT 1 FROM #spr_chunks) THROW 51000, 'Missing raw page dump.', 1;
            SELECT @base_address=CONVERT(bigint,MIN(address)) FROM #spr_chunks;
            IF EXISTS(SELECT 1 FROM (SELECT address,bytes,ROW_NUMBER() OVER(ORDER BY address)-1 AS ordinal FROM #spr_chunks) c
                WHERE CONVERT(bigint,address)-@base_address<>ordinal*20 OR DATALENGTH(bytes)<>CASE WHEN ordinal=409 THEN 12 ELSE 20 END)
                THROW 51000, 'Noncontiguous raw page dump.', 1;
            SELECT @bytes=CONVERT(varbinary(max),(SELECT CONVERT(varchar(40),bytes,2) AS [text()] FROM #spr_chunks ORDER BY address FOR XML PATH(''),TYPE).value('.','varchar(max)'),2);
            IF DATALENGTH(@bytes)<>8192 THROW 51000, 'Incomplete raw page dump.', 1;
            IF (SELECT COUNT(*) FROM #spr_dump WHERE [Object]='Allocation Status' AND Field LIKE 'PFS %')<>1
                THROW 51000, 'Missing PFS allocation status.', 1;
            SELECT @page_allocation=CONVERT(bigint,SUBSTRING(@bytes,8,1)+SUBSTRING(@bytes,7,1))*281474976710656
                +CONVERT(bigint,SUBSTRING(@bytes,28,1)+SUBSTRING(@bytes,27,1)+SUBSTRING(@bytes,26,1)+SUBSTRING(@bytes,25,1))*65536;
            IF EXISTS(SELECT 1 FROM #spr_allocations WHERE allocation_unit_id=@page_allocation)
                AND NOT EXISTS(SELECT 1 FROM #spr_dump WHERE [Object]='Allocation Status' AND Field LIKE 'PFS %' AND [VALUE] LIKE '%NOT ALLOCATED%')
                INSERT #spr_pagecache VALUES(@file,@page,@page_allocation,CONVERT(int,SUBSTRING(@bytes,2,1)),@bytes);
            DELETE #spr_pages WHERE file_id=@file AND page_id=@page;
        END;
        INSERT #spr_pages SELECT file_id,page_id FROM #spr_pagecache WHERE allocation_unit_id=@allocation AND page_type=1;
        WHILE EXISTS(SELECT 1 FROM #spr_pages)
        BEGIN
            SELECT TOP(1) @file=file_id,@page=page_id FROM #spr_pages ORDER BY file_id,page_id;
            SELECT @bytes=bytes FROM #spr_pagecache WHERE file_id=@file AND page_id=@page;
            IF CONVERT(int,SUBSTRING(@bytes,2,1))=1
            BEGIN
                BEGIN
                    SET @slots=CONVERT(int,SUBSTRING(@bytes,24,1)+SUBSTRING(@bytes,23,1));
                    IF @slots>4048 THROW 51000, 'Invalid page slot count.', 1;
                    SET @slot=0;
                    WHILE @slot<@slots
                    BEGIN
                        SET @offset=CONVERT(int,SUBSTRING(@bytes,8192-2*@slot,1)+SUBSTRING(@bytes,8191-2*@slot,1));
                        SET @slot=@slot+1;
                        IF @offset=0 CONTINUE;
                        IF @offset<96 OR @offset>8188-2*@slots THROW 51000, 'Invalid record offset.', 1;
                        SET @status=CONVERT(int,SUBSTRING(@bytes,@offset+1,1));
                        IF (@status & 14)/2=6 CONTINUE; -- deleted ghost
                        IF (@status & 14)/2<>0 OR (@status & 16)=0
                            THROW 51000, 'Unsupported record format/type (forwarded/compressed records need another decoder).', 1;
                        SELECT @null_offset=CONVERT(int,SUBSTRING(@bytes,@offset+4,1)+SUBSTRING(@bytes,@offset+3,1));
                        SET @count=CONVERT(int,SUBSTRING(@bytes,@offset+@null_offset+2,1)+SUBSTRING(@bytes,@offset+@null_offset+1,1));
                        IF @count<1 OR @count>1024 OR @null_offset<4 OR @offset+@null_offset+2+(@count+7)/8>8192-2*@slots
                            THROW 51000, 'Invalid record null bitmap.', 1;
                        SELECT @null_bytes=(@count+7)/8,@variable_count=0;
                        SET @variable_header=@offset+@null_offset+2+@null_bytes;
                        IF (@status & 32)<>0
                            SET @variable_count=CONVERT(int,SUBSTRING(@bytes,@variable_header+2,1)+SUBSTRING(@bytes,@variable_header+1,1));
                        IF @variable_count>@count OR @variable_header+2+2*@variable_count>8192-2*@slots
                            THROW 51000, 'Invalid variable-column offset array.', 1;
                        SET @record_id=@record_id+1;
                        OPEN column_cursor;
                        FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
                        WHILE @@FETCH_STATUS=0
                        BEGIN
                            SET @value=NULL;
                            IF @null_bit<0 OR @null_bit>=@count THROW 51000, 'Column null bitmap layout mismatch.', 1;
                            IF (CONVERT(int,SUBSTRING(@bytes,@offset+@null_offset+3+@null_bit/8,1)) & CONVERT(int,POWER(2,@null_bit%8)))=0
                            BEGIN
                                SELECT @start=@leaf_offset,@offrow=0;
                                IF @start<0
                                BEGIN
                                    SET @ordinal=-@start;
                                    IF @ordinal>@variable_count SELECT @start=4,@length=0; -- omitted trailing empty value
                                    ELSE
                                    BEGIN
                                        SET @i=@variable_header+2+2*(@ordinal-1);
                                        SET @value_end=CONVERT(int,SUBSTRING(@bytes,@i+2,1)+SUBSTRING(@bytes,@i+1,1));
                                        IF (@value_end & 32768)<>0 SET @offrow=1;
                                        SET @value_end=@value_end & 32767;
                                        IF @ordinal=1 SET @start=@variable_header+2+2*@variable_count-@offset;
                                        ELSE
                                        BEGIN
                                            SET @i=@variable_header+2+2*(@ordinal-2);
                                            SET @start=CONVERT(int,SUBSTRING(@bytes,@i+2,1)+SUBSTRING(@bytes,@i+1,1));
                                            SET @start=@start & 32767;
                                        END;
                                        SET @length=@value_end-@start;
                                    END;
                                END;
                                IF @start<4 OR @length<0 OR @offset+@start+@length>8192-2*@slots
                                    THROW 51000, 'Invalid record value bounds.', 1;
                                SET @raw=SUBSTRING(@bytes,@offset+@start+1,@length);
                                IF @offrow=1
                                BEGIN
                                    IF NOT EXISTS(SELECT 1 FROM #spr_columns WHERE column_id=@column_id AND max_length=-1 AND system_type_id IN(167,231,165))
                                        THROW 51000, 'Off-row values in non-MAX columns remain unsupported.', 1;
                                    SELECT @lob_pointer=@raw,@lob_total=NULL;
                                    TRUNCATE TABLE #spr_lob_work;
                                    TRUNCATE TABLE #spr_lob_seen;
                                    TRUNCATE TABLE #spr_lob_pieces;
                                    IF DATALENGTH(@lob_pointer)=16
                                    BEGIN
                                        -- Forced off-row MAX uses an eight-byte blob id and an eight-byte RID.
                                        SET @lob_blob=SUBSTRING(@lob_pointer,1,8);
                                        SELECT @lob_page=CONVERT(int,SUBSTRING(@lob_pointer,12,1)+SUBSTRING(@lob_pointer,11,1)+SUBSTRING(@lob_pointer,10,1)+SUBSTRING(@lob_pointer,9,1)),
                                            @lob_file=CONVERT(int,SUBSTRING(@lob_pointer,14,1)+SUBSTRING(@lob_pointer,13,1)),
                                            @lob_slot=CONVERT(int,SUBSTRING(@lob_pointer,16,1)+SUBSTRING(@lob_pointer,15,1));
                                        INSERT #spr_lob_work VALUES(@lob_file,@lob_page,@lob_slot,0,NULL,0);
                                    END;
                                    ELSE
                                    BEGIN
                                        -- Inline root: 12-byte header followed by cumulative length + RID links.
                                        IF DATALENGTH(@lob_pointer)<24 OR (DATALENGTH(@lob_pointer)-12)%12<>0
                                            OR SUBSTRING(@lob_pointer,1,2)<>0x0400 OR CONVERT(int,SUBSTRING(@lob_pointer,3,1))>31
                                            THROW 51000, 'Unsupported MAX inline-root format.', 1;
                                        SET @lob_blob=0x0000+SUBSTRING(@lob_pointer,9,4)+0x0000;
                                        SELECT @lob_at=13,@lob_prev=0;
                                        WHILE @lob_at<=DATALENGTH(@lob_pointer)
                                        BEGIN
                                            SELECT @lob_next=CONVERT(bigint,SUBSTRING(@lob_pointer,@lob_at+3,1)+SUBSTRING(@lob_pointer,@lob_at+2,1)+SUBSTRING(@lob_pointer,@lob_at+1,1)+SUBSTRING(@lob_pointer,@lob_at,1)),
                                                @lob_page=CONVERT(int,SUBSTRING(@lob_pointer,@lob_at+7,1)+SUBSTRING(@lob_pointer,@lob_at+6,1)+SUBSTRING(@lob_pointer,@lob_at+5,1)+SUBSTRING(@lob_pointer,@lob_at+4,1)),
                                                @lob_file=CONVERT(int,SUBSTRING(@lob_pointer,@lob_at+9,1)+SUBSTRING(@lob_pointer,@lob_at+8,1)),
                                                @lob_slot=CONVERT(int,SUBSTRING(@lob_pointer,@lob_at+11,1)+SUBSTRING(@lob_pointer,@lob_at+10,1));
                                            IF @lob_next<=@lob_prev OR @lob_next>2147483647 THROW 51000, 'Invalid MAX inline-root length.', 1;
                                            INSERT #spr_lob_work VALUES(@lob_file,@lob_page,@lob_slot,@lob_prev,@lob_next,0);
                                            SELECT @lob_prev=@lob_next,@lob_at=@lob_at+12;
                                        END;
                                        SET @lob_total=@lob_prev;
                                    END;
                                    WHILE EXISTS(SELECT 1 FROM #spr_lob_work)
                                    BEGIN
                                        SELECT TOP(1) @lob_id=id,@lob_file=file_id,@lob_page=page_id,@lob_slot=slot_id,
                                            @lob_start=start_offset,@lob_end=end_offset,@lob_depth=depth FROM #spr_lob_work ORDER BY id;
                                        DELETE #spr_lob_work WHERE id=@lob_id;
                                        IF @lob_depth>31 OR @lob_file<1 OR @lob_page<1 OR @lob_slot<0
                                            THROW 51000, 'Invalid MAX LOB address/depth.', 1;
                                        IF EXISTS(SELECT 1 FROM #spr_lob_seen WHERE file_id=@lob_file AND page_id=@lob_page AND slot_id=@lob_slot)
                                            THROW 51000, 'MAX LOB cycle or duplicate record detected.', 1;
                                        INSERT #spr_lob_seen VALUES(@lob_file,@lob_page,@lob_slot);
                                        SET @lob_bytes=NULL;
                                        SELECT @lob_bytes=c.bytes FROM #spr_pagecache c
                                        JOIN #spr_allocations a ON a.allocation_unit_id=c.allocation_unit_id AND a.type=2
                                        WHERE c.file_id=@lob_file AND c.page_id=@lob_page AND c.page_type IN(3,4);
                                        IF @lob_bytes IS NULL THROW 51000, 'MAX pointer does not reference an allocated LOB page for this table.', 1;
                                        SET @lob_slots=CONVERT(int,SUBSTRING(@lob_bytes,24,1)+SUBSTRING(@lob_bytes,23,1));
                                        IF @lob_slots>4048 OR @lob_slot>=@lob_slots THROW 51000, 'Invalid MAX LOB slot.', 1;
                                        SET @lob_offset=CONVERT(int,SUBSTRING(@lob_bytes,8192-2*@lob_slot,1)+SUBSTRING(@lob_bytes,8191-2*@lob_slot,1));
                                        IF @lob_offset<96 OR @lob_offset>8178-2*@lob_slots THROW 51000, 'Invalid MAX LOB record offset.', 1;
                                        SET @lob_length=CONVERT(int,SUBSTRING(@lob_bytes,@lob_offset+4,1)+SUBSTRING(@lob_bytes,@lob_offset+3,1));
                                        IF @lob_length<14 OR @lob_offset+@lob_length>8192-2*@lob_slots
                                            OR (CONVERT(int,SUBSTRING(@lob_bytes,@lob_offset+1,1)) & 14)<>8
                                            THROW 51000, 'Invalid MAX LOB record header.', 1;
                                        SET @lob_record=SUBSTRING(@lob_bytes,@lob_offset+1,@lob_length);
                                        IF SUBSTRING(@lob_record,5,8)<>@lob_blob THROW 51000, 'MAX LOB blob-id mismatch; the table may have changed during inspection.', 1;
                                        SET @lob_type=CONVERT(int,SUBSTRING(@lob_record,14,1)+SUBSTRING(@lob_record,13,1));
                                        IF @lob_type IN(0,3)
                                        BEGIN
                                            IF @lob_type=3 SET @lob_piece=SUBSTRING(@lob_record,15,@lob_length-14);
                                            ELSE
                                            BEGIN
                                                IF @lob_length<20 THROW 51000, 'Invalid MAX small-root header.', 1;
                                                SET @lob_piece_length=CONVERT(int,SUBSTRING(@lob_record,18,1)+SUBSTRING(@lob_record,17,1)+SUBSTRING(@lob_record,16,1)+SUBSTRING(@lob_record,15,1));
                                                IF @lob_piece_length<0 OR @lob_piece_length>@lob_length-20 THROW 51000, 'Invalid MAX small-root size.', 1;
                                                SET @lob_piece=SUBSTRING(@lob_record,21,@lob_piece_length);
                                            END;
                                            IF @lob_end IS NULL
                                            BEGIN
                                                SET @lob_end=@lob_start+DATALENGTH(@lob_piece);
                                                SET @lob_total=@lob_end;
                                            END;
                                            IF DATALENGTH(@lob_piece)<>@lob_end-@lob_start THROW 51000, 'MAX LOB fragment length mismatch.', 1;
                                            INSERT #spr_lob_pieces VALUES(@lob_start,@lob_piece);
                                        END;
                                        ELSE IF @lob_type IN(2,5)
                                        BEGIN
                                            -- Internal nodes use 8-byte offsets; forced-off-row large roots use 4.
                                            SELECT @lob_link_size=CASE WHEN @lob_type=2 THEN 16 ELSE 12 END,
                                                @lob_link_start=CASE WHEN @lob_type=2 THEN 20 ELSE 24 END,
                                                @lob_links=CONVERT(int,SUBSTRING(@lob_record,18,1)+SUBSTRING(@lob_record,17,1)),
                                                @lob_max_links=CONVERT(int,SUBSTRING(@lob_record,16,1)+SUBSTRING(@lob_record,15,1)),
                                                @lob_level=CONVERT(int,SUBSTRING(@lob_record,20,1)+SUBSTRING(@lob_record,19,1));
                                            IF @lob_links<1 OR @lob_links>@lob_max_links OR @lob_level>31
                                                OR @lob_link_start+@lob_links*@lob_link_size>@lob_length
                                                THROW 51000, 'Invalid MAX LOB child-link array.', 1;
                                            SET @lob_at=@lob_link_start+(@lob_links-1)*@lob_link_size+1;
                                            IF @lob_type=2
                                                SET @lob_last=CONVERT(bigint,SUBSTRING(@lob_record,@lob_at+7,1)+SUBSTRING(@lob_record,@lob_at+6,1)+SUBSTRING(@lob_record,@lob_at+5,1)+SUBSTRING(@lob_record,@lob_at+4,1)+SUBSTRING(@lob_record,@lob_at+3,1)+SUBSTRING(@lob_record,@lob_at+2,1)+SUBSTRING(@lob_record,@lob_at+1,1)+SUBSTRING(@lob_record,@lob_at,1));
                                            ELSE SET @lob_last=CONVERT(bigint,SUBSTRING(@lob_record,@lob_at+3,1)+SUBSTRING(@lob_record,@lob_at+2,1)+SUBSTRING(@lob_record,@lob_at+1,1)+SUBSTRING(@lob_record,@lob_at,1));
                                            IF @lob_end IS NULL SELECT @lob_end=@lob_start+@lob_last,@lob_total=@lob_start+@lob_last;
                                            SET @lob_base=CASE WHEN @lob_last=@lob_end-@lob_start THEN @lob_start WHEN @lob_last=@lob_end THEN 0 ELSE NULL END;
                                            IF @lob_base IS NULL OR @lob_last<1 OR @lob_total>2147483647 THROW 51000, 'MAX LOB child offsets do not cover the value.', 1;
                                            SELECT @lob_at=@lob_link_start+1,@lob_prev=@lob_start;
                                            WHILE @lob_at<=@lob_link_start+@lob_links*@lob_link_size
                                            BEGIN
                                                IF @lob_type=2
                                                    SET @lob_next=@lob_base+CONVERT(bigint,SUBSTRING(@lob_record,@lob_at+7,1)+SUBSTRING(@lob_record,@lob_at+6,1)+SUBSTRING(@lob_record,@lob_at+5,1)+SUBSTRING(@lob_record,@lob_at+4,1)+SUBSTRING(@lob_record,@lob_at+3,1)+SUBSTRING(@lob_record,@lob_at+2,1)+SUBSTRING(@lob_record,@lob_at+1,1)+SUBSTRING(@lob_record,@lob_at,1));
                                                ELSE SET @lob_next=@lob_base+CONVERT(bigint,SUBSTRING(@lob_record,@lob_at+3,1)+SUBSTRING(@lob_record,@lob_at+2,1)+SUBSTRING(@lob_record,@lob_at+1,1)+SUBSTRING(@lob_record,@lob_at,1));
                                                SET @lob_at=@lob_at+@lob_link_size-8;
                                                SELECT @lob_page=CONVERT(int,SUBSTRING(@lob_record,@lob_at+3,1)+SUBSTRING(@lob_record,@lob_at+2,1)+SUBSTRING(@lob_record,@lob_at+1,1)+SUBSTRING(@lob_record,@lob_at,1)),
                                                    @lob_file=CONVERT(int,SUBSTRING(@lob_record,@lob_at+5,1)+SUBSTRING(@lob_record,@lob_at+4,1)),
                                                    @lob_slot=CONVERT(int,SUBSTRING(@lob_record,@lob_at+7,1)+SUBSTRING(@lob_record,@lob_at+6,1));
                                                IF @lob_next<=@lob_prev OR @lob_next>@lob_end THROW 51000, 'Invalid MAX LOB child offset.', 1;
                                                INSERT #spr_lob_work VALUES(@lob_file,@lob_page,@lob_slot,@lob_prev,@lob_next,@lob_depth+1);
                                                SELECT @lob_prev=@lob_next,@lob_at=@lob_at+8;
                                            END;
                                        END;
                                        ELSE THROW 51000, 'Unsupported MAX LOB record type.', 1;
                                    END;
                                    IF @lob_total IS NULL OR @lob_total<>(SELECT SUM(CONVERT(bigint,DATALENGTH(bytes))) FROM #spr_lob_pieces)
                                        THROW 51000, 'Incomplete MAX LOB value.', 1;
                                    IF EXISTS(SELECT 1 FROM (SELECT start_offset,COALESCE(SUM(CONVERT(bigint,DATALENGTH(bytes))) OVER(ORDER BY start_offset ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),0) AS expected_start FROM #spr_lob_pieces) p WHERE start_offset<>expected_start)
                                        THROW 51000, 'MAX LOB fragments overlap or contain gaps.', 1;
                                    SELECT @raw=CONVERT(varbinary(max),(SELECT CONVERT(varchar(max),bytes,2) AS [text()] FROM #spr_lob_pieces ORDER BY start_offset FOR XML PATH(''),TYPE).value('.','varchar(max)'),2);
                                END;
                                SELECT @reversed=0x,@i=@length;
                                IF @type IN(52,56,127,59,62,60,122)
                                    WHILE @i>0 BEGIN SET @reversed=@reversed+SUBSTRING(@raw,@i,1);SET @i=@i-1;END;
                                IF @type=48 SET @value=CONVERT(nvarchar(100),CONVERT(tinyint,@raw));
                                ELSE IF @type=52 SET @value=CONVERT(nvarchar(100),CONVERT(smallint,@reversed));
                                ELSE IF @type=56 SET @value=CONVERT(nvarchar(100),CONVERT(int,@reversed));
                                ELSE IF @type=127 SET @value=CONVERT(nvarchar(100),CONVERT(bigint,@reversed));
                                ELSE IF @type IN(60,122)
                                BEGIN
                                    -- Uncompressed money/smallmoney are signed little-endian
                                    -- integers scaled by 10,000. Decimal division preserves all
                                    -- four places and the minimum signed value without overflow.
                                    IF @length<>CASE WHEN @type=60 THEN 8 ELSE 4 END OR DATALENGTH(@raw)<>@length
                                        THROW 51000, 'Money storage length mismatch.', 1;
                                    SET @n=CASE WHEN @type=60 THEN CONVERT(bigint,@reversed) ELSE CONVERT(int,@reversed) END;
                                    SET @value=CONVERT(nvarchar(40),CONVERT(decimal(19,4),CONVERT(decimal(19,0),@n)/CONVERT(decimal(5,0),10000)));
                                END;
                                ELSE IF @type=104 SET @value=CONVERT(nvarchar(1),(CONVERT(int,@raw)/CONVERT(int,POWER(2,@bit_position))) & 1);
                                ELSE IF @type IN(231,239) SET @value=CONVERT(nvarchar(max),@raw);
                                ELSE IF @type IN(167,175)
                                BEGIN
                                    SET @sql=N'INSERT #spr_varchars([c'+CONVERT(nvarchar(20),@column_id)+N']) VALUES(@b); SELECT @v=CONVERT(nvarchar(max),[c'+CONVERT(nvarchar(20),@column_id)+N']) FROM #spr_varchars; TRUNCATE TABLE #spr_varchars;';
                                    EXEC sys.sp_executesql @sql,N'@b varbinary(max),@v nvarchar(max) OUTPUT',@raw,@value OUTPUT;
                                END;
                                ELSE IF @type IN(165,173) SET @value=CASE WHEN DATALENGTH(@raw)=0 THEN N'0x' ELSE CONVERT(nvarchar(max),@raw,1) END;
                                ELSE IF @type=36 SET @value=CONVERT(nvarchar(36),CONVERT(uniqueidentifier,@raw));
                                ELSE IF @type IN(40,41,42,43)
                                BEGIN
                                    -- Rowstore stores date as days since 0001-01-01,
                                    -- and time as little-endian units of 10^-scale seconds.
                                    -- datetimeoffset stores UTC date/time plus a signed minute offset.
                                    IF @scale<0 OR @scale>7 THROW 51000, 'Invalid temporal scale.', 1;
                                    SELECT @date_days=0,@time_units=0,@zone_minutes=0,
                                        @time_factor=CONVERT(bigint,POWER(CONVERT(bigint,10),@scale)),
                                        @time_length=CASE WHEN @type=40 THEN 0 WHEN @scale<=2 THEN 3 WHEN @scale<=4 THEN 4 ELSE 5 END;
                                    IF @length<>@time_length+CASE WHEN @type=41 THEN 0 ELSE 3 END+CASE WHEN @type=43 THEN 2 ELSE 0 END
                                        THROW 51000, 'Temporal storage length mismatch.', 1;
                                    SELECT @reversed=0x,@i=@time_length;
                                    WHILE @i>0 BEGIN SET @reversed=@reversed+SUBSTRING(@raw,@i,1);SET @i=@i-1;END;
                                    IF @time_length>0 SET @time_units=CONVERT(bigint,@reversed);
                                    IF @type<>41 SET @date_days=CONVERT(int,SUBSTRING(@raw,@time_length+3,1)+SUBSTRING(@raw,@time_length+2,1)+SUBSTRING(@raw,@time_length+1,1));
                                    IF @date_days<0 OR @date_days>3652058 OR @time_units<0 OR @time_units>=86400*@time_factor
                                        THROW 51000, 'Temporal value outside valid range.', 1;
                                    SET @temporal=DATEADD(day,@date_days,CONVERT(datetime2(7),'00010101',112));
                                    SET @temporal=DATEADD(second,CONVERT(int,@time_units/@time_factor),@temporal);
                                    SET @temporal=DATEADD(nanosecond,CONVERT(int,(@time_units%@time_factor)*(1000000000/@time_factor)),@temporal);
                                    IF @type=43
                                    BEGIN
                                        SET @zone_minutes=CONVERT(smallint,SUBSTRING(@raw,@length,1)+SUBSTRING(@raw,@length-1,1));
                                        IF @zone_minutes<-840 OR @zone_minutes>840 THROW 51000, 'Invalid datetimeoffset time zone.', 1;
                                        -- Style 127 normalizes to Z; style 121 retains the stored offset.
                                        SET @value=CONVERT(nvarchar(48),TODATETIMEOFFSET(DATEADD(minute,@zone_minutes,@temporal),@zone_minutes),121);
                                    END;
                                    ELSE IF @type=40 SET @value=CONVERT(nvarchar(10),@temporal,23);
                                    ELSE IF @type=41 SET @value=CONVERT(nvarchar(30),CONVERT(time(7),@temporal));
                                    ELSE SET @value=CONVERT(nvarchar(30),@temporal,126);
                                END;
                                ELSE IF @type IN(58,61)
                                BEGIN
                                    -- Legacy rowstore order is time first, then day count.
                                    IF @type=61
                                    BEGIN
                                        IF @length<>8 THROW 51000, 'Invalid datetime storage length.', 1;
                                        SET @date_days=CONVERT(int,SUBSTRING(@raw,8,1)+SUBSTRING(@raw,7,1)+SUBSTRING(@raw,6,1)+SUBSTRING(@raw,5,1));
                                        SET @time_units=CONVERT(bigint,SUBSTRING(@raw,4,1)+SUBSTRING(@raw,3,1)+SUBSTRING(@raw,2,1)+SUBSTRING(@raw,1,1));
                                        IF @date_days<-53690 OR @date_days>2958463 OR @time_units<0 OR @time_units>=25920000
                                            THROW 51000, 'Invalid datetime day/tick value.', 1;
                                        SET @legacy=DATEADD(day,@date_days,CONVERT(datetime,'19000101',112));
                                        SET @legacy=DATEADD(millisecond,CONVERT(int,(@time_units*10+1)/3),@legacy);
                                    END;
                                    ELSE
                                    BEGIN
                                        IF @length<>4 THROW 51000, 'Invalid smalldatetime storage length.', 1;
                                        SET @date_days=CONVERT(int,SUBSTRING(@raw,4,1)+SUBSTRING(@raw,3,1));
                                        SET @time_units=CONVERT(int,SUBSTRING(@raw,2,1)+SUBSTRING(@raw,1,1));
                                        IF @date_days>65535 OR @time_units>=1440 THROW 51000, 'Invalid smalldatetime day/minute value.', 1;
                                        SET @legacy=DATEADD(minute,CONVERT(int,@time_units),DATEADD(day,@date_days,CONVERT(datetime,'19000101',112)));
                                    END;
                                    SET @value=CONVERT(nvarchar(30),@legacy,126);
                                END;
                                ELSE IF @type IN(106,108)
                                BEGIN
                                    SELECT @n=0,@i=@length;
                                    WHILE @i>1 BEGIN SET @n=@n*CONVERT(decimal(38,0),256)+CONVERT(int,SUBSTRING(@raw,@i,1));SET @i=@i-1;END;
                                    SET @value=CONVERT(nvarchar(100),@n);
                                    IF @scale>0
                                    BEGIN
                                        IF LEN(@value)<=@scale SET @value=REPLICATE(N'0',@scale+1-LEN(@value))+@value;
                                        SET @value=STUFF(@value,LEN(@value)-@scale+1,0,N'.');
                                    END;
                                    IF CONVERT(int,SUBSTRING(@raw,1,1))=0 SET @value=N'-'+@value;
                                END;
                                ELSE IF @type IN(59,62)
                                BEGIN
                                    IF @type=59
                                    BEGIN
                                        SET @u=CONVERT(bigint,@reversed);
                                        SELECT @sign=CASE WHEN @u<CONVERT(bigint,2147483648) THEN 1 ELSE -1 END,@exponent=CONVERT(int,(@u/8388608)%256),@fraction=@u%8388608;
                                        IF @exponent=255 THROW 51000, 'Nonfinite floating point value is unsupported.', 1;
                                        SET @float=@sign*POWER(CONVERT(float,2),CASE WHEN @exponent=0 THEN -126 ELSE @exponent-127 END)
                                            *(CASE WHEN @exponent=0 THEN 0.0E0 ELSE 1.0E0 END+CONVERT(float,@fraction)/8388608.0E0);
                                    END;
                                    ELSE
                                    BEGIN
                                        SET @fraction=CONVERT(bigint,SUBSTRING(@raw,6,1)+SUBSTRING(@raw,5,1)+SUBSTRING(@raw,4,1)+SUBSTRING(@raw,3,1)+SUBSTRING(@raw,2,1)+SUBSTRING(@raw,1,1))
                                            +(CONVERT(bigint,SUBSTRING(@raw,7,1)) & 15)*281474976710656;
                                        SELECT @sign=CASE WHEN (CONVERT(int,SUBSTRING(@raw,8,1)) & 128)=0 THEN 1 ELSE -1 END,
                                            @exponent=CONVERT(int,SUBSTRING(@raw,7,1))/16+(CONVERT(int,SUBSTRING(@raw,8,1)) & 127)*16;
                                        IF @exponent=2047 THROW 51000, 'Nonfinite floating point value is unsupported.', 1;
                                        SET @float=@sign*POWER(CONVERT(float,2),CASE WHEN @exponent=0 THEN -1022 ELSE @exponent-1023 END)
                                            *(CASE WHEN @exponent=0 THEN 0.0E0 ELSE 1.0E0 END+CONVERT(float,@fraction)/4503599627370496.0E0);
                                    END;
                                    SET @value=CONVERT(nvarchar(100),@float,3);
                                END;
                            END;
                            INSERT #spr_values VALUES(@record_id,@column_id,@value);
                            FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
                        END;
                        CLOSE column_cursor;
                    END;
                END;
            END;
            DELETE #spr_pages WHERE file_id=@file AND page_id=@page;
        END;

        -- Build a result set with the original column names and native base types.
        OPEN column_cursor;
        FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @type_sql=CASE @type WHEN 48 THEN N'tinyint' WHEN 52 THEN N'smallint' WHEN 56 THEN N'int' WHEN 127 THEN N'bigint'
                WHEN 104 THEN N'bit' WHEN 59 THEN N'real' WHEN 62 THEN N'float' WHEN 36 THEN N'uniqueidentifier'
                WHEN 60 THEN N'money' WHEN 122 THEN N'smallmoney'
                WHEN 106 THEN N'decimal' WHEN 108 THEN N'numeric' WHEN 231 THEN N'nvarchar' WHEN 239 THEN N'nchar'
                WHEN 167 THEN N'varchar' WHEN 175 THEN N'char' WHEN 165 THEN N'varbinary' WHEN 173 THEN N'binary' END;
            IF @type IN(40,41,42,43,58,61) SET @type_sql=CASE @type WHEN 40 THEN N'date' WHEN 41 THEN N'time' WHEN 42 THEN N'datetime2' WHEN 43 THEN N'datetimeoffset' WHEN 58 THEN N'smalldatetime' WHEN 61 THEN N'datetime' END;
            IF @type IN(41,42,43) SET @type_sql=@type_sql+N'('+CONVERT(nvarchar(10),@scale)+N')';
            IF @type IN(106,108) SET @type_sql=@type_sql+N'('+CONVERT(nvarchar(10),@precision)+N','+CONVERT(nvarchar(10),@scale)+N')';
            IF @type IN(231,239,167,175,165,173) SET @type_sql=@type_sql+N'('+CASE WHEN @length=-1 THEN N'max' ELSE CONVERT(nvarchar(10),CASE WHEN @type IN(231,239) THEN @length/2 ELSE @length END) END+N')';
            SET @projection=@projection+CASE WHEN @projection=N'' THEN N'' ELSE N',' END+N'CONVERT('+@type_sql+
                N',MAX(CASE WHEN column_id='+CONVERT(nvarchar(20),@column_id)+N' THEN text_value END)'+
                CASE WHEN @type IN(231,239,167,175) THEN N' COLLATE '+@collation ELSE N'' END+
                CASE WHEN @type IN(165,173) THEN N',1' ELSE N'' END+N') AS '+QUOTENAME(@name);
            FETCH NEXT FROM column_cursor INTO @column_id,@name,@type,@length,@precision,@scale,@collation,@leaf_offset,@null_bit,@bit_position;
        END;
        CLOSE column_cursor;
        DEALLOCATE column_cursor;
        IF @global_name IS NULL
        BEGIN
            SET @sql=N'SELECT '+@projection+N' FROM #spr_values GROUP BY record_id ORDER BY record_id;';
            EXEC(@sql);
        END;
        ELSE
        BEGIN
            -- Materialize only after the reader succeeds. Separate dynamic
            -- batches avoid compiling SELECT INTO against an existing schema.
            -- Keep the drop and creation in one transaction so failure restores
            -- the previous global table. The host keeps this connection alive.
            BEGIN TRANSACTION;
            SET @sql=N'DROP TABLE IF EXISTS '+QUOTENAME(@global_name)+N';';
            EXEC(@sql);
            SET @sql=N'SELECT '+@projection+N' INTO '+QUOTENAME(@global_name)+N' FROM #spr_values GROUP BY record_id;';
            EXEC(@sql);
            COMMIT TRANSACTION;
            SELECT COUNT_BIG(*) FROM (SELECT record_id FROM #spr_values GROUP BY record_id) copied;
        END;
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
