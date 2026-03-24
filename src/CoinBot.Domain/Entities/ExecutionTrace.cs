namespace CoinBot.Domain.Entities;

public sealed class ExecutionTrace : BaseEntity
{
    public Guid? ExecutionOrderId { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string ExecutionAttemptId { get; set; } = string.Empty;

    public string CommandId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string? RequestMasked { get; set; }

    public string? ResponseMasked { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ExchangeCode { get; set; }

    public int? LatencyMs { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
