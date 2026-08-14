using System;

namespace ScarletRadioControl.Device.Options;

public class CameraOptions
{

	public int Framerate { get; set; }

	public int Height { get; set; }

	public string LinuxPath { get; set; }

	public int Width { get; set; }

	public string WindowsPath { get; set; }

	public string GetPath()
	{
		return OperatingSystem.IsWindows() ? this.WindowsPath : this.LinuxPath;
	}

}
