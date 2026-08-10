using Microsoft.Extensions.DependencyInjection;
using ScarletRadioControl.Device.BackgroundServices;

namespace ScarletRadioControl.Device;

public static class Startup
{

	public static void ConfigureServices(IServiceCollection serviceCollection){
		serviceCollection.AddHostedService<WorkerBackgroundService>();
	}

}
