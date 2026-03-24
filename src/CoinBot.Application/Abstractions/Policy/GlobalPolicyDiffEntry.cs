namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicyDiffEntry(
    string Path,
    string? Before,
    string? After,
    string ChangeType);
