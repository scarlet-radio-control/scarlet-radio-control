using SIPSorceryMedia.FFmpeg;

namespace ScarletRadioControl.Device.Options;

public class FfmpegOptions
{

	public string? LibraryPath { get; set; }

	public FfmpegLogLevelEnum LogLevel { get; set; } = FfmpegLogLevelEnum.AV_LOG_WARNING;

}
