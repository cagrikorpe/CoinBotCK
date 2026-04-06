using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Ai;

public sealed class AiSignalOptions
{
    public bool Enabled { get; set; } = false;

    [Required]
    public string SelectedProvider { get; set; } = DeterministicStubAiSignalProviderAdapter.ProviderNameValue;

    [Range(100, 30000)]
    public int TimeoutMs { get; set; } = 1500;

    [Range(typeof(decimal), "0", "1")]
    public decimal MinimumConfidence { get; set; } = 0.70m;

    public bool AllowLong { get; set; } = true;

    public bool AllowShort { get; set; } = false;

    public bool StubMode { get; set; } = true;

    public bool OfflineMode { get; set; } = true;

    public bool FailSafeNeutralEnabled { get; set; } = true;

    [Range(64, 512)]
    public int MaxReasonLength { get; set; } = 160;

    public string? OpenAiModel { get; set; }

    public string? GeminiModel { get; set; }
}
