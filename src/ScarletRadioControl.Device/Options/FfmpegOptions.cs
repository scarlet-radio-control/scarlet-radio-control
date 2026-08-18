using System;
using System.Collections.Generic;

namespace ScarletRadioControl.Device.Options;

public class FfmpegOptions
{

	public int BitrateKbps { get; set; } = 2000;

	public string ExecutablePath { get; set; } = "ffmpeg";

	public ICollection<string> ExtraArgs { get; set; } = new List<string>();

	public int GopSeconds { get; set; } = 1;

	public string LinuxEncoder { get; set; } = "h264_v4l2m2m";

	public int RtpPort { get; set; }

	public string WindowsEncoder { get; set; } = "libx264";

	public string GetEncoder()
	{
		return OperatingSystem.IsWindows() ? this.WindowsEncoder : this.LinuxEncoder;
	}

}
