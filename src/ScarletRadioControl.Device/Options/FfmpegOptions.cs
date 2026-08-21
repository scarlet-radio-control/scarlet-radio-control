using System;

namespace ScarletRadioControl.Device.Options;

public class FfmpegOptions
{

	public int BitrateKilobitsPerSecond { get; set; }

	public string ExecutablePath { get; set; }

	public int KeyframeIntervalSeconds { get; set; }

	public string LinuxArguments { get; set; }

	public int RtpPort { get; set; }

	public string WindowsArguments { get; set; }

	public string GetArguments()
	{
		return OperatingSystem.IsWindows() ? this.WindowsArguments : this.LinuxArguments;
	}

}
