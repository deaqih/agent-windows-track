namespace Boneiset.Server;

public sealed class RetentionWorker(Store store, IConfiguration configuration, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await store.CleanupAsync(configuration.GetValue("LocationRetentionDays", 30)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogError(exception, "Location retention cleanup failed; it will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
