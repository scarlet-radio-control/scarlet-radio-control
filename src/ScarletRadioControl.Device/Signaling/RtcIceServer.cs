using System.Collections.Generic;

namespace ScarletRadioControl.Device.Signaling;

public record RtcIceServer
{
	public required string? Credential { get; init; }
	public required ICollection<string>? Urls { get; init; }
	public required string? Username { get; init; }
}
