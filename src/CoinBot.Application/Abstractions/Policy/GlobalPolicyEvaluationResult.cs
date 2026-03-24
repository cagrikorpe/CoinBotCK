using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicyEvaluationResult(
    bool IsBlocked,
    string? BlockCode,
    string? Message,
    int PolicyVersion,
    SymbolRestrictionState? MatchedRestrictionState = null,
    AutonomyPolicyMode? EffectiveAutonomyMode = null,
    bool IsAdvisory = false,
    string? AdvisoryCode = null,
    string? AdvisoryMessage = null);
