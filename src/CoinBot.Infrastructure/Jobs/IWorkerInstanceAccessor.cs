namespace CoinBot.Infrastructure.Jobs;

public interface IWorkerInstanceAccessor
{
    string WorkerInstanceId { get; }
}
