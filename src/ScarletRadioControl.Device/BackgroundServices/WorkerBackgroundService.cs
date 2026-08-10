using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScarletRadioControl.Device.BackgroundServices;

public class WorkerBackgroundService(ILogger<WorkerBackgroundService> logger) : BackgroundService
{

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			if (logger.IsEnabled(LogLevel.Information))
			{
				logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
			}
			await Task.Delay(1000, stoppingToken);
		}
	}

}
