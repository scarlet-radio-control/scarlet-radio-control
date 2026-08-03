using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ScarletRadioControl.Web.Controllers.API.V1;

[ApiController]
[ApiExplorerSettings(GroupName = "v1")]
[Route("/api/v1/whep")]
public class WhepController : ControllerBase
{

	[Consumes("application/sdp")]
	[HttpPost("endpoint")]
	[ProducesResponseType<string>(StatusCodes.Status201Created, "application/sdp")]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	public async Task<IActionResult> EndpointAsync([FromBody] string sdpOffer, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(sdpOffer)) { return this.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The SDP offer cannot be empty."); }

		var rtcSessionDescription = new RTCSessionDescription
		{
			Sdp = sdpOffer,
			Type = "offer"
		};

		// TODO: Pass sdpOffer to the WebRTC peer, set its remote description,
		// create the SDP answer, and return it with 201 Created and Location.
		return this.StatusCode(StatusCodes.Status501NotImplemented);
	}

	public record RTCSessionDescription
	{
		public required string Sdp { get; init; }
		public required string Type { get; init; } /* "offer" or "answer" */
	}

}
