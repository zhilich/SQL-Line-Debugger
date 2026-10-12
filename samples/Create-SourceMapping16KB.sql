CREATE PROCEDURE dbo.DebuggerSourceMapping16KB
    @Seed int = 7,
    @Iterations int = 3,
    @TakeErrorPath bit = 0,
    @Verbose bit = 1
AS
BEGIN
    SET NOCOUNT ON;

    -- One intentionally large module: scroll and set breakpoints near its end.
    -- Definitions contain CRLF, tabs, Unicode, comments, and multiline statements.
    IF @Seed NOT BETWEEN 0 AND 1000 OR @Iterations NOT BETWEEN 0 AND 5
        THROW 51215, 'Seed must be 0..1000 and Iterations must be 0..5.', 1;

    DECLARE @Checkpoint nvarchar(40) = N'ENTRY';
    DECLARE @Value int = @Seed;
    DECLARE @Phase int = 0;
    DECLARE @LoopIndex int = 1;
    DECLARE @Branch nvarchar(20) = N'initial';
    DECLARE @ErrorNumber int = NULL;
    DECLARE @ErrorLine int = NULL;
    DECLARE @ErrorMessage nvarchar(2048) = NULL;
    DECLARE @ReturnCode int = 0;
    DECLARE @Unicode nvarchar(80) = N'Greek Ω; CJK 漢字; emoji 😀';
    DECLARE @Multiline nvarchar(200) = N'First physical source line.
Second physical source line: SELECT ''quoted''; -- text, not SQL.
Third physical source line: Ω 漢字 😀';
    DECLARE @Trace TABLE
    (
        TraceId int IDENTITY(1,1),
        [Phase] int,
        [Checkpoint] nvarchar(40),
        [Value] int,
        [Branch] nvarchar(20),
        [Note] nvarchar(200)
    );

    /* The following are comment text rather than executable statements:
       SELECT * FROM imaginary_table;
       BEGIN TRY; THROW 51216, 'comment only', 1; END TRY;
       Unicode before executable spans: Ω 漢字 😀.
    */
    -- BREAK ENTRY: @Value starts at @Seed; Unicode has already appeared in source.
    INSERT @Trace VALUES (0, @Checkpoint, @Value, @Branch, @Unicode);

    BEGIN TRY
        WHILE @LoopIndex <= @Iterations
        BEGIN
            -- BREAK LOOP: the same span executes several times.
            SET @Checkpoint = CONCAT(N'LOOP-', @LoopIndex);
            SET @Value = @Value + (@LoopIndex * @Seed);
            INSERT @Trace
                ([Phase], [Checkpoint], [Value], [Branch], [Note])
            VALUES
                (-@LoopIndex, @Checkpoint, @Value, N'loop', @Multiline);
            SET @LoopIndex = @LoopIndex + 1;
        END;

        -- BREAK CTE: one statement spans several physical source lines.
        ;WITH [Seed Rows] AS
        (
            SELECT @Seed AS [Number]
            UNION ALL
            SELECT @Seed + 1
            UNION ALL
            SELECT @Seed + 2
        )
        SELECT @Value = @Value + SUM([Number])
        FROM [Seed Rows];
        SET @Checkpoint = N'CTE';
        INSERT @Trace VALUES (-100, @Checkpoint, @Value, N'cte', N'CTE complete');

        -- BREAK M01: independent checkpoint, including blank lines and tabs.
        SET @Phase = 1;
        SET @Checkpoint = N'M01';

        SET @Value =
            ((@Value + @Seed) * 3 + 1) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M01: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M02: independent checkpoint, including blank lines and tabs.
        SET @Phase = 2;
        SET @Checkpoint = N'M02';

        SET @Value =
            ((@Value + @Seed) * 3 + 2) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M02: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M03: independent checkpoint, including blank lines and tabs.
        SET @Phase = 3;
        SET @Checkpoint = N'M03';

        SET @Value =
            ((@Value + @Seed) * 3 + 3) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M03: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M04: independent checkpoint, including blank lines and tabs.
        SET @Phase = 4;
        SET @Checkpoint = N'M04';

        SET @Value =
            ((@Value + @Seed) * 3 + 4) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M04: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M05: independent checkpoint, including blank lines and tabs.
        SET @Phase = 5;
        SET @Checkpoint = N'M05';

        SET @Value =
            ((@Value + @Seed) * 3 + 5) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M05: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M06: independent checkpoint, including blank lines and tabs.
        SET @Phase = 6;
        SET @Checkpoint = N'M06';

        SET @Value =
            ((@Value + @Seed) * 3 + 6) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M06: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M07: independent checkpoint, including blank lines and tabs.
        SET @Phase = 7;
        SET @Checkpoint = N'M07';

        SET @Value =
            ((@Value + @Seed) * 3 + 7) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M07: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M08: independent checkpoint, including blank lines and tabs.
        SET @Phase = 8;
        SET @Checkpoint = N'M08';

        SET @Value =
            ((@Value + @Seed) * 3 + 8) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M08: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M09: independent checkpoint, including blank lines and tabs.
        SET @Phase = 9;
        SET @Checkpoint = N'M09';

        SET @Value =
            ((@Value + @Seed) * 3 + 9) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M09: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M10: independent checkpoint, including blank lines and tabs.
        SET @Phase = 10;
        SET @Checkpoint = N'M10';

        SET @Value =
            ((@Value + @Seed) * 3 + 10) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M10: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M11: independent checkpoint, including blank lines and tabs.
        SET @Phase = 11;
        SET @Checkpoint = N'M11';

        SET @Value =
            ((@Value + @Seed) * 3 + 11) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M11: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M12: independent checkpoint, including blank lines and tabs.
        SET @Phase = 12;
        SET @Checkpoint = N'M12';

        SET @Value =
            ((@Value + @Seed) * 3 + 12) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M12: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- BREAK M13: independent checkpoint, including blank lines and tabs.
        SET @Phase = 13;
        SET @Checkpoint = N'M13';

        SET @Value =
            ((@Value + @Seed) * 3 + 13) % 1000003;
        IF @Value % 2 = 0
        BEGIN
            SET @Branch = N'even';
            SET @Value = @Value + @Phase;
        END
        ELSE
        BEGIN
            SET @Branch = N'odd';
            SET @Value = @Value - @Phase;
        END;

		-- A real tab precedes this comment; literals contain fake SQL tokens.
        INSERT @Trace
            ([Phase], [Checkpoint], [Value], [Branch], [Note])
        VALUES
            (@Phase, @Checkpoint, @Value, @Branch,
             N'M13: SELECT ''literal''; /* text */ -- still a string; Ω 漢字 😀');
        IF @Verbose = 1
            PRINT CONCAT(@Checkpoint, N' value=', @Value, N' branch=', @Branch);

        -- Size padding: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
        -- BREAK LAST-TRY: inspect the last phase before the optional caught error.
        SET @Checkpoint = N'LAST-TRY';
        IF @TakeErrorPath = 1
        BEGIN
            -- BREAK THROW: deliberate error is caught within this procedure.
            THROW 51216, 'Intentional source-mapping fixture error.', 1;
        END;
        SET @Checkpoint = N'TRY-COMPLETE';
    END TRY
    BEGIN CATCH
        -- BREAK CATCH: ERROR_LINE() refers to the module's physical source line.
        SELECT
            @ErrorNumber = ERROR_NUMBER(),
            @ErrorLine = ERROR_LINE(),
            @ErrorMessage = ERROR_MESSAGE();
        SET @ReturnCode = 1;
        SET @Checkpoint = N'CAUGHT';
        INSERT @Trace VALUES (900, @Checkpoint, @Value, N'catch', @ErrorMessage);
    END CATCH;

    -- BREAK DONE: set a breakpoint here to look for accumulated mapping drift.
    SET @Checkpoint = N'DONE';
    INSERT @Trace VALUES (1000, @Checkpoint, @Value, N'final', @Unicode);

    SELECT
        @Checkpoint AS FinalCheckpoint,
        @Value AS FinalValue,
        @Phase AS CompletedPhases,
        @Iterations AS LoopIterations,
        (SELECT COUNT(*) FROM @Trace) AS TraceRows,
        @ErrorNumber AS CaughtErrorNumber,
        @ErrorLine AS CaughtErrorLine,
        @ErrorMessage AS CaughtErrorMessage,
        @Unicode AS UnicodeControl;

    -- BREAK RESULT: final multiline SELECT uses bracketed aliases.
    SELECT
        TraceId AS [Trace Id],
        [Phase],
        [Checkpoint],
        [Value],
        [Branch],
        [Note]
    FROM @Trace
    ORDER BY TraceId;

    -- BREAK RETURN: final executable statement in the module.
    RETURN @ReturnCode;
END;
