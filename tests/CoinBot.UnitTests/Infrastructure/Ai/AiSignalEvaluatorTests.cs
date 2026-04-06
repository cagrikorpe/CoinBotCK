using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Ai;

public sealed class AiSignalEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_DeterministicStub_ReturnsSameResult_ForSameInput()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = DeterministicStubAiSignalProviderAdapter.ProviderNameValue,
                MinimumConfidence = 0.70m
            },
            new DeterministicStubAiSignalProviderAdapter());
        var request = CreateRequest(CreateReadyFeatureSnapshot());

        var first = await evaluator.EvaluateAsync(request);
        var second = await evaluator.EvaluateAsync(request);

        Assert.False(first.IsFallback);
        Assert.Equal(AiSignalDirection.Long, first.SignalDirection);
        Assert.Equal(first.SignalDirection, second.SignalDirection);
        Assert.Equal(first.ConfidenceScore, second.ConfidenceScore);
        Assert.Equal(first.ReasonSummary, second.ReasonSummary);
        Assert.Equal(first.FeatureSnapshotId, second.FeatureSnapshotId);
        Assert.Equal(first.ProviderName, second.ProviderName);
        Assert.Equal(first.ProviderModel, second.ProviderModel);
        Assert.Equal(first.IsFallback, second.IsFallback);
        Assert.Equal(first.FallbackReason, second.FallbackReason);
        Assert.Equal(first.EvaluatedAtUtc, second.EvaluatedAtUtc);
        Assert.Equal(request.FeatureSnapshot!.Id, first.FeatureSnapshotId);
        Assert.Equal(DeterministicStubAiSignalProviderAdapter.ProviderNameValue, first.ProviderName);
        Assert.Equal("deterministic-v1", first.ProviderModel);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenProviderTimesOut()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "TimeoutAdapter",
                TimeoutMs = 25
            },
            new SlowAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalDirection.Neutral, result.SignalDirection);
        Assert.Equal(AiSignalFallbackReason.Timeout, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenProviderPayloadIsInvalid()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "InvalidJson"
            },
            new InvalidJsonAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalDirection.Neutral, result.SignalDirection);
        Assert.Equal(AiSignalFallbackReason.InvalidPayload, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenProviderUnavailable()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "Unavailable"
            },
            new ProviderUnavailableAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalFallbackReason.ProviderUnavailable, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenConfigurationIsMissing()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = OpenAiSignalProviderAdapter.ProviderNameValue,
                OpenAiModel = null
            },
            new OpenAiSignalProviderAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalFallbackReason.ConfigurationMissing, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenDirectionIsUnsupported()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "UnsupportedDirection"
            },
            new UnsupportedDirectionAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalFallbackReason.UnsupportedResponse, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenProviderIsUnsupported()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "MissingProvider"
            },
            new DeterministicStubAiSignalProviderAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalDirection.Neutral, result.SignalDirection);
        Assert.Equal(AiSignalFallbackReason.UnsupportedProvider, result.FallbackReason);
    }

    [Fact]
    public async Task EvaluateAsync_ReturnsNeutralFallback_WhenConfidenceValueIsUnsupported()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero));
        var evaluator = CreateEvaluator(
            timeProvider,
            new AiSignalOptions
            {
                Enabled = true,
                SelectedProvider = "UnsupportedConfidence"
            },
            new UnsupportedConfidenceAdapter());

        var result = await evaluator.EvaluateAsync(CreateRequest(CreateReadyFeatureSnapshot()));

        Assert.True(result.IsFallback);
        Assert.Equal(AiSignalDirection.Neutral, result.SignalDirection);
        Assert.Equal(AiSignalFallbackReason.UnsupportedResponse, result.FallbackReason);
    }

    private static IAiSignalEvaluator CreateEvaluator(
        TimeProvider timeProvider,
        AiSignalOptions options,
        params IAiSignalProviderAdapter[] adapters)
    {
        return new AiSignalEvaluator(
            adapters,
            Options.Create(options),
            timeProvider,
            NullLogger<AiSignalEvaluator>.Instance);
    }

    private static AiSignalEvaluationRequest CreateRequest(TradingFeatureSnapshotModel featureSnapshot)
    {
        return new AiSignalEvaluationRequest(
            featureSnapshot,
            featureSnapshot.Symbol,
            featureSnapshot.Timeframe,
            featureSnapshot.TradingContext.TradingMode,
            featureSnapshot.TradingContext.Plane,
            StrategySignalType.Entry,
            featureSnapshot.StrategyKey);
    }

    private static TradingFeatureSnapshotModel CreateReadyFeatureSnapshot()
    {
        return new TradingFeatureSnapshotModel(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "ai-user",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "ai-strategy",
            "BTCUSDT",
            "1m",
            new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 6, 11, 59, 59, DateTimeKind.Utc),
            "AI-1.v1",
            FeatureSnapshotState.Ready,
            DegradedModeReasonCode.None,
            240,
            200,
            64000m,
            new TradingTrendFeatureSnapshot(63800m, 63500m, 62000m, 63750m, 63690m),
            new TradingMomentumFeatureSnapshot(29m, 1.5m, 1.0m, 0.5m, 21m, 25m, 13m, -0.8m),
            new TradingVolatilityFeatureSnapshot(450m, 0.18m, 0.09m, -0.35m, 63200m, 62800m),
            new TradingVolumeFeatureSnapshot(1.40m, 1.35m, 1250m),
            new TradingContextFeatureSnapshot(ExchangeDataPlane.Futures, ExecutionEnvironment.Demo, false, false, null, null, null, null, null),
            "Ready feature snapshot.",
            "RSI oversold; MACD improving; relative volume elevated.",
            "TrendUp",
            "Bullish",
            "Contained",
            null);
    }

    private sealed class SlowAdapter : IAiSignalProviderAdapter
    {
        public string Name => "TimeoutAdapter";

        public async Task<AiSignalProviderAdapterResponse> EvaluateAsync(AiSignalProviderAdapterRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return AiSignalProviderAdapterResponse.Success(Name, "slow-v1", "{}");
        }
    }

    private sealed class InvalidJsonAdapter : IAiSignalProviderAdapter
    {
        public string Name => "InvalidJson";

        public Task<AiSignalProviderAdapterResponse> EvaluateAsync(AiSignalProviderAdapterRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AiSignalProviderAdapterResponse.Success(Name, "invalid-v1", "{"));
        }
    }

    private sealed class ProviderUnavailableAdapter : IAiSignalProviderAdapter
    {
        public string Name => "Unavailable";

        public Task<AiSignalProviderAdapterResponse> EvaluateAsync(AiSignalProviderAdapterRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AiSignalProviderAdapterResponse.Failure(Name, "offline-v1", AiSignalFallbackReason.ProviderUnavailable, "Provider is unavailable."));
        }
    }

    private sealed class UnsupportedDirectionAdapter : IAiSignalProviderAdapter
    {
        public string Name => "UnsupportedDirection";

        public Task<AiSignalProviderAdapterResponse> EvaluateAsync(AiSignalProviderAdapterRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AiSignalProviderAdapterResponse.Success(Name, "unsupported-v1", "{\"direction\":\"Sideways\",\"confidenceScore\":0.91,\"reasonSummary\":\"unsupported\"}"));
        }
    }
    private sealed class UnsupportedConfidenceAdapter : IAiSignalProviderAdapter
    {
        public string Name => "UnsupportedConfidence";

        public Task<AiSignalProviderAdapterResponse> EvaluateAsync(AiSignalProviderAdapterRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AiSignalProviderAdapterResponse.Success(Name, "unsupported-v1", "{\"direction\":\"Long\",\"confidenceScore\":1.5,\"reasonSummary\":\"unsupported\"}"));
        }
    }

}
