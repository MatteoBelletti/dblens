using Microsoft.Extensions.Hosting;

public sealed class OutboxPublisher(IConfiguration configuration, IMessageBroker broker, IOutbox outbox) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(configuration.GetValue<int>("Outbox:IntervalSeconds"));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var message in await outbox.PendingAsync(stoppingToken))
            {
                await broker.PublishAsync(message, stoppingToken);
                await outbox.MarkSentAsync(message, stoppingToken);
            }

            await Task.Delay(every, stoppingToken);
        }
    }
}

public interface IMessageBroker
{
    Task PublishAsync(string message, CancellationToken cancellationToken);
}

public interface IOutbox
{
    Task<IReadOnlyList<string>> PendingAsync(CancellationToken cancellationToken);

    Task MarkSentAsync(string message, CancellationToken cancellationToken);
}
