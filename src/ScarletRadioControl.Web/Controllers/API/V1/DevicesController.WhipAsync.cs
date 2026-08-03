using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ScarletRadioControl.Web.Controllers.API.V1;

public partial class DevicesController
{

	[Consumes("application/sdp")]
	[HttpPost("{deviceId}/whip")]
	[ProducesResponseType<string>(StatusCodes.Status201Created, "application/sdp")]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	public async Task<IActionResult> WhipAsync([FromRoute] string deviceId, [FromBody] string sdpOffer, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(sdpOffer)) { return this.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The SDP offer cannot be empty."); }

		var rtcSessionDescription = new RTCSessionDescription
		{
			Sdp = sdpOffer,
			Type = "offer"
		};

		// TODO: Pass sdpOffer to the ingest WebRTC peer, set its remote
		// description, create a receive-only SDP answer, and return it with
		// 201 Created and the WHIP session Location.
		return this.StatusCode(StatusCodes.Status501NotImplemented);
	}

}
