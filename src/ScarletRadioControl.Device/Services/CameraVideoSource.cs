using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarletRadioControl.Device.Options;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace ScarletRadioControl.Device.Services;

public class CameraVideoSource(
	IOptions<DeviceOptions> deviceOptions,
	ILogger<CameraVideoSource> logger
) : IAsyncDisposable
{

	private readonly IOptions<DeviceOptions> deviceOptions = deviceOptions;
	private readonly ILogger<CameraVideoSource> logger = logger;

	private readonly Lock lockObject = new Lock();

	private int consumerCount;

	private FFmpegCameraSource? fFmpegCameraSource;

	private bool ffmpegInitialised;

	public void EnsureInitialised()
	{
		lock (this.lockObject)
		{
			if (this.fFmpegCameraSource != null)
			{
				return;
			}

			var deviceOptionsValue = this.deviceOptions.Value;
			var cameraOptions = deviceOptionsValue.Camera;
			var ffmpegOptions = deviceOptionsValue.Ffmpeg;

			if (!this.ffmpegInitialised)
			{
				FFmpegInit.Initialise(ffmpegOptions.LogLevel, ffmpegOptions.GetLibraryPath(), this.logger);
				this.ffmpegInitialised = true;
			}

			var cameraPath = cameraOptions.GetPath();
			this.logger.LogInformation("Initialising camera {CameraPath} at {Width}x{Height}@{Framerate}", cameraPath, cameraOptions.Width, cameraOptions.Height, cameraOptions.Framerate);

			var cameraSource = new FFmpegCameraSource(cameraPath);
			if (!cameraSource.RestrictCameraFormats(cameraFormat => cameraFormat.Width == cameraOptions.Width && cameraFormat.Height == cameraOptions.Height && cameraFormat.FPS >= cameraOptions.Framerate))
			{
				var availableCameraFormats = FFmpegCameraManager.GetCameraByPath(cameraPath)?.AvailableFormats;
				this.logger.LogWarning(
					"Camera {CameraPath} does not offer {Width}x{Height}@{Framerate}, using the source default. Available formats: {AvailableCameraFormats}",
					cameraPath,
					cameraOptions.Width,
					cameraOptions.Height,
					cameraOptions.Framerate,
					availableCameraFormats == null ? "unknown" : string.Join(", ", availableCameraFormats.Select(x => $"{x.Width}x{x.Height}@{x.FPS} {x.PixelFormat}")));
			}
			cameraSource.RestrictFormats(videoFormat => videoFormat.Codec == VideoCodecsEnum.H264);
			cameraSource.OnVideoSourceError += this.OnVideoSourceError;

			this.fFmpegCameraSource = cameraSource;
			this.consumerCount = 0;
		}
	}

	public List<VideoFormat> GetVideoSourceFormats()
	{
		lock (this.lockObject)
		{
			var cameraSource = this.fFmpegCameraSource ?? throw new InvalidOperationException("The camera video source is not initialised.");
			return cameraSource.GetVideoSourceFormats();
		}
	}

	public void SetVideoSourceFormat(VideoFormat videoFormat)
	{
		lock (this.lockObject)
		{
			this.fFmpegCameraSource?.SetVideoSourceFormat(videoFormat);
		}
	}

	public void ForceKeyFrame()
	{
		lock (this.lockObject)
		{
			this.fFmpegCameraSource?.ForceKeyFrame();
		}
	}

	public async Task AddConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		// The source is torn down whenever the last consumer leaves, so it may need re-creating.
		this.EnsureInitialised();

		Task? startTask = null;
		lock (this.lockObject)
		{
			var cameraSource = this.fFmpegCameraSource ?? throw new InvalidOperationException("The camera video source is not initialised.");
			cameraSource.OnVideoSourceEncodedSample += encodedSampleDelegate;
			this.consumerCount++;
			if (this.consumerCount == 1)
			{
				startTask = cameraSource.StartVideo();
			}
		}

		if (startTask != null)
		{
			await startTask;
		}
	}

	public async Task RemoveConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		FFmpegCameraSource? cameraSourceToClose = null;
		lock (this.lockObject)
		{
			var cameraSource = this.fFmpegCameraSource;
			if (cameraSource == null)
			{
				return;
			}

			cameraSource.OnVideoSourceEncodedSample -= encodedSampleDelegate;
			if (this.consumerCount > 0)
			{
				this.consumerCount--;
				if (this.consumerCount == 0)
				{
					// Close instead of pausing: a paused source stops draining the capture buffer while
					// the device keeps filling it, overflowing it and keeping the camera busy.
					cameraSourceToClose = this.Detach();
				}
			}
		}

		if (cameraSourceToClose != null)
		{
			await cameraSourceToClose.CloseVideo();
			cameraSourceToClose.Dispose();
		}
	}

	public async ValueTask DisposeAsync()
	{
		var cameraSource = this.Detach();
		if (cameraSource == null)
		{
			return;
		}

		await cameraSource.CloseVideo();
		cameraSource.Dispose();
	}

	private void OnVideoSourceError(string errorMessage)
	{
		this.logger.LogError("Camera video source error, tearing down the source: {ErrorMessage}", errorMessage);

		// Dispose on the thread pool: the error is raised from the capture thread and disposing synchronously would deadlock.
		_ = Task.Run(() =>
		{
			var cameraSource = this.Detach();
			if (cameraSource == null)
			{
				return;
			}

			try
			{
				cameraSource.Dispose();
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "Failed to dispose the camera video source");
			}
		});
	}

	private FFmpegCameraSource? Detach()
	{
		lock (this.lockObject)
		{
			var cameraSource = this.fFmpegCameraSource;
			this.fFmpegCameraSource = null;
			this.consumerCount = 0;
			if (cameraSource != null)
			{
				cameraSource.OnVideoSourceError -= this.OnVideoSourceError;
			}
			return cameraSource;
		}
	}

}
