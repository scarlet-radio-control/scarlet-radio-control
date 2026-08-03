using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ScarletRadioControl.Web.Controllers.API.V1;

[ApiController]
[ApiExplorerSettings(GroupName = "v1")]
[Route("/api/v1/web-rtc")]
public class WebRtcController : ControllerBase
{

	[HttpGet("rtc-configuration")]
	[ProducesResponseType<RTCConfiguration>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, MediaTypeNames.Application.Json)]
	public async Task<IActionResult> GetRtcPeerConnectionConfigurationAsync()
	{
		await Task.CompletedTask;
		var rtcConfiguration = new RTCConfiguration
		{
			IceServers = new List<RTCIceServer>
			{
				new RTCIceServer
				{
					Credential = null,
					Urls = new List<string>
					{
						"stun:stun.l.google.com:19302"
					},
					Username = null
				},
				new RTCIceServer
				{
					Credential = null,
					Urls = new List<string>
					{
						"stun:stun.relay.metered.ca:80"
					},
					Username = null
				},
				new RTCIceServer
				{
					Credential = "xkw2mfGQr0ZAODKl",
					Urls = new List<string>
					{
						"turn:global.relay.metered.ca:80"
					},
					Username = "b6e796d3b6bc333d4bf58b84"
				},
				new RTCIceServer
				{
					Credential = "xkw2mfGQr0ZAODKl",
					Urls = new List<string>
					{
						"turn:global.relay.metered.ca:80?transport=tcp"
					},
					Username = "b6e796d3b6bc333d4bf58b84"
				},
				new RTCIceServer
				{
					Credential = "xkw2mfGQr0ZAODKl",
					Urls = new List<string>
					{
						"turn:global.relay.metered.ca:443"
					},
					Username = "b6e796d3b6bc333d4bf58b84"
				},
				new RTCIceServer
				{
					Credential = "xkw2mfGQr0ZAODKl",
					Urls = new List<string>
					{
						"turns:global.relay.metered.ca:443?transport=tcp"
					},
					Username = "b6e796d3b6bc333d4bf58b84"
				}
			}
		};
		return this.Ok(rtcConfiguration);
	}

	public record RTCConfiguration
	{
		public required ICollection<RTCIceServer>? IceServers { get; init; }
	}

	public record RTCIceServer
	{
		public required string? Credential { get; init; }
		public required ICollection<string>? Urls { get; init; }
		public required string? Username { get; init; }
	}

}
