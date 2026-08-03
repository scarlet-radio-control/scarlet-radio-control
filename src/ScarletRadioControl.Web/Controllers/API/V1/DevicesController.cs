using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ScarletRadioControl.Web.Controllers.API.V1;


[ApiController]
[ApiExplorerSettings(GroupName = "v1")]
[Route("/api/v1/devices")]
public partial class DevicesController : ControllerBase
{

	[HttpGet]
	[ProducesResponseType<ICollection<Device>>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, MediaTypeNames.Application.Json)]
	public async Task<IActionResult> GetRtcPeerConnectionConfigurationAsync()
	{
		await Task.CompletedTask;
		var result = new Device[] {
			new Device { Id = "test", Name = "test" },
			new Device { Id = "5454d100-26bf-4c9b-bec7-0289aad847d4", Name = "Device 1" },
		};
		return this.Ok(result);
	}

	public record Device
	{
		public required string Id { get; init; } /* SHOULD BE A GUID */
		public required string Name { get; init; }
	}

	public record RTCSessionDescription
	{
		public required string Sdp { get; init; }
		public required string Type { get; init; } /* "offer" or "answer" */
	}

}
