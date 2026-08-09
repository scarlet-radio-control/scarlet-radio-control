import { useEffect, useState } from "react";

export interface RtcDataChannels {
	commandsRtcDataChannel: RTCDataChannel;
	controlRtcDataChannel: RTCDataChannel;
	eventsRtcDataChannel: RTCDataChannel;
	telemetryRtcDataChannel: RTCDataChannel;
}

export default function useRtcPeerConnection(): {rtcPeerConnection: RTCPeerConnection, rtcDataChannels: RtcDataChannels} {
	const [rtcPeerConnection] = useState<{rtcPeerConnection: RTCPeerConnection, rtcDataChannels: RtcDataChannels}>(() => {
		const rtcPeerConnection = new RTCPeerConnection();

		return { 
			rtcPeerConnection, 
			rtcDataChannels: { 
				commandsRtcDataChannel: rtcPeerConnection.createDataChannel("commands", { negotiated: true, id: 1 }), 
				controlRtcDataChannel: rtcPeerConnection.createDataChannel("control", { ordered: false, maxRetransmits: 0, negotiated: true, id: 0 }), 
				eventsRtcDataChannel: rtcPeerConnection.createDataChannel("events", { negotiated: true, id: 3 }),
				telemetryRtcDataChannel: rtcPeerConnection.createDataChannel("telemetry", { ordered: false, maxRetransmits: 0, negotiated: true, id: 2 }), 
			} 
		};
	});

	useEffect(() => {
		return () => {
			//rtcPeerConnection.rtcPeerConnection.close();
		};
	}, [rtcPeerConnection]);

	return rtcPeerConnection;
}
