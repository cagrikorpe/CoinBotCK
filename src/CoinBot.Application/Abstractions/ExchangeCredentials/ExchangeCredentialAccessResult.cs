using System.Text.Json.Serialization;

namespace CoinBot.Application.Abstractions.ExchangeCredentials;

public sealed record ExchangeCredentialAccessResult(
    [property: JsonIgnore] string ApiKey,
    [property: JsonIgnore] string ApiSecret,
    ExchangeCredentialStateSnapshot State)
{
    public override string ToString()
    {
        return $"{nameof(ExchangeCredentialAccessResult)} {{ ApiKey = ***REDACTED***, ApiSecret = ***REDACTED***, State = {State} }}";
    }
}
