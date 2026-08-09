import { useEffect, useState } from "react";

export default function useRtcPeerConnection(): RTCPeerConnection {
	const [rtcPeerConnection] = useState(() => new RTCPeerConnection());

	useEffect(() => {
		return () => {
			rtcPeerConnection.close();
		};
	}, [rtcPeerConnection]);

	return rtcPeerConnection;
}
