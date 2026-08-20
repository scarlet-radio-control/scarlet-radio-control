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

	private const int DefaultFramerate = 30;

	private const int H264ClockRate = 90000;

	// profile-level-id is not optional in practice: RFC 6184 says a receiver must imply baseline level 1.0
	// when it is absent, and level 1.0 caps out at 99 macroblocks (176x144) and 64 kbps. A browser then
	// configures its decoder for that and wedges on the first 1280x720 frame. 42e01f is constrained baseline
	// level 3.1, whose limits (3600 macroblocks, 108000 macroblocks/s) are exactly 720p30, and it is the
	// profile every browser accepts. level-asymmetry-allowed lets the encoder's own level_idc differ.
	private const string H264FormatParameters = "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1";

	private const int IdrSliceNalUnitType = 5;

	// The payload type ffmpeg stamps on the loopback stream. It reaches the command line through the
	// {PayloadType} placeholder, so the configured template and the receive filter below cannot drift apart.
	private const int LoopbackRtpPayloadType = 96;

	private const int MaximumStandardErrorLines = 50;

	// What the sdp offer advertises, which is unrelated to the loopback one: VideoStream.SendVideo
	// repacketises the access unit under whatever format each peer negotiated.
	private const int OfferedVideoPayloadType = 96;

	private const int PictureParameterSetNalUnitType = 8;

	private const int ReceiveBufferSize = 2 * 1024 * 1024;

	private const int SequenceParameterSetNalUnitType = 7;

	private static readonly byte[] AnnexBStartCode = new byte[] { 0x00, 0x00, 0x00, 0x01 };

	private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

	private readonly Queue<string> ffmpegStandardErrorLines = new Queue<string>();

	private readonly Lock lockObject = new Lock();

	private CameraCapture? cameraCapture;

	private EncodedSampleDelegate? encodedSampleConsumers;

	public void EnsureInitialised()
	{
		// Validation only: the capture itself starts with the first consumer. Throwing here is what stops
		// the session manager from offering a video track it cannot feed.
		var deviceOptionsValue = this.deviceOptions.Value;
		var cameraPath = deviceOptionsValue.Camera.GetPath();
		if (string.IsNullOrEmpty(cameraPath))
		{
			throw new InvalidOperationException("The camera path is not configured for this platform.");
		}

		if (!OperatingSystem.IsWindows() && !File.Exists(cameraPath))
		{
			throw new InvalidOperationException($"The camera device {cameraPath} does not exist.");
		}

		// The arguments carry no in code default, so an empty section here means no command to run at all.
		if (string.IsNullOrWhiteSpace(deviceOptionsValue.Ffmpeg.GetArguments()))
		{
			throw new InvalidOperationException("The ffmpeg arguments are not configured for this platform.");
		}

		// Expanding the template here is what turns a misspelled placeholder into a refused offer rather
		// than an exception on the first viewer. The port is a stand in; the bound one is known later.
		FfmpegArgumentTemplate.Expand(deviceOptionsValue.Camera, deviceOptionsValue.Ffmpeg, 0, LoopbackRtpPayloadType);
	}

	public List<VideoFormat> GetVideoSourceFormats()
	{
		return new List<VideoFormat> { new VideoFormat(VideoCodecsEnum.H264, OfferedVideoPayloadType, H264ClockRate, H264FormatParameters) };
	}

	public void SetVideoSourceFormat(VideoFormat videoFormat)
	{
		// Nothing to switch: the external encoder only ever produces H264, and the negotiated payload id
		// is applied per connection inside VideoStream.SendVideo.
		this.logger.LogDebug("Negotiated video format {VideoCodec} with payload id {VideoFormatId}", videoFormat.Codec, videoFormat.FormatID);
	}

	public Task AddConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		// The capture is torn down whenever the last consumer leaves, so it may need starting again.
		this.EnsureInitialised();

		lock (this.lockObject)
		{
			// Written volatile because the receive loop reads it without taking the lock.
			Volatile.Write(ref this.encodedSampleConsumers, this.encodedSampleConsumers + encodedSampleDelegate);
			if (this.cameraCapture == null)
			{
				this.StartCapture();
			}
		}

		return Task.CompletedTask;
	}

	public async Task RemoveConsumerAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		bool stopCapture;
		lock (this.lockObject)
		{
			Volatile.Write(ref this.encodedSampleConsumers, this.encodedSampleConsumers - encodedSampleDelegate);

			// The multicast delegate is null exactly when the last consumer has gone. Stop instead of
			// idling: a running ffmpeg would hold the camera open and keep filling the socket.
			stopCapture = this.encodedSampleConsumers == null;
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
			Volatile.Write(ref this.encodedSampleConsumers, null);
		}

		await this.StopCaptureAsync();
	}

	private void StartCapture()
	{
		var deviceOptionsValue = this.deviceOptions.Value;
		var cameraOptions = deviceOptionsValue.Camera;

		// Bind before spawning so the ephemeral port is known, and so no packet is missed once ffmpeg starts sending.
		var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, deviceOptionsValue.Ffmpeg.RtpPort));
		CameraCapture cameraCapture;
		try
		{
			udpClient.Client.ReceiveBufferSize = ReceiveBufferSize;
			cameraCapture = new CameraCapture
			{
				ReceiveCancellationTokenSource = new CancellationTokenSource(),
				RtpPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port,
				UdpClient = udpClient,
			};

			this.logger.LogInformation(
				"Starting the capture of camera {CameraPath} at {Width}x{Height}@{Framerate} over rtp port {RtpPort}",
				cameraOptions.GetPath(),
				cameraOptions.Width,
				cameraOptions.Height,
				cameraOptions.Framerate,
				cameraCapture.RtpPort);

			cameraCapture.FfmpegProcess = this.StartFfmpegProcess(cameraCapture);
		}
		catch (Exception)
		{
			udpClient.Dispose();
			throw;
		}

		cameraCapture.ReceiveTask = Task.Run(() => this.ReceiveAsync(cameraCapture, cameraCapture.ReceiveCancellationTokenSource.Token));
		this.cameraCapture = cameraCapture;
	}

	private async Task StopCaptureAsync()
	{
		CameraCapture? cameraCapture;
		lock (this.lockObject)
		{
			// Swapping the reference out is what supersedes a respawn that is already in flight: the
			// supervisor below compares against it by identity.
			cameraCapture = this.cameraCapture;
			this.cameraCapture = null;
		}

		if (cameraCapture == null)
		{
			return;
		}

		var ffmpegProcess = cameraCapture.FfmpegProcess;
		if (ffmpegProcess != null)
		{
			try
			{
				ffmpegProcess.Kill(entireProcessTree: true);
				await ffmpegProcess.WaitForExitAsync();
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "Failed to stop the ffmpeg process");
			}
			finally
			{
				ffmpegProcess.Dispose();
			}
		}

		// Cancel and drain the receive loop before closing the socket it is blocked on.
		await cameraCapture.ReceiveCancellationTokenSource.CancelAsync();
		if (cameraCapture.ReceiveTask != null)
		{
			try
			{
				await cameraCapture.ReceiveTask;
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "The rtp receive loop faulted while the capture was stopping");
			}
		}

		cameraCapture.ReceiveCancellationTokenSource.Dispose();
		cameraCapture.UdpClient.Dispose();

		lock (this.lockObject)
		{
			this.ffmpegStandardErrorLines.Clear();
		}

		this.logger.LogInformation("Stopped the camera capture");
	}

	private Process StartFfmpegProcess(CameraCapture cameraCapture)
	{
		var deviceOptionsValue = this.deviceOptions.Value;

		var processStartInfo = new ProcessStartInfo
		{
			CreateNoWindow = true,
			FileName = deviceOptionsValue.Ffmpeg.ExecutablePath,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false,
		};
		foreach (var argument in FfmpegArgumentTemplate.Expand(deviceOptionsValue.Camera, deviceOptionsValue.Ffmpeg, cameraCapture.RtpPort, LoopbackRtpPayloadType))
		{
			processStartInfo.ArgumentList.Add(argument);
		}

		var ffmpegProcess = new Process { EnableRaisingEvents = true, StartInfo = processStartInfo };
		ffmpegProcess.ErrorDataReceived += this.OnFfmpegErrorDataReceived;
		ffmpegProcess.OutputDataReceived += this.OnFfmpegOutputDataReceived;
		ffmpegProcess.Exited += (_, _) => _ = this.HandleFfmpegExitedAsync(cameraCapture, ffmpegProcess);

		this.logger.LogInformation("Spawning ffmpeg: {FfmpegExecutablePath} {FfmpegArguments}", processStartInfo.FileName, string.Join(' ', processStartInfo.ArgumentList));
		ffmpegProcess.Start();
		ffmpegProcess.BeginErrorReadLine();
		ffmpegProcess.BeginOutputReadLine();
		return ffmpegProcess;
	}

	private async Task HandleFfmpegExitedAsync(CameraCapture cameraCapture, Process exitedFfmpegProcess)
	{
		try
		{
			string ffmpegStandardErrorTail;
			lock (this.lockObject)
			{
				if (!this.IsCurrentFfmpegProcess(cameraCapture, exitedFfmpegProcess))
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
				if (!this.IsCurrentFfmpegProcess(cameraCapture, exitedFfmpegProcess))
				{
					return;
				}

				// The socket stays bound and the receive loop keeps running, so only the process is replaced.
				cameraCapture.FfmpegProcess = this.StartFfmpegProcess(cameraCapture);
			}

			exitedFfmpegProcess.Dispose();
		}
		catch (Exception exception)
		{
			this.logger.LogError(exception, "Failed to restart the ffmpeg process");
		}
	}

	// Identity does the job the generation counter used to: a deliberate stop swaps the capture out, and a
	// respawn swaps the process, so a superseded handler recognises itself without a counter to keep in step.
	private bool IsCurrentFfmpegProcess(CameraCapture cameraCapture, Process ffmpegProcess)
	{
		return ReferenceEquals(this.cameraCapture, cameraCapture) && ReferenceEquals(cameraCapture.FfmpegProcess, ffmpegProcess);
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

	private async Task ReceiveAsync(CameraCapture cameraCapture, CancellationToken cancellationToken)
	{
		// The depacketiser and the stream state are stateful and not thread safe, so they live and die
		// with this loop rather than as fields the stop path would have to reset by hand.
		var h264Depacketiser = new H264Depacketiser();
		var captureStreamState = new CaptureStreamState();

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var udpReceiveResult = await cameraCapture.UdpClient.ReceiveAsync(cancellationToken);
				if (!RTPPacket.TryParse(udpReceiveResult.Buffer, out var rtpPacket, out _))
				{
					continue;
				}

				if (rtpPacket.Header.PayloadType != LoopbackRtpPayloadType)
				{
					continue;
				}

				// Returns the frame's nal units on the marker bit packet, null while it is still arriving.
				// Taking them as nal units rather than as a joined stream keeps the boundaries the
				// depacketiser already knows, instead of rescanning the joined bytes for start codes.
				var nalUnits = h264Depacketiser.ProcessRTPPayloadAsNals(rtpPacket.GetPayloadBytes(), rtpPacket.Header.SequenceNumber, rtpPacket.Header.Timestamp, rtpPacket.Header.MarkerBit, out _);
				if (nalUnits == null || nalUnits.Count == 0)
				{
					continue;
				}

				var accessUnit = this.BuildAccessUnit(nalUnits, captureStreamState);
				var durationRtpUnits = this.ComputeDurationRtpUnits(rtpPacket.Header.Timestamp, captureStreamState);

				// Read without the lock: it is a single reference, and this path must not queue behind a
				// spawn, which holds the lock across ffmpeg's fork and exec.
				var encodedSampleConsumers = Volatile.Read(ref this.encodedSampleConsumers);

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

	// Joins the frame's nal units into one annex-b access unit, caching the parameter sets on the way past
	// and prepending them to any idr that arrived without them.
	private byte[] BuildAccessUnit(List<byte[]> nalUnits, CaptureStreamState captureStreamState)
	{
		var containsIdrSlice = false;
		var containsSequenceParameterSet = false;

		foreach (var nalUnit in nalUnits)
		{
			if (nalUnit.Length == 0)
			{
				continue;
			}

			switch (nalUnit[0] & 0x1F)
			{
				case IdrSliceNalUnitType:
					containsIdrSlice = true;
					break;
				case SequenceParameterSetNalUnitType:
					containsSequenceParameterSet = true;
					captureStreamState.SequenceParameterSet = nalUnit;
					break;
				case PictureParameterSetNalUnitType:
					captureStreamState.PictureParameterSet = nalUnit;
					break;
				default:
					break;
			}
		}

		// Browsers need the parameter sets in front of every idr, and neither h264_v4l2m2m nor the rtp
		// muxer's global header handling guarantees that.
		var sequenceParameterSet = captureStreamState.SequenceParameterSet;
		var pictureParameterSet = captureStreamState.PictureParameterSet;
		var prependParameterSets = containsIdrSlice && !containsSequenceParameterSet;
		if (prependParameterSets && (sequenceParameterSet == null || pictureParameterSet == null))
		{
			if (!captureStreamState.ParameterSetsUnavailableLogged)
			{
				captureStreamState.ParameterSetsUnavailableLogged = true;
				this.logger.LogWarning("The encoder produced a keyframe before any sps/pps, viewers cannot decode it yet");
			}

			prependParameterSets = false;
		}

		var accessUnitLength = 0;
		if (prependParameterSets)
		{
			accessUnitLength += (2 * AnnexBStartCode.Length) + sequenceParameterSet!.Length + pictureParameterSet!.Length;
		}

		foreach (var nalUnit in nalUnits)
		{
			accessUnitLength += AnnexBStartCode.Length + nalUnit.Length;
		}

		var accessUnit = new byte[accessUnitLength];
		var offset = 0;
		if (prependParameterSets)
		{
			AppendNalUnit(accessUnit, ref offset, sequenceParameterSet!);
			AppendNalUnit(accessUnit, ref offset, pictureParameterSet!);
		}

		foreach (var nalUnit in nalUnits)
		{
			AppendNalUnit(accessUnit, ref offset, nalUnit);
		}

		return accessUnit;
	}

	private static void AppendNalUnit(byte[] accessUnit, ref int offset, byte[] nalUnit)
	{
		AnnexBStartCode.CopyTo(accessUnit, offset);
		offset += AnnexBStartCode.Length;
		nalUnit.CopyTo(accessUnit, offset);
		offset += nalUnit.Length;
	}

	private uint ComputeDurationRtpUnits(uint rtpTimestamp, CaptureStreamState captureStreamState)
	{
		var framerate = this.deviceOptions.Value.Camera.Framerate;
		var fallbackDurationRtpUnits = (uint)(H264ClockRate / (framerate > 0 ? framerate : DefaultFramerate));

		var previousRtpTimestamp = captureStreamState.PreviousRtpTimestamp;
		captureStreamState.PreviousRtpTimestamp = rtpTimestamp;
		if (previousRtpTimestamp == null)
		{
			return fallbackDurationRtpUnits;
		}

		// Unchecked so a wrapped 32 bit timestamp still yields the real delta. A restarted ffmpeg picks a
		// fresh timestamp base, which shows up as an absurd delta and falls back to the nominal duration.
		var durationRtpUnits = unchecked(rtpTimestamp - previousRtpTimestamp.Value);
		return durationRtpUnits == 0 || durationRtpUnits > H264ClockRate ? fallbackDurationRtpUnits : durationRtpUnits;
	}

	// One running capture: the bound socket, the loop that drains it, and the ffmpeg process currently
	// feeding it. Grouping everything with the same lifetime makes starting and stopping a single
	// reference swap instead of six fields that have to be kept in step.
	private sealed class CameraCapture
	{

		public Process? FfmpegProcess { get; set; }

		public required CancellationTokenSource ReceiveCancellationTokenSource { get; init; }

		public Task? ReceiveTask { get; set; }

		public required int RtpPort { get; init; }

		public required UdpClient UdpClient { get; init; }

	}

	// Per stream state owned solely by the receive loop, on the same terms as the depacketiser: one thread,
	// one lifetime, so it needs neither the lock nor a reset when the capture stops.
	private sealed class CaptureStreamState
	{

		public bool ParameterSetsUnavailableLogged { get; set; }

		public byte[]? PictureParameterSet { get; set; }

		public uint? PreviousRtpTimestamp { get; set; }

		public byte[]? SequenceParameterSet { get; set; }

	}

}
