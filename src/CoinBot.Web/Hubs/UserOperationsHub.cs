using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CoinBot.Web.Hubs;

[Authorize]
public sealed class UserOperationsHub : Hub
{
}
