using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ScarletRadioControl.Device;

public class Program
{

	public static async Task Main(string[] args)
	{
		using var host = Host.CreateDefaultBuilder(args)
			.ConfigureServices(Startup.ConfigureServices)
			.Build();
		await host.RunAsync();
	}

}
