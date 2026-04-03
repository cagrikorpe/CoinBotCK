using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Risk;

public sealed record RiskVetoResult(
    bool IsVetoed,
    RiskVetoReasonCode ReasonCode,
    PreTradeRiskSnapshot Snapshot,
    string ReasonSummary = "");
