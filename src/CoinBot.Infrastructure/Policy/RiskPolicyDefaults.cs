using System.Text.Json;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Policy;

internal static class RiskPolicyDefaults
{
    internal static readonly Guid SingletonId = new("8A8A6C2B-7B4D-4B1C-9136-4AF1D06F2C21");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal static RiskPolicy CreateEntity(DateTime utcNow, RiskPolicySnapshot snapshot)
    {
        var normalizedSnapshot = Normalize(snapshot);
        var policyJson = Serialize(normalizedSnapshot);

        return new RiskPolicy
        {
            Id = SingletonId,
            PolicyKey = normalizedSnapshot.PolicyKey,
            CurrentVersion = 1,
            PolicyJson = policyJson,
            PolicyHash = ComputeHash(policyJson),
            LastUpdatedAtUtc = utcNow,
            LastChangeSummary = "Initial policy"
        };
    }

    internal static RiskPolicyVersion CreateVersionEntity(
        Guid riskPolicyId,
        int version,
        string createdByUserId,
        DateTime utcNow,
        RiskPolicySnapshot snapshot,
        string changeSummary,
        IReadOnlyCollection<GlobalPolicyDiffEntry> diffEntries,
        string? source = null,
        string? correlationId = null,
        int? rolledBackFromVersion = null)
    {
        var normalizedSnapshot = Normalize(snapshot);
        var policyJson = Serialize(normalizedSnapshot);

        return new RiskPolicyVersion
        {
            Id = Guid.NewGuid(),
            RiskPolicyId = riskPolicyId,
            Version = version,
            CreatedAtUtc = utcNow,
            CreatedByUserId = NormalizeRequired(createdByUserId),
            Source = NormalizeOptional(source),
            CorrelationId = NormalizeOptional(correlationId),
            ChangeSummary = NormalizeRequired(changeSummary),
            PolicyJson = policyJson,
            DiffJson = Serialize(diffEntries),
            RolledBackFromVersion = rolledBackFromVersion
        };
    }

    internal static RiskPolicySnapshot Normalize(RiskPolicySnapshot snapshot)
    {
        return new RiskPolicySnapshot(
            string.IsNullOrWhiteSpace(snapshot.PolicyKey) ? "GlobalRiskPolicy" : snapshot.PolicyKey.Trim(),
            NormalizeExecutionGuard(snapshot.ExecutionGuardPolicy),
            NormalizeAutonomy(snapshot.AutonomyPolicy),
            NormalizeRestrictions(snapshot.SymbolRestrictions));
    }

    internal static RiskPolicySnapshot CreateDefaultSnapshot()
    {
        return RiskPolicySnapshot.CreateDefault();
    }

    internal static string Serialize(RiskPolicySnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    internal static string Serialize(IReadOnlyCollection<GlobalPolicyDiffEntry> diffEntries)
    {
        return JsonSerializer.Serialize(diffEntries, SerializerOptions);
    }

    internal static RiskPolicySnapshot DeserializePolicy(string policyJson)
    {
        var snapshot = JsonSerializer.Deserialize<RiskPolicySnapshot>(policyJson, SerializerOptions)
            ?? RiskPolicySnapshot.CreateDefault();

        return Normalize(snapshot);
    }

    internal static IReadOnlyCollection<GlobalPolicyDiffEntry> DeserializeDiff(string? diffJson)
    {
        if (string.IsNullOrWhiteSpace(diffJson))
        {
            return Array.Empty<GlobalPolicyDiffEntry>();
        }

        return JsonSerializer.Deserialize<GlobalPolicyDiffEntry[]>(diffJson, SerializerOptions)
            ?? Array.Empty<GlobalPolicyDiffEntry>();
    }

    internal static string ComputeHash(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    internal static IReadOnlyCollection<GlobalPolicyDiffEntry> BuildDiff(
        RiskPolicySnapshot before,
        RiskPolicySnapshot after)
    {
        var diffEntries = new List<GlobalPolicyDiffEntry>();

        AddIfChanged(diffEntries, "PolicyKey", before.PolicyKey, after.PolicyKey);
        AddIfChanged(diffEntries, "ExecutionGuardPolicy.MaxOrderNotional", before.ExecutionGuardPolicy.MaxOrderNotional, after.ExecutionGuardPolicy.MaxOrderNotional);
        AddIfChanged(diffEntries, "ExecutionGuardPolicy.MaxPositionNotional", before.ExecutionGuardPolicy.MaxPositionNotional, after.ExecutionGuardPolicy.MaxPositionNotional);
        AddIfChanged(diffEntries, "ExecutionGuardPolicy.MaxDailyTrades", before.ExecutionGuardPolicy.MaxDailyTrades, after.ExecutionGuardPolicy.MaxDailyTrades);
        AddIfChanged(diffEntries, "ExecutionGuardPolicy.CloseOnlyBlocksNewPositions", before.ExecutionGuardPolicy.CloseOnlyBlocksNewPositions, after.ExecutionGuardPolicy.CloseOnlyBlocksNewPositions);
        AddIfChanged(diffEntries, "AutonomyPolicy.Mode", before.AutonomyPolicy.Mode, after.AutonomyPolicy.Mode);
        AddIfChanged(diffEntries, "AutonomyPolicy.RequireManualApprovalForLive", before.AutonomyPolicy.RequireManualApprovalForLive, after.AutonomyPolicy.RequireManualApprovalForLive);

        var beforeRestrictions = before.SymbolRestrictions.OrderBy(item => item.Symbol, StringComparer.Ordinal).ToArray();
        var afterRestrictions = after.SymbolRestrictions.OrderBy(item => item.Symbol, StringComparer.Ordinal).ToArray();
        var symbols = beforeRestrictions.Select(item => item.Symbol)
            .Concat(afterRestrictions.Select(item => item.Symbol))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            var beforeRestriction = beforeRestrictions.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.Ordinal));
            var afterRestriction = afterRestrictions.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.Ordinal));

            if (beforeRestriction is null && afterRestriction is not null)
            {
                diffEntries.Add(new GlobalPolicyDiffEntry(
                    $"SymbolRestrictions[{symbol}]",
                    null,
                    $"{afterRestriction.State}:{afterRestriction.Reason ?? "none"}",
                    "Added"));
                continue;
            }

            if (beforeRestriction is not null && afterRestriction is null)
            {
                diffEntries.Add(new GlobalPolicyDiffEntry(
                    $"SymbolRestrictions[{symbol}]",
                    $"{beforeRestriction.State}:{beforeRestriction.Reason ?? "none"}",
                    null,
                    "Removed"));
                continue;
            }

            if (beforeRestriction is null || afterRestriction is null)
            {
                continue;
            }

            AddIfChanged(diffEntries, $"SymbolRestrictions[{symbol}].State", beforeRestriction.State, afterRestriction.State);
            AddIfChanged(diffEntries, $"SymbolRestrictions[{symbol}].Reason", beforeRestriction.Reason, afterRestriction.Reason);
        }

        return diffEntries;
    }

    private static ExecutionGuardPolicy NormalizeExecutionGuard(ExecutionGuardPolicy policy)
    {
        return policy with
        {
            MaxOrderNotional = NormalizeMoney(policy.MaxOrderNotional),
            MaxPositionNotional = NormalizeMoney(policy.MaxPositionNotional),
            MaxDailyTrades = policy.MaxDailyTrades is < 0 ? null : policy.MaxDailyTrades
        };
    }

    private static AutonomyPolicy NormalizeAutonomy(AutonomyPolicy policy)
    {
        return policy with
        {
            RequireManualApprovalForLive = policy.RequireManualApprovalForLive
        };
    }

    private static IReadOnlyCollection<SymbolRestriction> NormalizeRestrictions(IReadOnlyCollection<SymbolRestriction> restrictions)
    {
        return restrictions
            .Select(item => new SymbolRestriction(
                NormalizeSymbol(item.Symbol),
                item.State,
                NormalizeOptional(item.Reason),
                item.UpdatedAtUtc?.ToUniversalTime(),
                NormalizeOptional(item.UpdatedByUserId)))
            .OrderBy(item => item.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The symbol is required.", nameof(symbol));
        }

        return normalized;
    }

    private static decimal? NormalizeMoney(decimal? amount)
    {
        return amount.HasValue && amount.Value > 0m
            ? amount.Value
            : null;
    }

    private static string NormalizeRequired(string? value)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The value is required.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void AddIfChanged<T>(
        IList<GlobalPolicyDiffEntry> diffEntries,
        string path,
        T before,
        T after)
    {
        if (EqualityComparer<T>.Default.Equals(before, after))
        {
            return;
        }

        diffEntries.Add(new GlobalPolicyDiffEntry(
            path,
            before?.ToString(),
            after?.ToString(),
            "Changed"));
    }
}
