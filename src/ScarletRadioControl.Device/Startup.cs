using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
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
			.AddSingleton<HubConnection>(serviceProvider =>
			{
				return new HubConnectionBuilder()
					.WithUrl(hostBuilderContext.Configuration.GetConnectionString(nameof(HubConnection)))
					.WithAutomaticReconnect()
					.Build();
			});
		serviceCollection
			.AddSingleton<Services.CameraVideoSource>();
		serviceCollection
			.AddSingleton<Services.WebRtcSessionManager>();
		serviceCollection
			.AddSingleton<Services.WebRtcSignalingClient>();
	}

}
