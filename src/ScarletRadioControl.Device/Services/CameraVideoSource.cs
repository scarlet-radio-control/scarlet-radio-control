using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarletRadioControl.Device.Options;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScarletRadioControl.Device.Services;

public class CameraVideoSource(
	IOptions<DeviceOptions> deviceOptions,
	ILogger<CameraVideoSource> logger
) : IAsyncDisposable
{

	private readonly IOptions<DeviceOptions> deviceOptions = deviceOptions;
	private readonly ILogger<CameraVideoSource> logger = logger;

	private const int BaselineProfileIdc = 66;

	private const int DefaultFramerate = 30;

	private const int H264ClockRate = 90000;

	private const int IdrSliceNalUnitType = 5;

	private const int MaximumStandardErrorLines = 50;

	private const int PictureParameterSetNalUnitType = 8;

	private const int ReceiveBufferSize = 2 * 1024 * 1024;

	// The payload type ffmpeg stamps on the loopback stream. It is unrelated to the payload type each
	// peer receives: VideoStream.SendVideo repacketises the access unit under the negotiated format.
	private const int RtpPayloadType = 96;

	private const int SequenceParameterSetNalUnitType = 7;

	private static readonly byte[] AnnexBStartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

	private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

	private readonly Queue<string> ffmpegStandardErrorLines = new Queue<string>();

	private readonly Lock lockObject = new Lock();

	private int captureGeneration;

	private int consumerCount;

	private EncodedSampleDelegate? encodedSampleConsumers;

	private Process? ffmpegProcess;

	private bool parameterSetsUnavailableLogged;

	private byte[]? pictureParameterSet;

	private uint? previousRtpTimestamp;

	private CancellationTokenSource? receiveCancellationTokenSource;

	private Task? receiveTask;

	private int rtpPort;

	private byte[]? sequenceParameterSet;

	private UdpClient? udpClient;

	public void EnsureInitialised()
	{
		// Validation only: the capture itself starts with the first consumer. Throwing here is what stops
		// the session manager from offering a video track it cannot feed.
		var cameraPath = this.deviceOptions.Value.Camera.GetPath();
		if (string.IsNullOrEmpty(cameraPath))
		{
			throw new InvalidOperationException("The camera path is not configured for this platform.");
		}

		if (!OperatingSystem.IsWindows() && !File.Exists(cameraPath))
		{
			throw new InvalidOperationException($"The camera device {cameraPath} does not exist.");
		}
	}

	public List<VideoFormat> GetVideoSourceFormats()
	{
		return new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, RtpPayloadType, H264ClockRate, "packetization-mode=1") };
	}

	public void SetVideoSourceFormat(VideoFormat videoFormat)
	{
		// Nothing to switch: the external encoder only ever produces H264, and the negotiated payload id
		// is applied per connection inside VideoStream.SendVideo.
		this.logger.LogDebug("Negotiated video format {VideoCodec} with payload id {VideoFormatId}", videoFormat.Codec, videoFormat.FormatID);
	}

	public void ForceKeyFrame()
	{
		// An external encoder cannot be asked for a keyframe on demand; the fixed short gop bounds the wait instead.
		this.logger.LogDebug("Ignoring the keyframe request, the external encoder emits one every {GopSeconds}s", this.deviceOptions.Value.Ffmpeg.GopSeconds);
	}

	public Task AddConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		// The capture is torn down whenever the last consumer leaves, so it may need starting again.
		this.EnsureInitialised();

		lock (this.lockObject)
		{
			this.encodedSampleConsumers += encodedSampleDelegate;
			this.consumerCount++;
			if (this.udpClient == null)
			{
				this.StartCapture();
			}
		}

		return Task.CompletedTask;
	}

	public async Task RemoveConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		var stopCapture = false;
		lock (this.lockObject)
		{
			this.encodedSampleConsumers -= encodedSampleDelegate;
			if (this.consumerCount > 0)
			{
				this.consumerCount--;

				// Stop instead of idling: a running ffmpeg would hold the camera open and keep filling the socket.
				stopCapture = this.consumerCount == 0;
			}
		}

		if (stopCapture)
		{
			await this.StopCaptureAsync();
		}
	}

	public async ValueTask DisposeAsync()
	{
		lock (this.lockObject)
		{
			this.encodedSampleConsumers = null;
			this.consumerCount = 0;
		}

		await this.StopCaptureAsync();
	}

	private static List<string> BuildFfmpegArguments(CameraOptions cameraOptions, FfmpegOptions ffmpegOptions, int rtpPort)
	{
		var framerate = cameraOptions.Framerate > 0 ? cameraOptions.Framerate : DefaultFramerate;
		var gopSize = framerate * Math.Max(1, ffmpegOptions.GopSeconds);
		var encoder = ffmpegOptions.GetEncoder();
		var videoSize = $"{cameraOptions.Width}x{cameraOptions.Height}";

		var arguments = new List<string> { "-hide_banner", "-nostats", "-loglevel", "warning", "-fflags", "nobuffer" };

		// The input is pinned to mjpeg on both platforms: uvc bandwidth does not carry raw 720p30.
		if (OperatingSystem.IsWindows())
		{
			arguments.AddRange(new[] { "-f", "dshow", "-rtbufsize", "64M", "-video_size", videoSize, "-framerate", $"{framerate}", "-vcodec", "mjpeg" });
		}
		else
		{
			arguments.AddRange(new[] { "-f", "v4l2", "-input_format", "mjpeg", "-video_size", videoSize, "-framerate", $"{framerate}" });
		}

		arguments.AddRange(new[] { "-i", cameraOptions.GetPath() });

		// yuv420p is load-bearing: mjpeg decodes to yuvj422p, and browsers reject the 4:2:2 High profile that would follow.
		arguments.AddRange(new[] { "-an", "-c:v", encoder, "-pix_fmt", "yuv420p" });

		if (string.Equals(encoder, "libx264", StringComparison.OrdinalIgnoreCase))
		{
			// zerolatency drops b-frames, lookahead and frame threading; the half second vbv keeps an idr
			// spike from stalling the webrtc leg.
			arguments.AddRange(new[] { "-profile:v", "baseline", "-preset", "ultrafast", "-tune", "zerolatency", "-sc_threshold", "0", "-x264-params", "repeat-headers=1", "-maxrate", $"{ffmpegOptions.BitrateKbps}k", "-bufsize", $"{ffmpegOptions.BitrateKbps / 2}k" });
		}
		else
		{
			// Only libx264 names its profiles. h264_v4l2m2m and friends take the numeric profile_idc off the
			// generic encoder option, and fail to start on "baseline".
			arguments.AddRange(new[] { "-profile:v", $"{BaselineProfileIdc}" });
		}

		arguments.AddRange(new[] { "-b:v", $"{ffmpegOptions.BitrateKbps}k", "-g", $"{gopSize}", "-keyint_min", $"{gopSize}" });
		arguments.AddRange(ffmpegOptions.ExtraArgs);

		// pkt_size keeps every datagram below the mtu so nothing is ip fragmented.
		arguments.AddRange(new[] { "-f", "rtp", "-payload_type", $"{RtpPayloadType}", $"rtp://127.0.0.1:{rtpPort}?pkt_size=1200" });

		return arguments;
	}

	private static IEnumerable<(int Offset, int Length)> EnumerateNalUnits(byte[] accessUnit)
	{
		var nalUnitOffset = -1;
		var index = 0;
		while (index + 2 < accessUnit.Length)
		{
			if (accessUnit[index] != 0x00 || accessUnit[index + 1] != 0x00 || accessUnit[index + 2] != 0x01)
			{
				index++;
				continue;
			}

			if (nalUnitOffset >= 0)
			{
				var nalUnitLength = MeasureNalUnit(accessUnit, nalUnitOffset, index);
				if (nalUnitLength > 0)
				{
					yield return (nalUnitOffset, nalUnitLength);
				}
			}

			index += 3;
			nalUnitOffset = index;
		}

		if (nalUnitOffset >= 0)
		{
			var trailingNalUnitLength = MeasureNalUnit(accessUnit, nalUnitOffset, accessUnit.Length);
			if (trailingNalUnitLength > 0)
			{
				yield return (nalUnitOffset, trailingNalUnitLength);
			}
		}
	}

	private static int MeasureNalUnit(byte[] accessUnit, int nalUnitOffset, int nalUnitEnd)
	{
		// The leading zero of a four byte start code, and any cabac padding, belong to the delimiter rather than the nal unit.
		while (nalUnitEnd > nalUnitOffset && accessUnit[nalUnitEnd - 1] == 0x00)
		{
			nalUnitEnd--;
		}

		return nalUnitEnd - nalUnitOffset;
	}

	private void StartCapture()
	{
		var deviceOptionsValue = this.deviceOptions.Value;
		var cameraOptions = deviceOptionsValue.Camera;

		// Bind before spawning so the ephemeral port is known, and so no packet is missed once ffmpeg starts sending.
		var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, deviceOptionsValue.Ffmpeg.RtpPort));
		try
		{
			udpClient.Client.ReceiveBufferSize = ReceiveBufferSize;
			this.rtpPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;

			this.logger.LogInformation(
				"Starting the capture of camera {CameraPath} at {Width}x{Height}@{Framerate} over rtp port {RtpPort}",
				cameraOptions.GetPath(),
				cameraOptions.Width,
				cameraOptions.Height,
				cameraOptions.Framerate,
				this.rtpPort);

			this.ffmpegProcess = this.StartFfmpegProcess(++this.captureGeneration);
		}
		catch (Exception)
		{
			udpClient.Dispose();
			throw;
		}

		var receiveCancellationTokenSource = new CancellationTokenSource();
		this.udpClient = udpClient;
		this.receiveCancellationTokenSource = receiveCancellationTokenSource;
		this.receiveTask = Task.Run(() => this.ReceiveAsync(udpClient, receiveCancellationTokenSource.Token));
	}

	private async Task StopCaptureAsync()
	{
		Process? ffmpegProcessToStop;
		CancellationTokenSource? receiveCancellationTokenSourceToStop;
		Task? receiveTaskToStop;
		UdpClient? udpClientToStop;

		lock (this.lockObject)
		{
			// Supersede any respawn that is already in flight before tearing the capture down.
			this.captureGeneration++;
			ffmpegProcessToStop = this.ffmpegProcess;
			receiveCancellationTokenSourceToStop = this.receiveCancellationTokenSource;
			receiveTaskToStop = this.receiveTask;
			udpClientToStop = this.udpClient;
			this.ffmpegProcess = null;
			this.receiveCancellationTokenSource = null;
			this.receiveTask = null;
			this.udpClient = null;
		}

		if (udpClientToStop == null)
		{
			return;
		}

		if (ffmpegProcessToStop != null)
		{
			try
			{
				ffmpegProcessToStop.Kill(entireProcessTree: true);
				await ffmpegProcessToStop.WaitForExitAsync();
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "Failed to stop the ffmpeg process");
			}
			finally
			{
				ffmpegProcessToStop.Dispose();
			}
		}

		// Cancel and drain the receive loop before closing the socket it is blocked on.
		if (receiveCancellationTokenSourceToStop != null)
		{
			await receiveCancellationTokenSourceToStop.CancelAsync();
		}

		if (receiveTaskToStop != null)
		{
			try
			{
				await receiveTaskToStop;
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "The rtp receive loop faulted while the capture was stopping");
			}
		}

		receiveCancellationTokenSourceToStop?.Dispose();
		udpClientToStop.Dispose();

		lock (this.lockObject)
		{
			this.ffmpegStandardErrorLines.Clear();
			this.parameterSetsUnavailableLogged = false;
			this.pictureParameterSet = null;
			this.previousRtpTimestamp = null;
			this.sequenceParameterSet = null;
		}

		this.logger.LogInformation("Stopped the camera capture");
	}

	private Process StartFfmpegProcess(int captureGeneration)
	{
		var deviceOptionsValue = this.deviceOptions.Value;
		var ffmpegOptions = deviceOptionsValue.Ffmpeg;

		var processStartInfo = new ProcessStartInfo
		{
			CreateNoWindow = true,
			FileName = ffmpegOptions.ExecutablePath,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		foreach (var argument in BuildFfmpegArguments(deviceOptionsValue.Camera, ffmpegOptions, this.rtpPort))
		{
			processStartInfo.ArgumentList.Add(argument);
		}

		var ffmpegProcess = new Process { EnableRaisingEvents = true, StartInfo = processStartInfo };
		ffmpegProcess.ErrorDataReceived += this.OnFfmpegErrorDataReceived;
		ffmpegProcess.OutputDataReceived += this.OnFfmpegOutputDataReceived;
		ffmpegProcess.Exited += (_, _) => _ = this.HandleFfmpegExitedAsync(captureGeneration, ffmpegProcess);

		this.logger.LogInformation("Spawning ffmpeg: {FfmpegExecutablePath} {FfmpegArguments}", processStartInfo.FileName, string.Join(' ', processStartInfo.ArgumentList));
		ffmpegProcess.Start();
		ffmpegProcess.BeginErrorReadLine();
		ffmpegProcess.BeginOutputReadLine();
		return ffmpegProcess;
	}

	private async Task HandleFfmpegExitedAsync(int captureGeneration, Process exitedFfmpegProcess)
	{
		try
		{
			string ffmpegStandardErrorTail;
			lock (this.lockObject)
			{
				if (this.captureGeneration != captureGeneration || this.consumerCount == 0)
				{
					return;
				}

				ffmpegStandardErrorTail = string.Join(Environment.NewLine, this.ffmpegStandardErrorLines);
			}

			this.logger.LogError(
				"ffmpeg exited with code {FfmpegExitCode} while consumers were still attached, restarting it in {RestartDelay}. Last ffmpeg output:{NewLine}{FfmpegStandardError}",
				exitedFfmpegProcess.ExitCode,
				RestartDelay,
				Environment.NewLine,
				ffmpegStandardErrorTail);

			await Task.Delay(RestartDelay);

			lock (this.lockObject)
			{
				if (this.captureGeneration != captureGeneration || this.consumerCount == 0 || this.udpClient == null)
				{
					return;
				}

				// The socket stays bound and the receive loop keeps running, so only the process is replaced.
				this.ffmpegProcess = this.StartFfmpegProcess(++this.captureGeneration);
			}

			exitedFfmpegProcess.Dispose();
		}
		catch (Exception exception)
		{
			this.logger.LogError(exception, "Failed to restart the ffmpeg process");
		}
	}

	private void OnFfmpegErrorDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
	{
		var ffmpegStandardErrorLine = dataReceivedEventArgs.Data;
		if (string.IsNullOrWhiteSpace(ffmpegStandardErrorLine))
		{
			return;
		}

		lock (this.lockObject)
		{
			this.ffmpegStandardErrorLines.Enqueue(ffmpegStandardErrorLine);
			while (this.ffmpegStandardErrorLines.Count > MaximumStandardErrorLines)
			{
				this.ffmpegStandardErrorLines.Dequeue();
			}
		}

		// ffmpeg runs at -loglevel warning, so anything reaching here is worth seeing.
		this.logger.LogWarning("ffmpeg: {FfmpegStandardErrorLine}", ffmpegStandardErrorLine);
	}

	private void OnFfmpegOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
	{
		if (string.IsNullOrWhiteSpace(dataReceivedEventArgs.Data))
		{
			return;
		}

		this.logger.LogDebug("ffmpeg: {FfmpegStandardOutputLine}", dataReceivedEventArgs.Data);
	}

	private async Task ReceiveAsync(UdpClient udpClient, CancellationToken cancellationToken)
	{
		// The depacketiser is stateful and not thread safe, so it lives and dies with this loop.
		var h264Depacketiser = new H264Depacketiser();

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var udpReceiveResult = await udpClient.ReceiveAsync(cancellationToken);
				if (!RTPPacket.TryParse(udpReceiveResult.Buffer, out var rtpPacket, out _))
				{
					continue;
				}

				if (rtpPacket.Header.PayloadType != RtpPayloadType)
				{
					continue;
				}

				// Returns an annex-b access unit on the marker bit packet, null while the frame is still arriving.
				var annexBStream = h264Depacketiser.ProcessRTPPayload(rtpPacket.GetPayloadBytes(), rtpPacket.Header.SequenceNumber, rtpPacket.Header.Timestamp, rtpPacket.Header.MarkerBit, out _);
				if (annexBStream == null)
				{
					continue;
				}

				var accessUnit = this.EnsureParameterSets(annexBStream.ToArray());
				var durationRtpUnits = this.ComputeDurationRtpUnits(rtpPacket.Header.Timestamp);

				EncodedSampleDelegate? encodedSampleConsumers;
				lock (this.lockObject)
				{
					encodedSampleConsumers = this.encodedSampleConsumers;
				}

				try
				{
					encodedSampleConsumers?.Invoke(durationRtpUnits, accessUnit);
				}
				catch (Exception exception)
				{
					this.logger.LogWarning(exception, "Failed to deliver an encoded video sample");
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Expected while the capture is stopping.
		}
		catch (ObjectDisposedException)
		{
			// Expected while the capture is stopping.
		}
		catch (Exception exception)
		{
			this.logger.LogError(exception, "The rtp receive loop stopped unexpectedly");
		}
	}

	private byte[] EnsureParameterSets(byte[] accessUnit)
	{
		var containsIdrSlice = false;
		var containsSequenceParameterSet = false;

		foreach (var (nalUnitOffset, nalUnitLength) in EnumerateNalUnits(accessUnit))
		{
			switch (accessUnit[nalUnitOffset] & 0x1F)
			{
				case IdrSliceNalUnitType:
					containsIdrSlice = true;
					break;
				case SequenceParameterSetNalUnitType:
					containsSequenceParameterSet = true;
					this.sequenceParameterSet = accessUnit.AsSpan(nalUnitOffset, nalUnitLength).ToArray();
					break;
				case PictureParameterSetNalUnitType:
					this.pictureParameterSet = accessUnit.AsSpan(nalUnitOffset, nalUnitLength).ToArray();
					break;
				default:
					break;
			}
		}

		if (!containsIdrSlice || containsSequenceParameterSet)
		{
			return accessUnit;
		}

		var sequenceParameterSet = this.sequenceParameterSet;
		var pictureParameterSet = this.pictureParameterSet;
		if (sequenceParameterSet == null || pictureParameterSet == null)
		{
			if (!this.parameterSetsUnavailableLogged)
			{
				this.parameterSetsUnavailableLogged = true;
				this.logger.LogWarning("The encoder produced a keyframe before any sps/pps, viewers cannot decode it yet");
			}

			return accessUnit;
		}

		// Browsers need the parameter sets in front of every idr, and neither h264_v4l2m2m nor the rtp
		// muxer's global header handling guarantees that.
		var parameterisedAccessUnit = new byte[(2 * AnnexBStartCode.Length) + sequenceParameterSet.Length + pictureParameterSet.Length + accessUnit.Length];
		var offset = 0;
		foreach (var parameterSet in new[] { sequenceParameterSet, pictureParameterSet })
		{
			AnnexBStartCode.CopyTo(parameterisedAccessUnit, offset);
			offset += AnnexBStartCode.Length;
			parameterSet.CopyTo(parameterisedAccessUnit, offset);
			offset += parameterSet.Length;
		}
		accessUnit.CopyTo(parameterisedAccessUnit, offset);

		return parameterisedAccessUnit;
	}

	private uint ComputeDurationRtpUnits(uint rtpTimestamp)
	{
		var framerate = this.deviceOptions.Value.Camera.Framerate;
		var fallbackDurationRtpUnits = (uint)(H264ClockRate / (framerate > 0 ? framerate : DefaultFramerate));

		var previousRtpTimestamp = this.previousRtpTimestamp;
		this.previousRtpTimestamp = rtpTimestamp;
		if (previousRtpTimestamp == null)
		{
			return fallbackDurationRtpUnits;
		}

		// Unchecked so a wrapped 32 bit timestamp still yields the real delta. A restarted ffmpeg picks a
		// fresh timestamp base, which shows up as an absurd delta and falls back to the nominal duration.
		var durationRtpUnits = unchecked(rtpTimestamp - previousRtpTimestamp.Value);
		return durationRtpUnits == 0 || durationRtpUnits > H264ClockRate ? fallbackDurationRtpUnits : durationRtpUnits;
	}

}
