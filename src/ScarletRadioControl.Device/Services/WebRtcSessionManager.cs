using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ScarletRadioControl.Device.Signaling;
using ScarletRadioControl.Device.WebRtc;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScarletRadioControl.Device.Services;

public class WebRtcSessionManager(
	CameraVideoSource cameraVideoSource,
	ILogger<WebRtcSessionManager> logger
)
{

	private readonly CameraVideoSource cameraVideoSource = cameraVideoSource;
	private readonly ILogger<WebRtcSessionManager> logger = logger;

	// SIPSorcery only supports up to 10 ice servers per connection.
	private const int MaximumIceServers = 10;

	private readonly ConcurrentDictionary<string, WebRtcPeerSession> webRtcPeerSessions = new ConcurrentDictionary<string, WebRtcPeerSession>();

	private List<RTCIceServer> rtcIceServers = new List<RTCIceServer>();

	public event Action<string, RtcIceCandidateInit>? OnIceCandidate;

	public void SetIceServers(ICollection<RtcIceServer>? rtcIceServers)
	{
		var mappedRtcIceServers = new List<RTCIceServer>();

		foreach (var rtcIceServer in rtcIceServers ?? new List<RtcIceServer>())
		{
			foreach (var url in rtcIceServer.Urls ?? new List<string>())
			{
				if (mappedRtcIceServers.Count >= MaximumIceServers)
				{
					break;
				}

				if (string.IsNullOrEmpty(url))
				{
					continue;
				}

				if (url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
				{
					this.logger.LogWarning("Skipping ice server url {Url}, TURN over TCP/TLS is not supported", url);
					continue;
				}

				var mappedRtcIceServer = new RTCIceServer { urls = url };
				if (!string.IsNullOrEmpty(rtcIceServer.Username) || !string.IsNullOrEmpty(rtcIceServer.Credential))
				{
					mappedRtcIceServer.username = rtcIceServer.Username;
					mappedRtcIceServer.credential = rtcIceServer.Credential;
					mappedRtcIceServer.credentialType = RTCIceCredentialType.password;
				}
				mappedRtcIceServers.Add(mappedRtcIceServer);
			}
		}

		this.rtcIceServers = mappedRtcIceServers;
		this.logger.LogInformation("Configured {IceServerCount} ice servers", mappedRtcIceServers.Count);
	}

	public async Task<RtcSessionDescriptionInit> CreateOfferAsync(string clientConnectionId)
	{
		this.ClosePeer(clientConnectionId);

		this.cameraVideoSource.EnsureInitialised();

		var rtcPeerConnection = new RTCPeerConnection(new RTCConfiguration { iceServers = this.rtcIceServers });
		var webRtcPeerSession = new WebRtcPeerSession(clientConnectionId, rtcPeerConnection);

		// The identical negotiated data channel set as useRtcPeerConnection.tsx; unused for now.
		var rtcDataChannels = new[]
		{
			await rtcPeerConnection.createDataChannel("control", new RTCDataChannelInit { id = 0, maxRetransmits = 0, negotiated = true, ordered = false }),
			await rtcPeerConnection.createDataChannel("commands", new RTCDataChannelInit { id = 1, negotiated = true }),
			await rtcPeerConnection.createDataChannel("telemetry", new RTCDataChannelInit { id = 2, maxRetransmits = 0, negotiated = true, ordered = false }),
			await rtcPeerConnection.createDataChannel("events", new RTCDataChannelInit { id = 3, negotiated = true }),
		};
		foreach (var rtcDataChannel in rtcDataChannels)
		{
			rtcDataChannel.onopen += () => this.logger.LogDebug("Data channel {Label} opened for client {ClientConnectionId}", rtcDataChannel.label, clientConnectionId);
		}

		rtcPeerConnection.addTrack(new MediaStreamTrack(this.cameraVideoSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly));

		rtcPeerConnection.OnVideoFormatsNegotiated += videoFormats => this.cameraVideoSource.SetVideoSourceFormat(videoFormats.First());

		rtcPeerConnection.onicecandidate += rtcIceCandidate =>
		{
			if (rtcIceCandidate == null)
			{
				return;
			}

			// SIPSorcery's ToString() omits the "candidate:" prefix browsers expect.
			this.OnIceCandidate?.Invoke(clientConnectionId, new RtcIceCandidateInit
			{
				Candidate = "candidate:" + rtcIceCandidate.ToString(),
				SdpMid = rtcIceCandidate.sdpMid ?? "0",
				SdpMLineIndex = rtcIceCandidate.sdpMLineIndex,
			});
		};

		rtcPeerConnection.onconnectionstatechange += rtcPeerConnectionState =>
		{
			this.logger.LogInformation("Peer connection state changed for client {ClientConnectionId}: {RtcPeerConnectionState}", clientConnectionId, rtcPeerConnectionState);
			switch (rtcPeerConnectionState)
			{
				case RTCPeerConnectionState.connected:
					_ = this.AttachVideoAsync(webRtcPeerSession);
					break;
				case RTCPeerConnectionState.failed:
				case RTCPeerConnectionState.closed:
				case RTCPeerConnectionState.disconnected:
					this.ClosePeer(clientConnectionId);
					break;
				default:
					break;
			}
		};

		this.webRtcPeerSessions[clientConnectionId] = webRtcPeerSession;

		var rtcSessionDescriptionInit = rtcPeerConnection.createOffer();
		await rtcPeerConnection.setLocalDescription(rtcSessionDescriptionInit);

		this.logger.LogInformation("Created offer for client {ClientConnectionId}", clientConnectionId);
		return new RtcSessionDescriptionInit { Sdp = rtcSessionDescriptionInit.sdp, Type = "offer" };
	}

	public void ApplyAnswer(string clientConnectionId, RtcSessionDescriptionInit rtcSessionDescriptionInit)
	{
		if (!this.webRtcPeerSessions.TryGetValue(clientConnectionId, out var webRtcPeerSession))
		{
			this.logger.LogWarning("Received an answer for unknown client {ClientConnectionId}", clientConnectionId);
			return;
		}

		SetDescriptionResultEnum setDescriptionResult;
		var pendingRtcIceCandidateInits = new List<RtcIceCandidateInit>();
		lock (webRtcPeerSession.SessionLock)
		{
			setDescriptionResult = webRtcPeerSession.RtcPeerConnection.setRemoteDescription(new RTCSessionDescriptionInit { sdp = rtcSessionDescriptionInit.Sdp, type = RTCSdpType.answer });
			if (setDescriptionResult == SetDescriptionResultEnum.OK)
			{
				webRtcPeerSession.RemoteDescriptionSet = true;
				pendingRtcIceCandidateInits.AddRange(webRtcPeerSession.PendingRtcIceCandidateInits);
				webRtcPeerSession.PendingRtcIceCandidateInits.Clear();
			}
		}

		if (setDescriptionResult != SetDescriptionResultEnum.OK)
		{
			this.logger.LogError("Failed to apply the answer for client {ClientConnectionId}: {SetDescriptionResult}", clientConnectionId, setDescriptionResult);
			this.ClosePeer(clientConnectionId);
			return;
		}

		this.logger.LogInformation("Applied answer for client {ClientConnectionId}", clientConnectionId);

		foreach (var pendingRtcIceCandidateInit in pendingRtcIceCandidateInits)
		{
			this.ApplyIceCandidate(webRtcPeerSession, pendingRtcIceCandidateInit);
		}
	}

	public void AddIceCandidate(string clientConnectionId, RtcIceCandidateInit rtcIceCandidateInit)
	{
		if (string.IsNullOrEmpty(rtcIceCandidateInit.Candidate))
		{
			// End-of-candidates indication.
			return;
		}

		if (!this.webRtcPeerSessions.TryGetValue(clientConnectionId, out var webRtcPeerSession))
		{
			this.logger.LogWarning("Received an ice candidate for unknown client {ClientConnectionId}", clientConnectionId);
			return;
		}

		lock (webRtcPeerSession.SessionLock)
		{
			if (!webRtcPeerSession.RemoteDescriptionSet)
			{
				webRtcPeerSession.PendingRtcIceCandidateInits.Add(rtcIceCandidateInit);
				return;
			}
		}

		this.ApplyIceCandidate(webRtcPeerSession, rtcIceCandidateInit);
	}

	public void ClosePeer(string clientConnectionId)
	{
		if (!this.webRtcPeerSessions.TryRemove(clientConnectionId, out var webRtcPeerSession))
		{
			return;
		}

		EncodedSampleDelegate? encodedSampleDelegate;
		lock (webRtcPeerSession.SessionLock)
		{
			encodedSampleDelegate = webRtcPeerSession.EncodedSampleDelegate;
			webRtcPeerSession.EncodedSampleDelegate = null;
		}

		if (encodedSampleDelegate != null)
		{
			_ = this.DetachVideoAsync(encodedSampleDelegate);
		}

		try
		{
			webRtcPeerSession.RtcPeerConnection.close();
		}
		catch (Exception exception)
		{
			this.logger.LogWarning(exception, "Failed to close the peer connection for client {ClientConnectionId}", clientConnectionId);
		}

		this.logger.LogInformation("Closed peer session for client {ClientConnectionId}", clientConnectionId);
	}

	public void CloseAll()
	{
		foreach (var clientConnectionId in this.webRtcPeerSessions.Keys)
		{
			this.ClosePeer(clientConnectionId);
		}
	}

	private async Task AttachVideoAsync(WebRtcPeerSession webRtcPeerSession)
	{
		try
		{
			EncodedSampleDelegate encodedSampleDelegate;
			lock (webRtcPeerSession.SessionLock)
			{
				if (webRtcPeerSession.EncodedSampleDelegate != null)
				{
					return;
				}

				encodedSampleDelegate = webRtcPeerSession.RtcPeerConnection.SendVideo;
				webRtcPeerSession.EncodedSampleDelegate = encodedSampleDelegate;
			}

			await this.cameraVideoSource.AddConsumerAsync(encodedSampleDelegate);
		}
		catch (Exception exception)
		{
			this.logger.LogError(exception, "Failed to attach video for client {ClientConnectionId}", webRtcPeerSession.ClientConnectionId);
		}
	}

	private async Task DetachVideoAsync(EncodedSampleDelegate encodedSampleDelegate)
	{
		try
		{
			await this.cameraVideoSource.RemoveConsumerAsync(encodedSampleDelegate);
		}
		catch (Exception exception)
		{
			this.logger.LogWarning(exception, "Failed to detach video");
		}
	}

	private void ApplyIceCandidate(WebRtcPeerSession webRtcPeerSession, RtcIceCandidateInit rtcIceCandidateInit)
	{
		try
		{
			// SIPSorcery's parser strips the "candidate:" prefix itself.
			webRtcPeerSession.RtcPeerConnection.addIceCandidate(new RTCIceCandidateInit
			{
				candidate = rtcIceCandidateInit.Candidate,
				sdpMid = rtcIceCandidateInit.SdpMid,
				sdpMLineIndex = (ushort)(rtcIceCandidateInit.SdpMLineIndex ?? 0),
				usernameFragment = rtcIceCandidateInit.UsernameFragment,
			});
		}
		catch (Exception exception)
		{
			this.logger.LogWarning(exception, "Failed to add an ice candidate for client {ClientConnectionId}", webRtcPeerSession.ClientConnectionId);
		}
	}

}
