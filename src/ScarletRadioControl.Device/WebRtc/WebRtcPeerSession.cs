using System.Collections.Generic;
using System.Threading;
using ScarletRadioControl.Device.Signaling;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace ScarletRadioControl.Device.WebRtc;

public class WebRtcPeerSession(string clientConnectionId, RTCPeerConnection rtcPeerConnection)
{

	public string ClientConnectionId { get; } = clientConnectionId;

	public EncodedSampleDelegate? EncodedSampleDelegate { get; set; }

	public List<RtcIceCandidateInit> PendingRtcIceCandidateInits { get; } = new List<RtcIceCandidateInit>();

	public bool RemoteDescriptionSet { get; set; }

	public RTCPeerConnection RtcPeerConnection { get; } = rtcPeerConnection;

	public Lock SessionLock { get; } = new Lock();

}
