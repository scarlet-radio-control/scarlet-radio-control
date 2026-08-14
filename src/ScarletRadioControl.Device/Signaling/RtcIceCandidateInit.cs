namespace ScarletRadioControl.Device.Signaling;

public record RtcIceCandidateInit
{
	public string? Candidate { get; init; }
	public string? SdpMid { get; init; }
	public int? SdpMLineIndex { get; init; }
	public string? UsernameFragment { get; init; }
}
