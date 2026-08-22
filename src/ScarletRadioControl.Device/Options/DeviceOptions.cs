namespace ScarletRadioControl.Device.Options;

public class DeviceOptions
{

	public const string SectionName = "Device";

	public CameraOptions Camera { get; set; } = new CameraOptions();

	public string DeviceId { get; set; }

	public FfmpegOptions Ffmpeg { get; set; } = new FfmpegOptions();

}
