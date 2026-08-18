using System;
using SIPSorceryMedia.FFmpeg;

namespace ScarletRadioControl.Device.Options;

public class FfmpegOptions
{

	public string? LinuxLibraryPath { get; set; }

	public FfmpegLogLevelEnum LogLevel { get; set; } = FfmpegLogLevelEnum.AV_LOG_WARNING;

	public string? WindowsLibraryPath { get; set; }

	public string? GetLibraryPath()
	{
		return OperatingSystem.IsWindows() ? this.WindowsLibraryPath : this.LinuxLibraryPath;
	}

}
