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
				commandsRtcDataChannel: rtcPeerConnection.createDataChannel("commands", { id: 1, negotiated: true, }), 
				controlRtcDataChannel: rtcPeerConnection.createDataChannel("control", { id: 0,  maxRetransmits: 0, negotiated: true, ordered: false, }), 
				eventsRtcDataChannel: rtcPeerConnection.createDataChannel("events", { id: 3, negotiated: true, }),
				telemetryRtcDataChannel: rtcPeerConnection.createDataChannel("telemetry", { id: 2, maxRetransmits: 0, negotiated: true, ordered: false, }), 
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
