$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$diagRoot = Join-Path $repoRoot '.diag\ui-live-browser-smoke'
$webStdOutPath = Join-Path $diagRoot 'web.stdout.log'
$webStdErrPath = Join-Path $diagRoot 'web.stderr.log'
$browserSummaryPath = Join-Path $diagRoot 'ui-live-browser-summary.json'
$summaryPath = Join-Path $diagRoot 'ui-live-runtime-smoke-summary.json'

function New-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ($listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Wait-Until {
    param([string]$Name, [scriptblock]$Condition, [int]$TimeoutSeconds = 60, [int]$PollMilliseconds = 500)

    $startedAt = Get-Date
    $lastError = $null

    while (((Get-Date) - $startedAt).TotalSeconds -lt $TimeoutSeconds) {
        try {
            $result = & $Condition
            if ($result) { return $result }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }

    if ($lastError) {
        throw "Timed out while waiting for $Name. Last error: $($lastError.Exception.Message)"
    }

    throw "Timed out while waiting for $Name."
}

function New-SqlConnection {
    param([string]$ConnectionString)
    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery {
    param([string]$ConnectionString, [string]$CommandText, [hashtable]$Parameters = @{})
    $connection = New-SqlConnection -ConnectionString $ConnectionString
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        foreach ($entry in $Parameters.GetEnumerator()) {
            $null = $command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value ?? [DBNull]::Value)
        }

        return $command.ExecuteNonQuery()
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqlRow {
    param([string]$ConnectionString, [string]$CommandText, [hashtable]$Parameters = @{})
    $connection = New-SqlConnection -ConnectionString $ConnectionString
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        foreach ($entry in $Parameters.GetEnumerator()) {
            $null = $command.Parameters.AddWithValue("@$($entry.Key)", $entry.Value ?? [DBNull]::Value)
        }

        $reader = $command.ExecuteReader()
        try {
            if (-not $reader.Read()) { return $null }
            $row = [ordered]@{}
            for ($index = 0; $index -lt $reader.FieldCount; $index++) {
                $row[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
            }
            return [pscustomobject]$row
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Ensure-SqlDatabaseExists {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) { throw 'Database name is required for UI live smoke bootstrap.' }

    $masterConnection = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace("]", "]]" )
    Invoke-SqlNonQuery -ConnectionString $masterConnection -CommandText "IF DB_ID(N'$databaseName') IS NULL BEGIN EXEC(N'CREATE DATABASE [$escapedDatabaseName]') END;" | Out-Null
}

function Start-ManagedProcess {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [string]$StandardOutputPath, [string]$StandardErrorPath, [hashtable]$EnvironmentVariables)

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath -Environment $EnvironmentVariables -PassThru -WindowStyle Hidden
    return [pscustomobject]@{ Process = $process }
}

function Stop-ManagedProcess {
    param($Handle)
    if ($null -eq $Handle) { return }

    $process = $Handle.Process
    try {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $null = $process.WaitForExit(5000)
        }
    }
    catch {
    }
    finally {
        try { $process.Dispose() } catch {}
    }
}

function Remove-SqlDatabase {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $databaseName = $builder.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($databaseName)) { return }

    $masterConnection = $ConnectionString -replace 'Database=[^;]+', 'Database=master'
    $escapedDatabaseName = $databaseName.Replace("]", "]]" )
    Invoke-SqlNonQuery -ConnectionString $masterConnection -CommandText "IF DB_ID(N'$databaseName') IS NOT NULL BEGIN ALTER DATABASE [$escapedDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$escapedDatabaseName]; END;" | Out-Null
}

function Get-SmokeUser {
    param([string]$ConnectionString, [string]$Email)
    return Invoke-SqlRow -ConnectionString $ConnectionString -CommandText 'SELECT TOP (1) Id, Email FROM AspNetUsers WHERE NormalizedEmail = UPPER(@Email);' -Parameters @{ Email = $Email }
}

function Seed-LiveUiGraph {
    param(
        [string]$ConnectionString,
        [string]$UserId,
        [guid]$StrategyId,
        [guid]$StrategyVersionId,
        [guid]$StrategySignalId,
        [guid]$ExchangeAccountId,
        [guid]$BotId,
        [guid]$FeatureSnapshotId,
        [guid]$DecisionId,
        [guid]$OutcomeId,
        [guid]$FilledOrderId,
        [guid]$RejectOrderId,
        [guid]$DecisionTraceId,
        [guid]$ExecutionTraceId,
        [guid]$AuditLogId,
        [guid]$RiskProfileId,
        [guid]$ExchangeBalanceId,
        [guid]$ExchangePositionId,
        [guid]$SyncStateId,
        [guid]$DemoPositionId,
        [guid]$DemoLedgerId,
        [guid]$FilledTransitionId,
        [guid]$RejectTransitionId,
        [datetime]$NowUtc)

    $filledOpenedAtUtc = $NowUtc.AddMinutes(-30)
    $filledClosedAtUtc = $NowUtc.AddMinutes(-27)
    $rejectAtUtc = $NowUtc.AddMinutes(-5)
    $confidenceJson = '{"scorePercentage":78,"band":"High","matchedRuleCount":5,"totalRuleCount":6,"isDeterministic":true,"isRiskApproved":true,"isVetoed":false,"riskReasonCode":"None","isVirtualRiskCheck":false,"summary":"AI overlay boosted the signal.","aiOverlayDisposition":"Boost","aiOverlayBoostPoints":5}'

    Invoke-SqlNonQuery -ConnectionString $ConnectionString -CommandText @"
IF EXISTS (SELECT 1 FROM GlobalExecutionSwitches WHERE Id = '0F4D61F5-595D-4C35-9B21-3D87A0F1D001')
    UPDATE GlobalExecutionSwitches SET TradeMasterState = 'Disarmed', DemoModeEnabled = 1, LiveModeApprovedAtUtc = NULL, LiveModeApprovalReference = NULL, UpdatedDate = @NowUtc, IsDeleted = 0 WHERE Id = '0F4D61F5-595D-4C35-9B21-3D87A0F1D001';
ELSE
    INSERT INTO GlobalExecutionSwitches (Id, TradeMasterState, DemoModeEnabled, LiveModeApprovedAtUtc, LiveModeApprovalReference, CreatedDate, UpdatedDate, IsDeleted) VALUES ('0F4D61F5-595D-4C35-9B21-3D87A0F1D001', 'Disarmed', 1, NULL, NULL, @NowUtc, @NowUtc, 0);

INSERT INTO RiskProfiles (Id, OwnerUserId, ProfileName, MaxDailyLossPercentage, MaxPositionSizePercentage, MaxLeverage, KillSwitchEnabled, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@RiskProfileId, @UserId, 'UI Live Smoke', 10.0000, 100.0000, 2.0000, 0, @NowUtc, @NowUtc, 0);

INSERT INTO ExchangeAccounts (Id, OwnerUserId, ExchangeName, DisplayName, IsReadOnly, LastValidatedAt, ApiKeyCiphertext, ApiSecretCiphertext, CredentialFingerprint, CredentialKeyVersion, CredentialStatus, CredentialStoredAtUtc, CredentialLastAccessedAtUtc, CredentialLastRotatedAtUtc, CredentialRevalidateAfterUtc, CredentialRotateAfterUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@ExchangeAccountId, @UserId, 'Binance', 'UI Live Smoke Binance', 0, @NowUtc, 'cipher-api-key', 'cipher-api-secret', 'FINGERPRINT', 'credential-v1', 'Active', @NowUtc, NULL, @NowUtc, DATEADD(DAY, 30, @NowUtc), DATEADD(DAY, 90, @NowUtc), @NowUtc, @NowUtc, 0);

INSERT INTO ExchangeBalances (Id, OwnerUserId, ExchangeAccountId, Plane, Asset, WalletBalance, CrossWalletBalance, AvailableBalance, MaxWithdrawAmount, LockedBalance, ExchangeUpdatedAtUtc, SyncedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@ExchangeBalanceId, @UserId, @ExchangeAccountId, 'Futures', 'USDT', 1500.0000, 1500.0000, 1200.0000, 1200.0000, 0, @NowUtc, @NowUtc, @NowUtc, @NowUtc, 0);

INSERT INTO ExchangePositions (Id, OwnerUserId, ExchangeAccountId, Plane, Symbol, PositionSide, Quantity, EntryPrice, BreakEvenPrice, UnrealizedProfit, MarginType, IsolatedWallet, ExchangeUpdatedAtUtc, SyncedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@ExchangePositionId, @UserId, @ExchangeAccountId, 'Futures', 'BTCUSDT', 'LONG', 0.50000000, 60000.00000000, 60010.00000000, 250.00000000, 'cross', 0, @NowUtc, @NowUtc, @NowUtc, @NowUtc, 0);

INSERT INTO ExchangeAccountSyncStates (Id, OwnerUserId, ExchangeAccountId, Plane, PrivateStreamConnectionState, LastListenKeyStartedAtUtc, LastListenKeyRenewedAtUtc, LastPrivateStreamEventAtUtc, LastBalanceSyncedAtUtc, LastPositionSyncedAtUtc, LastStateReconciledAtUtc, DriftStatus, DriftSummary, LastDriftDetectedAtUtc, ConsecutiveStreamFailureCount, LastErrorCode, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@SyncStateId, @UserId, @ExchangeAccountId, 'Futures', 'Connected', @NowUtc, @NowUtc, @NowUtc, @NowUtc, @NowUtc, @NowUtc, 'InSync', 'Private plane synced.', NULL, 0, NULL, @NowUtc, @NowUtc, 0);

IF EXISTS (SELECT 1 FROM DegradedModeStates WHERE Id = '3E17E8EF-3A73-45CC-8C32-A11FA55178D7')
    UPDATE DegradedModeStates SET StateCode = 'Normal', ReasonCode = 'None', SignalFlowBlocked = 0, ExecutionFlowBlocked = 0, LatestDataTimestampAtUtc = @NowUtc, LatestHeartbeatReceivedAtUtc = @NowUtc, LatestClockDriftMilliseconds = 50, LastStateChangedAtUtc = @NowUtc, LatestHeartbeatSource = 'shared-cache:kline', LatestSymbol = 'BTCUSDT', LatestTimeframe = '1m', LatestExpectedOpenTimeUtc = NULL, LatestContinuityGapCount = 0, LatestContinuityGapStartedAtUtc = NULL, LatestContinuityGapLastSeenAtUtc = NULL, LatestContinuityRecoveredAtUtc = @NowUtc, UpdatedDate = @NowUtc, IsDeleted = 0 WHERE Id = '3E17E8EF-3A73-45CC-8C32-A11FA55178D7';
ELSE
    INSERT INTO DegradedModeStates (Id, StateCode, ReasonCode, SignalFlowBlocked, ExecutionFlowBlocked, LatestDataTimestampAtUtc, LatestHeartbeatReceivedAtUtc, LatestClockDriftMilliseconds, LastStateChangedAtUtc, LatestHeartbeatSource, LatestSymbol, LatestTimeframe, LatestExpectedOpenTimeUtc, LatestContinuityGapCount, LatestContinuityGapStartedAtUtc, LatestContinuityGapLastSeenAtUtc, LatestContinuityRecoveredAtUtc, CreatedDate, UpdatedDate, IsDeleted) VALUES ('3E17E8EF-3A73-45CC-8C32-A11FA55178D7', 'Normal', 'None', 0, 0, @NowUtc, @NowUtc, 50, @NowUtc, 'shared-cache:kline', 'BTCUSDT', '1m', NULL, 0, NULL, NULL, @NowUtc, @NowUtc, @NowUtc, 0);

INSERT INTO TradingBots (Id, OwnerUserId, Name, StrategyKey, Symbol, Quantity, ExchangeAccountId, Leverage, MarginType, IsEnabled, TradingModeOverride, TradingModeApprovedAtUtc, TradingModeApprovalReference, OpenOrderCount, OpenPositionCount, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@BotId, @UserId, 'UI Live Smoke Bot', 'ui-live-smoke', 'BTCUSDT', 0.50000000, @ExchangeAccountId, 1.0000, 'ISOLATED', 1, NULL, NULL, NULL, 0, 1, @NowUtc, @NowUtc, 0);

INSERT INTO TradingStrategies (Id, OwnerUserId, StrategyKey, DisplayName, UsesExplicitVersionLifecycle, ActiveTradingStrategyVersionId, ActiveVersionActivatedAtUtc, PromotionState, PublishedMode, PublishedAtUtc, LivePromotionApprovedAtUtc, LivePromotionApprovalReference, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@StrategyId, @UserId, 'ui-live-smoke', 'UI Live Smoke Strategy', 0, NULL, NULL, 'LivePublished', 'Demo', @NowUtc, NULL, NULL, @NowUtc, @NowUtc, 0);

INSERT INTO TradingStrategyVersions (Id, OwnerUserId, TradingStrategyId, SchemaVersion, VersionNumber, Status, DefinitionJson, PublishedAtUtc, ArchivedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@StrategyVersionId, @UserId, @StrategyId, 1, 1, 'Published', '{}', @NowUtc, NULL, @NowUtc, @NowUtc, 0);

INSERT INTO TradingStrategySignals (Id, OwnerUserId, TradingStrategyId, TradingStrategyVersionId, StrategyVersionNumber, StrategySchemaVersion, SignalType, ExecutionEnvironment, Symbol, Timeframe, IndicatorOpenTimeUtc, IndicatorCloseTimeUtc, IndicatorReceivedAtUtc, GeneratedAtUtc, ExplainabilitySchemaVersion, IndicatorSnapshotJson, RuleResultSnapshotJson, RiskEvaluationJson, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@StrategySignalId, @UserId, @StrategyId, @StrategyVersionId, 1, 1, 'Entry', 'Demo', 'BTCUSDT', '1m', DATEADD(MINUTE, -1, @NowUtc), @NowUtc, @NowUtc, @NowUtc, 1, '{}', '{}', @ConfidenceJson, @NowUtc, @NowUtc, 0);

INSERT INTO TradingFeatureSnapshots (Id, OwnerUserId, BotId, ExchangeAccountId, StrategyKey, Symbol, Timeframe, EvaluatedAtUtc, MarketDataTimestampUtc, FeatureVersion, SnapshotState, QualityReasonCode, MissingFeatureSummary, MarketDataReasonCode, SampleCount, RequiredSampleCount, ReferencePrice, Ema20, Ema50, Ema200, Alma, Frama, Rsi, MacdLine, MacdSignal, MacdHistogram, KdjK, KdjD, KdjJ, FisherTransform, Atr, BollingerPercentB, BollingerBandWidth, KeltnerChannelRelation, PmaxValue, ChandelierExit, VolumeSpikeRatio, RelativeVolume, Obv, Mfi, KlingerOscillator, KlingerSignal, Plane, TradingMode, HasOpenPosition, IsInCooldown, LastVetoReasonCode, LastDecisionOutcome, LastDecisionCode, LastExecutionState, LastFailureCode, FeatureSummary, TopSignalHints, PrimaryRegime, MomentumBias, VolatilityState, NormalizationMeta, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@FeatureSnapshotId, @UserId, @BotId, @ExchangeAccountId, 'ui-live-smoke', 'BTCUSDT', '1m', @NowUtc, @NowUtc, 'AI-1.v1', 'Ready', 'None', NULL, 'None', 64, 50, 60500.00000000, 60200.00000000, 60050.00000000, 59750.00000000, 60300.00000000, 60190.00000000, 61.50000000, 120.00000000, 110.00000000, 10.00000000, 78.00000000, 72.00000000, 90.00000000, 0.60000000, 320.00000000, 0.72000000, 0.14000000, 1.12000000, 60350.00000000, 59880.00000000, 1.40000000, 1.25000000, 1500.00000000, 64.00000000, 8.00000000, 6.50000000, 'Futures', 'Demo', 1, 0, NULL, 'Persisted', 'StrategyEntry', 'Rejected', 'TradeMasterDisarmed', 'EMA stack and volume support long bias.', 'Trend aligned.', 'Trending', 'Bullish', 'Expanding', '{"quality":"Ready"}', @NowUtc, @NowUtc, 0);

INSERT INTO AiShadowDecisions (Id, OwnerUserId, BotId, ExchangeAccountId, TradingStrategyId, TradingStrategyVersionId, StrategySignalId, StrategySignalVetoId, FeatureSnapshotId, StrategyDecisionTraceId, HypotheticalDecisionTraceId, CorrelationId, StrategyKey, Symbol, Timeframe, EvaluatedAtUtc, MarketDataTimestampUtc, FeatureVersion, StrategyDirection, StrategyConfidenceScore, StrategyDecisionOutcome, StrategyDecisionCode, StrategySummary, AiDirection, AiConfidence, AiReasonSummary, AiProviderName, AiProviderModel, AiLatencyMs, AiIsFallback, AiFallbackReason, RiskVetoPresent, RiskVetoReason, RiskVetoSummary, PilotSafetyBlocked, PilotSafetyReason, PilotSafetySummary, TradingMode, Plane, FinalAction, HypotheticalSubmitAllowed, HypotheticalBlockReason, HypotheticalBlockSummary, NoSubmitReason, FeatureSummary, AgreementState, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@DecisionId, @UserId, @BotId, @ExchangeAccountId, @StrategyId, @StrategyVersionId, @StrategySignalId, NULL, @FeatureSnapshotId, NULL, NULL, 'ui-live-correlation-01', 'ui-live-smoke', 'BTCUSDT', '1m', @NowUtc, @NowUtc, 'AI-1.v1', 'Long', 78, 'Persisted', 'StrategyEntry', 'Strategy favored long.', 'Long', 0.78000000, 'AI liked the long setup.', 'DeterministicStub', 'stub-v1', 9, 0, NULL, 0, NULL, NULL, 0, NULL, NULL, 'Demo', 'Futures', 'NoSubmit', 0, 'TradeMasterDisarmed', 'Global trade master is disarmed.', 'TradeMasterDisarmed', 'Feature summary.', 'Agreement', @NowUtc, @NowUtc, 0);

INSERT INTO AiShadowDecisionOutcomes (Id, OwnerUserId, AiShadowDecisionId, BotId, Symbol, Timeframe, DecisionEvaluatedAtUtc, HorizonKind, HorizonValue, OutcomeState, OutcomeScore, RealizedDirectionality, ConfidenceBucket, FutureDataAvailability, ReferenceCandleCloseTimeUtc, FutureCandleCloseTimeUtc, ReferenceClosePrice, FutureClosePrice, RealizedReturn, FalsePositive, FalseNeutral, Overtrading, SuppressionCandidate, SuppressionAligned, ScoredAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@OutcomeId, @UserId, @DecisionId, @BotId, 'BTCUSDT', '1m', @NowUtc, 'BarsForward', 1, 'Scored', 0.64000000, 'Long', 'High', 'Available', DATEADD(MINUTE, -1, @NowUtc), @NowUtc, 60000.00000000, 60384.00000000, 0.00640000, 0, 0, 0, 1, 1, @NowUtc, @NowUtc, @NowUtc, 0);

INSERT INTO DemoPositions (Id, OwnerUserId, BotId, PositionScopeKey, Symbol, BaseAsset, QuoteAsset, PositionKind, MarginMode, Leverage, Quantity, CostBasis, AverageEntryPrice, RealizedPnl, UnrealizedPnl, TotalFeesInQuote, NetFundingInQuote, IsolatedMargin, MaintenanceMarginRate, MaintenanceMargin, MarginBalance, LiquidationPrice, LastMarkPrice, LastPrice, LastFillPrice, LastFundingRate, LastFilledAtUtc, LastValuationAtUtc, LastFundingAppliedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@DemoPositionId, @UserId, @BotId, 'ui-live-smoke', 'BTCUSDT', 'BTC', 'USDT', 'Futures', 'Cross', 1.00000000, 0, 0, 0, 42.50000000, 0, 1.25000000, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, @FilledClosedAtUtc, @FilledClosedAtUtc, NULL, @FilledOpenedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO DemoLedgerTransactions (Id, OwnerUserId, OperationId, TransactionType, BotId, PositionScopeKey, OrderId, FillId, Symbol, BaseAsset, QuoteAsset, PositionKind, MarginMode, Side, Quantity, Price, FeeAsset, FeeAmount, FeeAmountInQuote, Leverage, FundingRate, FundingDeltaInQuote, RealizedPnlDelta, PositionQuantityAfter, PositionCostBasisAfter, PositionAverageEntryPriceAfter, CumulativeRealizedPnlAfter, UnrealizedPnlAfter, CumulativeFeesInQuoteAfter, NetFundingInQuoteAfter, LastPriceAfter, MarkPriceAfter, MaintenanceMarginRateAfter, MaintenanceMarginAfter, MarginBalanceAfter, LiquidationPriceAfter, OccurredAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@DemoLedgerId, @UserId, 'execution-fill:' + CONVERT(nvarchar(36), @FilledOrderId), 'FillApplied', @BotId, 'ui-live-smoke', CONVERT(nvarchar(36), @FilledOrderId), 'demo-fill-1', 'BTCUSDT', 'BTC', 'USDT', 'Futures', 'Cross', 'Sell', 0.50000000, 65100.00000000, 'USDT', 1.25000000, 1.25000000, NULL, NULL, NULL, 42.50000000, 0, 0, 0, 42.50000000, 0, 1.25000000, 0, NULL, NULL, NULL, NULL, NULL, NULL, @FilledClosedAtUtc, @FilledClosedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO ExecutionOrders (Id, OwnerUserId, TradingStrategyId, TradingStrategyVersionId, StrategySignalId, SignalType, BotId, ExchangeAccountId, Plane, StrategyKey, Symbol, Timeframe, BaseAsset, QuoteAsset, Side, OrderType, Quantity, Price, FilledQuantity, AverageFillPrice, LastFilledAtUtc, StopLossPrice, TakeProfitPrice, ReduceOnly, ReplacesExecutionOrderId, ExecutionEnvironment, ExecutorKind, State, IdempotencyKey, RootCorrelationId, ParentCorrelationId, ExternalOrderId, FailureCode, FailureDetail, RejectionStage, SubmittedToBroker, RetryEligible, CooldownApplied, DuplicateSuppressed, SubmittedAtUtc, LastReconciledAtUtc, ReconciliationStatus, ReconciliationSummary, LastDriftDetectedAtUtc, LastStateChangedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@FilledOrderId, @UserId, @StrategyId, @StrategyVersionId, @StrategySignalId, 'Entry', @BotId, NULL, 'Futures', 'ui-live-smoke', 'BTCUSDT', '1m', 'BTC', 'USDT', 'Buy', 'Market', 0.50000000, 65000.00000000, 0.50000000, 65100.00000000, @FilledClosedAtUtc, NULL, NULL, 0, NULL, 'Demo', 'Virtual', 'Filled', 'ui-live-filled-idem', 'ui-live-root-filled', NULL, CONVERT(nvarchar(36), @FilledOrderId), NULL, NULL, 'None', 1, 0, 1, 0, @FilledOpenedAtUtc, @FilledClosedAtUtc, 'InSync', 'Portfolio reconciliation matched.', NULL, @FilledClosedAtUtc, @FilledOpenedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO ExecutionOrderTransitions (Id, OwnerUserId, ExecutionOrderId, SequenceNumber, State, EventCode, Detail, CorrelationId, ParentCorrelationId, OccurredAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@FilledTransitionId, @UserId, @FilledOrderId, 1, 'Filled', 'DemoFillSimulated', 'ClientOrderId=ui_live_demo_fill_01; Plane=Futures; ExecutedQuantity=0.5; CumulativeQuoteQuantity=32550; TradeId=77; Fee=USDT:1.25; ReconciliationStatus=InSync; ReconciliationSummary=Portfolio reconciliation matched.', 'ui-live-transition-filled', 'ui-live-root-filled', @FilledClosedAtUtc, @FilledClosedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO DecisionTraces (Id, StrategySignalId, CorrelationId, DecisionId, UserId, Symbol, Timeframe, StrategyVersion, SignalType, RiskScore, DecisionOutcome, DecisionReasonType, DecisionReasonCode, DecisionSummary, DecisionAtUtc, VetoReasonCode, LatencyMs, LastCandleAtUtc, DataAgeMs, StaleThresholdMs, StaleReason, ContinuityState, ContinuityGapCount, ContinuityGapStartedAtUtc, ContinuityGapLastSeenAtUtc, ContinuityRecoveredAtUtc, SnapshotJson, CreatedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@DecisionTraceId, @StrategySignalId, 'ui-live-root-filled', 'ui-live-decision', @UserId, 'BTCUSDT', '1m', '1', 'Entry', 78, 'Allowed', NULL, NULL, 'Strategy allowed the demo fill.', @FilledOpenedAtUtc, NULL, 5, @FilledOpenedAtUtc, 0, 2000, NULL, NULL, 0, NULL, NULL, NULL, '{}', @FilledOpenedAtUtc, @FilledOpenedAtUtc, @FilledOpenedAtUtc, 0);

INSERT INTO ExecutionTraces (Id, ExecutionOrderId, CorrelationId, ExecutionAttemptId, CommandId, UserId, Provider, Endpoint, RequestMasked, ResponseMasked, HttpStatusCode, ExchangeCode, LatencyMs, CreatedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@ExecutionTraceId, @FilledOrderId, 'ui-live-root-filled', 'ui-live-exec-attempt', 'ui-live-exec-command', @UserId, 'virtual', 'DispatchAsync', NULL, 'Accepted', NULL, NULL, NULL, @FilledClosedAtUtc, @FilledClosedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO AuditLogs (Id, Actor, Action, Target, Context, CorrelationId, Outcome, Environment, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@AuditLogId, 'system', 'TradeExecution.Dispatch', 'ExecutionOrder/' + CONVERT(nvarchar(36), @FilledOrderId), 'ui-live-smoke', 'ui-live-root-filled', 'Allowed', 'Demo', @FilledClosedAtUtc, @FilledClosedAtUtc, 0);

INSERT INTO ExecutionOrders (Id, OwnerUserId, TradingStrategyId, TradingStrategyVersionId, StrategySignalId, SignalType, BotId, ExchangeAccountId, Plane, StrategyKey, Symbol, Timeframe, BaseAsset, QuoteAsset, Side, OrderType, Quantity, Price, FilledQuantity, AverageFillPrice, LastFilledAtUtc, StopLossPrice, TakeProfitPrice, ReduceOnly, ReplacesExecutionOrderId, ExecutionEnvironment, ExecutorKind, State, IdempotencyKey, RootCorrelationId, ParentCorrelationId, ExternalOrderId, FailureCode, FailureDetail, RejectionStage, SubmittedToBroker, RetryEligible, CooldownApplied, DuplicateSuppressed, SubmittedAtUtc, LastReconciledAtUtc, ReconciliationStatus, ReconciliationSummary, LastDriftDetectedAtUtc, LastStateChangedAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@RejectOrderId, @UserId, @StrategyId, @StrategyVersionId, @StrategySignalId, 'Entry', @BotId, @ExchangeAccountId, 'Futures', 'ui-live-smoke', 'BTCUSDT', '1m', 'BTC', 'USDT', 'Buy', 'Market', 0.25000000, 65200.00000000, 0, NULL, NULL, NULL, NULL, 0, NULL, 'Demo', 'Binance', 'Rejected', 'ui-live-reject-idem', 'ui-live-root-reject', NULL, NULL, 'TradeMasterDisarmed', 'Execution blocked because kill switch is off.', 'PreSubmit', 0, 0, 0, 0, NULL, @RejectAtUtc, 'InSync', 'Rejected before submit.', NULL, @RejectAtUtc, @RejectAtUtc, @RejectAtUtc, 0);

INSERT INTO ExecutionOrderTransitions (Id, OwnerUserId, ExecutionOrderId, SequenceNumber, State, EventCode, Detail, CorrelationId, ParentCorrelationId, OccurredAtUtc, CreatedDate, UpdatedDate, IsDeleted)
VALUES (@RejectTransitionId, @UserId, @RejectOrderId, 1, 'Rejected', 'GateRejected', 'FailureCode=TradeMasterDisarmed; Stage=PreSubmit; Summary=Execution blocked because kill switch is off.', 'ui-live-transition-reject', 'ui-live-root-reject', @RejectAtUtc, @RejectAtUtc, @RejectAtUtc, 0);
"@ -Parameters @{
        UserId = $UserId
        StrategyId = $StrategyId
        StrategyVersionId = $StrategyVersionId
        StrategySignalId = $StrategySignalId
        ExchangeAccountId = $ExchangeAccountId
        BotId = $BotId
        FeatureSnapshotId = $FeatureSnapshotId
        DecisionId = $DecisionId
        OutcomeId = $OutcomeId
        FilledOrderId = $FilledOrderId
        RejectOrderId = $RejectOrderId
        DecisionTraceId = $DecisionTraceId
        ExecutionTraceId = $ExecutionTraceId
        AuditLogId = $AuditLogId
        RiskProfileId = $RiskProfileId
        ExchangeBalanceId = $ExchangeBalanceId
        ExchangePositionId = $ExchangePositionId
        SyncStateId = $SyncStateId
        DemoPositionId = $DemoPositionId
        DemoLedgerId = $DemoLedgerId
        FilledTransitionId = $FilledTransitionId
        RejectTransitionId = $RejectTransitionId
        NowUtc = $NowUtc
        FilledOpenedAtUtc = $filledOpenedAtUtc
        FilledClosedAtUtc = $filledClosedAtUtc
        RejectAtUtc = $rejectAtUtc
        ConfidenceJson = $confidenceJson
    } | Out-Null
}

if (Test-Path $diagRoot) {
    Remove-Item $diagRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $diagRoot | Out-Null

$databaseName = 'CoinBotUiLiveSmoke_' + [Guid]::NewGuid().ToString('N')
$connectionString = 'Server=(localdb)\MSSQLLocalDB;Database=' + $databaseName + ';Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True'
Ensure-SqlDatabaseExists -ConnectionString $connectionString
$webPort = New-FreeTcpPort
$baseUrl = 'http://127.0.0.1:' + $webPort
$registrationEmail = 'ui.live.smoke.' + [Guid]::NewGuid().ToString('N') + '@coinbot.test'
$registrationPassword = 'Passw0rd!Smoke1'

$environmentVariables = @{
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.dotnet')
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    ASPNETCORE_ENVIRONMENT = 'Development'
    DOTNET_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $baseUrl
    ConnectionStrings__DefaultConnection = $connectionString
    MarketData__Binance__Enabled = 'false'
    ExchangeSync__Binance__Enabled = 'false'
    JobOrchestration__Enabled = 'false'
}

$summary = [ordered]@{
    SmokeDatabaseName = $databaseName
    BaseUrl = $baseUrl
    RegistrationEmail = $registrationEmail
    BrowserSummaryPath = $browserSummaryPath
    Ui = $null
}

$webHandle = $null

try {
    $webHandle = Start-ManagedProcess -FilePath 'dotnet' -ArgumentList @('run', '--project', 'src/CoinBot.Web/CoinBot.Web.csproj', '--no-build', '--no-launch-profile') -WorkingDirectory $repoRoot -StandardOutputPath $webStdOutPath -StandardErrorPath $webStdErrPath -EnvironmentVariables $environmentVariables

    Wait-Until -Name 'web startup' -TimeoutSeconds 90 -Condition {
        try {
            $response = Invoke-WebRequest -Uri ($baseUrl + '/Auth/Login') -MaximumRedirection 0 -SkipHttpErrorCheck
            return $response.StatusCode -in 200, 302
        }
        catch {
            return $false
        }
    } | Out-Null

    & node (Join-Path $PSScriptRoot 'UiLiveBrowserSmoke.mjs') register $baseUrl $registrationEmail $registrationPassword $diagRoot
    if ($LASTEXITCODE -ne 0) { throw 'UI live browser smoke registration step failed.' }

    $userRow = Wait-Until -Name 'registered smoke user' -TimeoutSeconds 30 -Condition {
        Get-SmokeUser -ConnectionString $connectionString -Email $registrationEmail
    }

    $nowUtc = [DateTime]::SpecifyKind([datetime]'2026-04-07T10:00:00', [DateTimeKind]::Utc)
    Seed-LiveUiGraph -ConnectionString $connectionString -UserId $userRow.Id -StrategyId ([Guid]'11111111-1111-1111-1111-111111111111') -StrategyVersionId ([Guid]'22222222-2222-2222-2222-222222222222') -StrategySignalId ([Guid]'33333333-3333-3333-3333-333333333333') -ExchangeAccountId ([Guid]'44444444-4444-4444-4444-444444444444') -BotId ([Guid]'55555555-5555-5555-5555-555555555555') -FeatureSnapshotId ([Guid]'66666666-6666-6666-6666-666666666666') -DecisionId ([Guid]'77777777-7777-7777-7777-777777777777') -OutcomeId ([Guid]'88888888-8888-8888-8888-888888888888') -FilledOrderId ([Guid]'99999999-9999-9999-9999-999999999999') -RejectOrderId ([Guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa') -DecisionTraceId ([Guid]'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb') -ExecutionTraceId ([Guid]'cccccccc-cccc-cccc-cccc-cccccccccccc') -AuditLogId ([Guid]'dddddddd-dddd-dddd-dddd-dddddddddddd') -RiskProfileId ([Guid]'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee') -ExchangeBalanceId ([Guid]'12121212-1212-1212-1212-121212121212') -ExchangePositionId ([Guid]'13131313-1313-1313-1313-131313131313') -SyncStateId ([Guid]'14141414-1414-1414-1414-141414141414') -DemoPositionId ([Guid]'15151515-1515-1515-1515-151515151515') -DemoLedgerId ([Guid]'16161616-1616-1616-1616-161616161616') -FilledTransitionId ([Guid]'17171717-1717-1717-1717-171717171717') -RejectTransitionId ([Guid]'18181818-1818-1818-1818-181818181818') -NowUtc $nowUtc

    & node (Join-Path $PSScriptRoot 'UiLiveBrowserSmoke.mjs') inspect $baseUrl $registrationEmail $registrationPassword $diagRoot
    if ($LASTEXITCODE -ne 0) { throw 'UI live browser smoke inspection step failed.' }

    if (Test-Path $browserSummaryPath) {
        $summary.Ui = Get-Content -Path $browserSummaryPath -Raw | ConvertFrom-Json
    }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8

    Write-Host ('SmokeDatabaseName=' + $databaseName)
    Write-Host ('RegisteredUserId=' + $userRow.Id)
    if ($summary.Ui) {
        Write-Host ('HomeTradeMaster=' + $summary.Ui.home.tradeMaster)
        Write-Host ('HomeLatestReject=' + $summary.Ui.home.latestRejectSummary)
        Write-Host ('HomeEquityEstimate=' + $summary.Ui.home.equityEstimate)
        Write-Host ('HomeOpenPositionCount=' + $summary.Ui.home.openPositionCount)
        Write-Host ('HomeRecentOrderCount=' + $summary.Ui.home.recentOrders.Count)
        Write-Host ('AiRobotTradeMaster=' + $summary.Ui.aiRobot.tradeMaster)
        Write-Host ('AiRobotLatestReject=' + $summary.Ui.aiRobot.latestReject)
        Write-Host ('AiRobotDecisionCount=' + $summary.Ui.aiRobot.decisionCount)
    }
    Write-Host ('SummaryPath=' + $summaryPath)
}
finally {
    Stop-ManagedProcess -Handle $webHandle
    try {
        Remove-SqlDatabase -ConnectionString $connectionString
    }
    catch {
        Write-Warning ('Failed to remove smoke database ' + $databaseName + ': ' + $_.Exception.Message)
    }
}






