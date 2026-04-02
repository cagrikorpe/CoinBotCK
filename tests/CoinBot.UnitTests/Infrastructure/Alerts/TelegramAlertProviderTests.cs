using System.Net;
using System.Text;
using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Infrastructure.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Alerts;

public sealed class TelegramAlertProviderTests
{
    [Fact]
    public async Task SendAsync_PostsFormattedTelegramPayload_WithoutSecretsInMessageBody()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = new TelegramAlertProvider(
            new HttpClient(handler),
            Options.Create(new AlertingOptions
            {
                ApplicationName = "CoinBot Worker"
            }),
            Options.Create(new TelegramAlertOptions
            {
                Enabled = true,
                BotToken = "telegram-bot-token",
                ChatId = "123456"
            }),
            NullLogger<TelegramAlertProvider>.Instance);

        await provider.SendAsync(
            new AlertNotification(
                "ORDER_FILLED",
                AlertSeverity.Information,
                "OrderFilled",
                "EventType=OrderFilled; Symbol=BTCUSDT; Result=Filled; FailureCode=none; ClientOrderId=cbp0_test123; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.telegram.org/bottelegram-bot-token/sendMessage", request.RequestUri!.ToString());

        var body = await request.Content!.ReadAsStringAsync();
        var payload = ParseForm(body);
        Assert.Equal("123456", payload["chat_id"]);
        Assert.Contains("CoinBot Worker [Information] OrderFilled", payload["text"], StringComparison.Ordinal);
        Assert.Contains("Code: ORDER_FILLED", payload["text"], StringComparison.Ordinal);
        Assert.Contains("EventType=OrderFilled", payload["text"], StringComparison.Ordinal);
        Assert.Contains("ClientOrderId=cbp0_test123", payload["text"], StringComparison.Ordinal);
        Assert.DoesNotContain("telegram-bot-token", payload["text"], StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", payload["text"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAsync_DoesNothing_WhenProviderIsDisabled()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = new TelegramAlertProvider(
            new HttpClient(handler),
            Options.Create(new AlertingOptions()),
            Options.Create(new TelegramAlertOptions
            {
                Enabled = false,
                BotToken = string.Empty,
                ChatId = string.Empty
            }),
            NullLogger<TelegramAlertProvider>.Instance);

        await provider.SendAsync(
            new AlertNotification(
                "ORDER_FAILED",
                AlertSeverity.Critical,
                "OrderFailed",
                "EventType=OrderFailed; Symbol=BTCUSDT; Result=Failed; FailureCode=DispatchFailed; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet"));

        Assert.Empty(handler.Requests);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var segment in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            var key = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            var value = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key.Replace("+", " ", StringComparison.Ordinal))] =
                Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }

        return result;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clonedRequest = new HttpRequestMessage(request.Method, request.RequestUri);

            if (request.Content is not null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                clonedRequest.Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/x-www-form-urlencoded");
            }

            Requests.Add(clonedRequest);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
