-- SQL Line Debugger: nested order-processing fixture for SQL Server 2019+.
-- Install with sqlcmd -S "(localdb)\mssqllocaldb" -E -b -i this-file.sql
-- Entry point: EXEC SqlDebugTest.Run @Seed=7, @OrderCount=3, @Scenario=N'normal';
-- All working rows live in session-local temporary tables created by Run.
-- The installation refuses to overwrite an existing SqlDebugTest schema.
USE [Test];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF SCHEMA_ID(N'SqlDebugTest') IS NOT NULL
  THROW 51000, 'SqlDebugTest already exists; installation will not overwrite it.', 1;
EXEC(N'CREATE SCHEMA SqlDebugTest AUTHORIZATION dbo;');
GO

CREATE PROCEDURE SqlDebugTest.AssessRisk
  @CustomerId int,
  @Priority tinyint,
  @Quantity int,
  @Gross decimal(18,2),
  @Region nvarchar(16),
  @RiskScore int OUTPUT,
  @Decision nvarchar(20) OUTPUT,
  @Verbose bit = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @CustomerComponent int = 0;
  DECLARE @VolumeComponent int = 0;
  DECLARE @RegionComponent int = 0;
  DECLARE @PriorityComponent int = 0;
  DECLARE @ExposureComponent int = 0;
  DECLARE @Exposure decimal(18,2) = 0;
  DECLARE @CustomerBucket int;

  SET @RiskScore = 0;
  SET @Decision = N'Pending';
  SET @CustomerBucket = @CustomerId % 7;

  IF @CustomerId <= 0
    THROW 51001, 'CustomerId must be positive.', 1;
  IF @Quantity < 0
    THROW 51002, 'Negative quantity: intentional error scenario.', 1;
  IF @Gross < 0
    THROW 51003, 'Gross amount cannot be negative.', 1;

  SET @CustomerComponent = @CustomerBucket * 3;
  IF @Quantity >= 8
    SET @VolumeComponent = 18;
  ELSE IF @Quantity >= 4
    SET @VolumeComponent = 9;
  ELSE
    SET @VolumeComponent = 2;

  SET @RegionComponent = CASE @Region
    WHEN N'North' THEN 3
    WHEN N'South' THEN 6
    WHEN N'West' THEN 8
    ELSE 12
  END;
  SET @PriorityComponent = CASE WHEN @Priority = 1 THEN 7 ELSE 0 END;
  SET @Exposure = @Gross * (1 + CONVERT(decimal(8,2), @Quantity) / 10);
  IF @Exposure > 500
    SET @ExposureComponent = 20;
  ELSE IF @Exposure > 150
    SET @ExposureComponent = 10;
  ELSE
    SET @ExposureComponent = 1;

  SET @RiskScore = @CustomerComponent + @VolumeComponent;
  SET @RiskScore = @RiskScore + @RegionComponent;
  SET @RiskScore = @RiskScore + @PriorityComponent;
  SET @RiskScore = @RiskScore + @ExposureComponent;

  IF @RiskScore >= 50
  BEGIN
    SET @Decision = N'Review';
  END
  ELSE IF @RiskScore >= 30
  BEGIN
    SET @Decision = N'Limited';
  END
  ELSE
  BEGIN
    SET @Decision = N'Accept';
  END;

  INSERT #Trace (Depth, ModuleName, Marker, Amount, Detail)
  VALUES (5, N'AssessRisk', N'decision', @Exposure,
    CONCAT(@Decision, N'; score=', @RiskScore));
  IF @Verbose = 1
    PRINT CONCAT(N'[AssessRisk] customer=', @CustomerId,
      N', quantity=', @Quantity, N', score=', @RiskScore, N', ', @Decision);
  RETURN @RiskScore;
END;
GO

CREATE PROCEDURE SqlDebugTest.CalculateDiscount
  @CustomerId int,
  @Priority tinyint,
  @Quantity int,
  @Gross decimal(18,2),
  @Region nvarchar(16),
  @Category nvarchar(16),
  @DiscountRate decimal(9,4) OUTPUT,
  @RiskScore int OUTPUT,
  @Decision nvarchar(20) OUTPUT,
  @Verbose bit = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @BaseRate decimal(9,4) = 0;
  DECLARE @VolumeRate decimal(9,4) = 0;
  DECLARE @LoyaltyRate decimal(9,4) = 0;
  DECLARE @PriorityRate decimal(9,4) = 0;
  DECLARE @UncappedRate decimal(9,4) = 0;
  DECLARE @MaximumRate decimal(9,4) = 0.1000;
  DECLARE @RiskReturn int = 0;
  DECLARE @CustomerTier nvarchar(12);
  DECLARE @Eligible bit = 1;

  SET @DiscountRate = 0;
  SET @RiskScore = 0;
  SET @Decision = N'Pending';

  EXEC @RiskReturn = SqlDebugTest.AssessRisk
    @CustomerId, @Priority, @Quantity, @Gross, @Region,
    @RiskScore OUTPUT, @Decision OUTPUT, @Verbose;

  SET @CustomerTier = CASE WHEN @CustomerId % 3 = 0 THEN N'Gold'
    WHEN @CustomerId % 3 = 1 THEN N'Silver' ELSE N'Standard' END;
  SET @BaseRate = CASE @Category
    WHEN N'Hardware' THEN 0.0250
    WHEN N'Office' THEN 0.0400
    WHEN N'Service' THEN 0.0100
    ELSE 0.0000 END;

  IF @Quantity >= 6
    SET @VolumeRate = 0.0600;
  ELSE IF @Quantity >= 3
    SET @VolumeRate = 0.0300;
  ELSE
    SET @VolumeRate = 0.0000;

  IF @CustomerTier = N'Gold'
    SET @LoyaltyRate = 0.0350;
  ELSE IF @CustomerTier = N'Silver'
    SET @LoyaltyRate = 0.0150;
  IF @Priority = 1
    SET @PriorityRate = 0.0050;

  SET @UncappedRate = @BaseRate + @VolumeRate;
  SET @UncappedRate = @UncappedRate + @LoyaltyRate;
  SET @UncappedRate = @UncappedRate + @PriorityRate;
  SET @DiscountRate = @UncappedRate;
  IF @DiscountRate > @MaximumRate
  BEGIN
    SET @DiscountRate = @MaximumRate;
  END;

  IF @Decision = N'Review'
  BEGIN
    SET @Eligible = 0;
    SET @DiscountRate = 0;
  END
  ELSE IF @Decision = N'Limited'
  BEGIN
    SET @DiscountRate = ROUND(@DiscountRate / 2, 4);
  END;

  IF @Gross = 0
  BEGIN
    SET @DiscountRate = 0;
  END;
  INSERT #Trace (Depth, ModuleName, Marker, Amount, Detail)
  VALUES (4, N'CalculateDiscount', N'rate', @DiscountRate,
    CONCAT(N'tier=', @CustomerTier, N'; eligible=', @Eligible,
      N'; risk return=', @RiskReturn));
  IF @Verbose = 1
    PRINT CONCAT(N'[CalculateDiscount] category=', @Category,
      N', tier=', @CustomerTier, N', rate=', @DiscountRate);
  RETURN 0;
END;
GO

CREATE PROCEDURE SqlDebugTest.PriceLine
  @OrderId int,
  @LineNo int,
  @CustomerId int,
  @Priority tinyint,
  @Region nvarchar(16),
  @Net decimal(18,2) OUTPUT,
  @Tax decimal(18,2) OUTPUT,
  @LineTotal decimal(18,2) OUTPUT,
  @RiskScore int OUTPUT,
  @Verbose bit = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @Quantity int;
  DECLARE @UnitPrice decimal(18,2);
  DECLARE @Category nvarchar(16);
  DECLARE @Sku nvarchar(32);
  DECLARE @Gross decimal(18,2) = 0;
  DECLARE @DiscountRate decimal(9,4) = 0;
  DECLARE @DiscountAmount decimal(18,2) = 0;
  DECLARE @TaxRate decimal(9,4) = 0;
  DECLARE @Decision nvarchar(20) = N'Pending';
  DECLARE @Found bit = 0;

  SET @Net = 0;
  SET @Tax = 0;
  SET @LineTotal = 0;
  SET @RiskScore = 0;

  SELECT
    @Quantity = Quantity,
    @UnitPrice = UnitPrice,
    @Category = Category,
    @Sku = Sku,
    @Found = 1
  FROM #Lines
  WHERE OrderId = @OrderId
   AND LineNumber = @LineNo;

  IF @Found = 0
    THROW 51004, 'PriceLine could not locate the requested line.', 1;
  IF @UnitPrice < 0
    THROW 51005, 'Unit price must be nonnegative.', 1;
  SET @Gross = ROUND(CONVERT(decimal(18,2), @Quantity) * @UnitPrice, 2);

  EXEC SqlDebugTest.CalculateDiscount
    @CustomerId, @Priority, @Quantity, @Gross, @Region, @Category,
    @DiscountRate OUTPUT, @RiskScore OUTPUT, @Decision OUTPUT, @Verbose;

  SET @DiscountAmount = ROUND(@Gross * @DiscountRate, 2);
  SET @Net = @Gross - @DiscountAmount;
  IF @Net < 0
    SET @Net = 0;
  SET @TaxRate = CASE @Region
    WHEN N'North' THEN 0.0725
    WHEN N'South' THEN 0.0600
    WHEN N'West' THEN 0.0825
    ELSE 0.0500 END;
  IF @Category = N'Service'
    SET @TaxRate = 0;
  SET @Tax = ROUND(@Net * @TaxRate, 2);
  SET @LineTotal = @Net + @Tax;

  UPDATE #Lines
  SET Gross = @Gross,
    DiscountRate = @DiscountRate,
    DiscountAmount = @DiscountAmount,
    Net = @Net,
    Tax = @Tax,
    LineTotal = @LineTotal,
    RiskScore = @RiskScore,
    Decision = @Decision
  WHERE OrderId = @OrderId AND LineNumber = @LineNo;

  INSERT #Trace (Depth, ModuleName, Marker, Amount, Detail)
  VALUES (3, N'PriceLine', N'priced', @LineTotal,
    CONCAT(N'order=', @OrderId, N'; line=', @LineNo,
      N'; ', @Sku, N': ', @Decision));
  IF @Verbose = 1
    PRINT CONCAT(N'[PriceLine] order=', @OrderId, N', line=', @LineNo,
      N', net=', @Net, N', tax=', @Tax, N', total=', @LineTotal);
  RETURN 0;
END;
GO

CREATE PROCEDURE SqlDebugTest.ProcessOrder
  @OrderId int,
  @OrderTotal decimal(18,2) OUTPUT,
  @ReviewRequired bit OUTPUT,
  @Verbose bit = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @CustomerId int;
  DECLARE @Priority tinyint;
  DECLARE @Region nvarchar(16);
  DECLARE @LineNo int = 1;
  DECLARE @LastLine int = 0;
  DECLARE @LineNet decimal(18,2) = 0;
  DECLARE @LineTax decimal(18,2) = 0;
  DECLARE @LineTotal decimal(18,2) = 0;
  DECLARE @Subtotal decimal(18,2) = 0;
  DECLARE @TaxTotal decimal(18,2) = 0;
  DECLARE @Shipping decimal(18,2) = 0;
  DECLARE @RiskScore int = 0;
  DECLARE @MaximumRisk int = 0;
  DECLARE @ProcessedLines int = 0;
  DECLARE @FreeShippingThreshold decimal(18,2) = 250;
  DECLARE @Status nvarchar(20) = N'Processing';
  DECLARE @InitialStatus nvarchar(20);

  SET @OrderTotal = 0;
  SET @ReviewRequired = 0;
  SELECT @CustomerId = CustomerId, @Priority = Priority,
    @Region = Region, @InitialStatus = Status
  FROM #Orders WHERE OrderId = @OrderId;
  IF @CustomerId IS NULL
    THROW 51006, 'ProcessOrder could not locate the requested order.', 1;
  IF @InitialStatus <> N'New'
    THROW 51007, 'Only new orders can be processed.', 1;
  SELECT @LastLine = MAX(LineNumber) FROM #Lines WHERE OrderId = @OrderId;
  UPDATE #Orders SET Status = @Status WHERE OrderId = @OrderId;

  WHILE @LineNo <= @LastLine
  BEGIN
    SET @LineNet = 0;
    SET @LineTax = 0;
    SET @LineTotal = 0;
    SET @RiskScore = 0;

    EXEC SqlDebugTest.PriceLine
      @OrderId, @LineNo, @CustomerId, @Priority, @Region,
      @LineNet OUTPUT, @LineTax OUTPUT, @LineTotal OUTPUT, @RiskScore OUTPUT, @Verbose;

    SET @Subtotal = @Subtotal + @LineNet;
    SET @TaxTotal = @TaxTotal + @LineTax;
    SET @OrderTotal = @OrderTotal + @LineTotal;
    SET @ProcessedLines = @ProcessedLines + 1;
    IF @RiskScore > @MaximumRisk
      SET @MaximumRisk = @RiskScore;
    IF @RiskScore >= 50
      SET @ReviewRequired = 1;

    SET @LineNo = @LineNo + 1;
  END;

  IF @Subtotal >= @FreeShippingThreshold
    SET @Shipping = 0;
  ELSE IF @Priority = 1
    SET @Shipping = 12.50;
  ELSE
    SET @Shipping = 5.00;
  IF @Subtotal = 0
    SET @Shipping = 0;
  SET @OrderTotal = @OrderTotal + @Shipping;
  SET @Status = CASE WHEN @ReviewRequired = 1 THEN N'Review' ELSE N'Complete' END;

  UPDATE #Orders
  SET Subtotal = @Subtotal, Tax = @TaxTotal, Shipping = @Shipping,
    Total = @OrderTotal, RiskScore = @MaximumRisk, Status = @Status
  WHERE OrderId = @OrderId;
  IF @Verbose = 1
    PRINT CONCAT(N'[ProcessOrder] order=', @OrderId, N', lines=', @ProcessedLines,
      N', shipping=', @Shipping, N', total=', @OrderTotal, N', ', @Status);
  RETURN @ProcessedLines;
END;
GO

CREATE PROCEDURE SqlDebugTest.BuildOrders
  @Seed int,
  @OrderCount int,
  @Scenario nvarchar(16),
  @CreatedLines int OUTPUT,
  @Verbose bit = 1
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @OrderId int = 1;
  DECLARE @LineNo int = 1;
  DECLARE @CustomerId int = 0;
  DECLARE @Region nvarchar(16);
  DECLARE @Priority tinyint = 0;
  DECLARE @Sku nvarchar(32);
  DECLARE @Category nvarchar(16);
  DECLARE @Quantity int = 0;
  DECLARE @UnitPrice decimal(18,2) = 0;
  DECLARE @PriceBase decimal(18,2) = 0;
  DECLARE @Mix int = 0;
  DECLARE @LinesPerOrder int = 3;

  SET @CreatedLines = 0;
  WHILE @OrderId <= @OrderCount
  BEGIN
    SET @CustomerId = 1000 + @Seed + @OrderId;
    SET @Region = CASE @OrderId % 3
      WHEN 1 THEN N'North' WHEN 2 THEN N'South' ELSE N'West' END;
    SET @Priority = CASE WHEN @OrderId % 2 = 0 THEN 1 ELSE 0 END;
    INSERT #Orders (OrderId, CustomerId, Region, Priority, Status)
    VALUES (@OrderId, @CustomerId, @Region, @Priority, N'New');
    SET @LineNo = 1;

    WHILE @LineNo <= @LinesPerOrder
    BEGIN
      SET @Mix = (@Seed + @OrderId * 7 + @LineNo * 3) % 11;
      SET @Quantity = 1 + @Mix % 8;
      SET @Category = CASE @LineNo
        WHEN 1 THEN N'Hardware' WHEN 2 THEN N'Office' ELSE N'Service' END;
      SET @PriceBase = CASE @Category
        WHEN N'Hardware' THEN 29.95 WHEN N'Office' THEN 8.50 ELSE 15.00 END;
      SET @UnitPrice = @PriceBase + @Mix * 1.25;
      SET @Sku = CONCAT(N'SKU-', @OrderId, N'-', @LineNo);

      IF @Scenario = N'edge' AND @OrderId = 1
      BEGIN
        IF @LineNo = 1 SET @Quantity = 0;
        IF @LineNo = 2 SET @UnitPrice = 0;
        IF @LineNo = 3 SET @Quantity = 12;
      END;
      IF @Scenario = N'error' AND @OrderId = 1 AND @LineNo = 2
        SET @Quantity = -1;

      INSERT #Lines (OrderId, LineNumber, Sku, Category, Quantity, UnitPrice)
      VALUES (@OrderId, @LineNo, @Sku, @Category, @Quantity, @UnitPrice);
      SET @CreatedLines = @CreatedLines + 1;
      SET @LineNo = @LineNo + 1;
    END;

    SET @OrderId = @OrderId + 1;
  END;
  IF @Verbose = 1
    PRINT CONCAT(N'[BuildOrders] created ', @OrderCount, N' orders and ', @CreatedLines, N' lines');
  RETURN @OrderCount;
END;
GO

CREATE PROCEDURE SqlDebugTest.Run
  @Seed int = 7,
  @OrderCount int = 3,
  @Scenario nvarchar(16) = N'normal',
  @Verbose bit = 1,
  @Rethrow bit = 0
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @CreatedLines int = 0;
  DECLARE @OrderId int = 1;
  DECLARE @OrderTotal decimal(18,2) = 0;
  DECLARE @GrandTotal decimal(18,2) = 0;
  DECLARE @ReviewRequired bit = 0;
  DECLARE @ReviewCount int = 0;
  DECLARE @CompletedCount int = 0;
  DECLARE @ProcessReturn int = 0;
  DECLARE @ErrorNumber int = NULL;
  DECLARE @ErrorLine int = NULL;
  DECLARE @ErrorProcedure nvarchar(128) = NULL;
  DECLARE @ErrorMessage nvarchar(2048) = NULL;
  DECLARE @RunLabel nvarchar(100);

  IF @Seed IS NULL OR @Seed < 0 OR @Seed > 10000
    THROW 51008, 'Seed must be between 0 and 10000.', 1;
  IF @OrderCount IS NULL OR @OrderCount < 1 OR @OrderCount > 12
    THROW 51009, 'OrderCount must be between 1 and 12.', 1;
  IF @Scenario IS NULL OR @Scenario NOT IN (N'normal', N'edge', N'error')
    THROW 51010, 'Scenario must be normal, edge, or error.', 1;
  SET @RunLabel = CONCAT(N'seed=', @Seed, N'; scenario=', @Scenario);

  CREATE TABLE #Orders
  (
    OrderId int NOT NULL PRIMARY KEY,
    CustomerId int NOT NULL,
    Region nvarchar(16) NOT NULL,
    Priority tinyint NOT NULL,
    Status nvarchar(20) NOT NULL,
    Subtotal decimal(18,2) NULL,
    Tax decimal(18,2) NULL,
    Shipping decimal(18,2) NULL,
    Total decimal(18,2) NULL,
    RiskScore int NULL
  );
  CREATE TABLE #Lines
  (
    OrderId int NOT NULL,
    LineNumber int NOT NULL,
    Sku nvarchar(32) NOT NULL,
    Category nvarchar(16) NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    Gross decimal(18,2) NULL,
    DiscountRate decimal(9,4) NULL,
    DiscountAmount decimal(18,2) NULL,
    Net decimal(18,2) NULL,
    Tax decimal(18,2) NULL,
    LineTotal decimal(18,2) NULL,
    RiskScore int NULL,
    Decision nvarchar(20) NULL,
    PRIMARY KEY (OrderId, LineNumber)
  );
  CREATE TABLE #Trace
  (
    Sequence int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Depth int NOT NULL,
    ModuleName nvarchar(40) NOT NULL,
    Marker nvarchar(40) NOT NULL,
    Amount decimal(18,4) NULL,
    Detail nvarchar(400) NOT NULL
  );

  PRINT CONCAT(N'[Run] starting ', @RunLabel);
  BEGIN TRY
    EXEC SqlDebugTest.BuildOrders
      @Seed = @Seed, @OrderCount = @OrderCount, @Scenario = @Scenario,
      @CreatedLines = @CreatedLines OUTPUT, @Verbose = @Verbose;

    WHILE @OrderId <= @OrderCount
    BEGIN
      SET @OrderTotal = 0;
      SET @ReviewRequired = 0;
      EXEC @ProcessReturn = SqlDebugTest.ProcessOrder
        @OrderId = @OrderId, @OrderTotal = @OrderTotal OUTPUT,
        @ReviewRequired = @ReviewRequired OUTPUT, @Verbose = @Verbose;

      SET @GrandTotal = @GrandTotal + @OrderTotal;
      SET @CompletedCount = @CompletedCount + 1;
      IF @ReviewRequired = 1
        SET @ReviewCount = @ReviewCount + 1;
      INSERT #Trace (Depth, ModuleName, Marker, Amount, Detail)
      VALUES (1, N'Run', N'order-complete', @GrandTotal,
        CONCAT(N'order=', @OrderId, N'; processed lines=', @ProcessReturn));
      SET @OrderId = @OrderId + 1;
    END;
  END TRY
  BEGIN CATCH
    SET @ErrorNumber = ERROR_NUMBER();
    SET @ErrorLine = ERROR_LINE();
    SET @ErrorProcedure = ERROR_PROCEDURE();
    SET @ErrorMessage = ERROR_MESSAGE();
    UPDATE #Orders SET Status = N'Failed' WHERE OrderId = @OrderId;
    INSERT #Trace (Depth, ModuleName, Marker, Amount, Detail)
    VALUES (1, N'Run', N'caught-error', NULL,
      CONCAT(@ErrorNumber, N' in ', @ErrorProcedure, N': ', @ErrorMessage));
    PRINT CONCAT(N'[Run CATCH] ', @ErrorNumber, N': ', @ErrorMessage);
    IF @Rethrow = 1 THROW;
  END CATCH;

  SELECT @RunLabel AS RunLabel, @CreatedLines AS CreatedLines,
    @CompletedCount AS CompletedOrders, @ReviewCount AS ReviewOrders,
    @GrandTotal AS GrandTotal, @ErrorNumber AS ErrorNumber,
    @ErrorProcedure AS ErrorProcedure, @ErrorLine AS ErrorLine,
    @ErrorMessage AS ErrorMessage;
  SELECT OrderId, CustomerId, Region, Priority, Status,
    Subtotal, Tax, Shipping, Total, RiskScore
  FROM #Orders ORDER BY OrderId;
  SELECT OrderId, LineNumber, Sku, Category, Quantity, UnitPrice, Gross,
    DiscountRate, DiscountAmount, Net, Tax, LineTotal, RiskScore, Decision
  FROM #Lines ORDER BY OrderId, LineNumber;
  SELECT Sequence, Depth, ModuleName, Marker, Amount, Detail
  FROM #Trace ORDER BY Sequence;

  PRINT CONCAT(N'[Run] finished; completed=', @CompletedCount,
    N'; review=', @ReviewCount, N'; grand total=', @GrandTotal);
  RETURN CASE WHEN @ErrorNumber IS NULL THEN 0 ELSE 1 END;
END;
GO
COMMIT TRANSACTION;
GO
