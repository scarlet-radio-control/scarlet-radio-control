using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScarletRadioControl.Device.BackgroundServices;
using ScarletRadioControl.Device.Options;
using ScarletRadioControl.Device.Video;
using ScarletRadioControl.Device.WebRtc;

namespace ScarletRadioControl.Device;

public static class Startup
{

	public static void ConfigureServices(HostBuilderContext hostBuilderContext, IServiceCollection serviceCollection)
	{
		serviceCollection.Configure<DeviceOptions>(hostBuilderContext.Configuration.GetSection(DeviceOptions.SectionName));
		serviceCollection.AddSingleton<CameraVideoSource>();
		serviceCollection.AddSingleton<WebRtcSessionManager>();
		serviceCollection.AddHostedService<WebRtcSignalingBackgroundService>();
	}

}
