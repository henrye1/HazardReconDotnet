namespace HazardRecon.Web.Runs;

/// <summary>
/// Runs the retention sweep at startup and once a day after that. Kept apart
/// from InputPurger so the decision of what to delete stays testable without a
/// clock or a host.
/// </summary>
public class InputPurgeService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly InputPurger _purger;

    public InputPurgeService(InputPurger purger) => _purger = purger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                InputPurger.PurgeOutcome outcome = await _purger.PurgeAsync(DateTimeOffset.UtcNow, stoppingToken);

                if (outcome.Purged > 0)
                    Console.WriteLine($" i purged inputs for {outcome.Purged} run(s) past retention");

                foreach (string failure in outcome.Failed)
                    Console.WriteLine($" ! could not purge inputs - {failure}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // retention is housekeeping: a failure waits for the next sweep
                // rather than taking the host down
                Console.WriteLine($" ! retention sweep failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
