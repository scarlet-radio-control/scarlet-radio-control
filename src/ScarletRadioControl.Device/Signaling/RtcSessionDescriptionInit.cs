namespace ScarletRadioControl.Device.Signaling;

public record RtcSessionDescriptionInit
{
	public required string Sdp { get; init; }
	public required string Type { get; init; }
}
