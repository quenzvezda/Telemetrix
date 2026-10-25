using Microsoft.Extensions.Options;
using Telemetrix.Simulation;

namespace Telemetrix.PlantSim.Faults;

public sealed class RandomFaultScheduler(IOptions<RandomFaultOptions> options, PumpingLine line,
    TimeProvider clock, ILogger<RandomFaultScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var schedule = new RandomFaultSchedule(options.Value, line, clock);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var fault in schedule.Tick())
                logger.LogInformation("PlantSim random fault: {Fault}", fault);
    }
}
