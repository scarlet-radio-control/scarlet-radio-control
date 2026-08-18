using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScarletRadioControl.Device.Options;

namespace ScarletRadioControl.Device;

public static class Startup
{

	public static void ConfigureServices(HostBuilderContext hostBuilderContext, IServiceCollection serviceCollection)
	{
		serviceCollection
			.Configure<DeviceOptions>(hostBuilderContext.Configuration.GetSection(DeviceOptions.SectionName));
		serviceCollection
			.AddHostedService<BackgroundServices.WebRtcSignalingBackgroundService>();
		serviceCollection
			.AddSingleton<Services.CameraVideoSource>();
		serviceCollection
			.AddSingleton<Services.WebRtcSessionManager>();
	}

}
