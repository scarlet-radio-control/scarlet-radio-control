import { useEffect, useRef, useState } from "react";
import { useParams } from "react-router-dom";
import countdown from "../../assets/countdown.mp4";
import { useSignalRContext } from "../../contexts/SignalRContext";
import useEffectAsync from "../../hooks/useEffectAsync";
import useRtcPeerConnection from "../../hooks/useRtcPeerConnection";

interface RTCWellKnownStats {
	localCandidateType?: string;
	remoteCandidateType?: string;
}

type Status = "unknown" | "rtc-connection-loaded" | "hub-connection-or-rtc-peer-connection-loaded" | "offer-sent" | "connected" | "error";

export default function ControlTest() {
	const { deviceId } = useParams<{ deviceId: string }>();
	const { connected, hubConnection }= useSignalRContext();

	const [heartbeatStatus, setHeartbeatStatus] = useState<"disconnected" | "connected">("disconnected");

	const [status, setStatus] = useState<Status>("unknown");
	const [rtcWellKnownStats, setRtcWellKnownStats] = useState<RTCWellKnownStats | undefined>(undefined);

	const htmlVideoElementRefObject = useRef<HTMLVideoElement>(null);
	const rtcIceCandidateInitsRefObject = useRef<RTCIceCandidateInit[]>([]);
	const remotePeerConnectionIdRefObject = useRef<string | null>(null);
	const tracksAddedRefObject = useRef(false);
	const rtcPeerConnection = useRtcPeerConnection();

	useEffect(() => {
		if (!connected || !deviceId || !hubConnection) { return; }

		const interval = setInterval(() => {
			hubConnection.invoke("DeviceHeartbeat", deviceId)
				.then(() => setHeartbeatStatus("connected"))
				.catch(() => setHeartbeatStatus("disconnected"));
		}, 1000);

		return () => {
			clearInterval(interval);
			setHeartbeatStatus("disconnected");
		};
	}, [connected, deviceId, hubConnection]);

	useEffect(() => {
		if (!connected || !deviceId || !hubConnection || !rtcPeerConnection) { return; }

		hubConnection.on("ClientJoined", async (connectionId: string) => {
			if (remotePeerConnectionIdRefObject.current === connectionId) {
				return;
			}

			remotePeerConnectionIdRefObject.current = connectionId;

			const rtcOffer = await rtcPeerConnection.createOffer();
			await rtcPeerConnection.setLocalDescription(rtcOffer);
			await hubConnection.invoke("SendOffer", deviceId, connectionId, rtcOffer);
			setStatus("offer-sent");
		});

		hubConnection.on("ReceiveAnswer", async (connectionId: string, rtcSessionDescriptionInit: RTCSessionDescriptionInit) => {
			const flushPendingIceCandidates = async () => {
				if (!rtcPeerConnection.remoteDescription) {
					return;
				}

				const queuedCandidates = rtcIceCandidateInitsRefObject.current;
				rtcIceCandidateInitsRefObject.current = [];

				for (const queuedCandidate of queuedCandidates) {
					await rtcPeerConnection.addIceCandidate(queuedCandidate);
				}
			};

			remotePeerConnectionIdRefObject.current ??= connectionId;

			await rtcPeerConnection.setRemoteDescription(rtcSessionDescriptionInit);
			await flushPendingIceCandidates();

			setStatus("connected");
		});

		hubConnection.on("ReceiveIceCandidate", async (connectionId: string, rtcIceCandidateInit: RTCIceCandidateInit) => {
			remotePeerConnectionIdRefObject.current ??= connectionId;

			if (rtcPeerConnection.remoteDescription) {
				await rtcPeerConnection.addIceCandidate(rtcIceCandidateInit);
				return;
			}

			rtcIceCandidateInitsRefObject.current.push(rtcIceCandidateInit);
		});

		setStatus("hub-connection-or-rtc-peer-connection-loaded");
		
		return () => {};
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	useEffect(() => {
		if (!connected || !deviceId || !hubConnection || !rtcPeerConnection) { return; }

		rtcPeerConnection.onconnectionstatechange = () => {
			if (rtcPeerConnection.connectionState !== "connected") { return; }

			rtcPeerConnection.getStats()
				.then((x)=> { 
					x.forEach((report) => {
						if (report.type === "transport" && report.selectedCandidatePairId !== null){
							const selectedPair = x.get(report.selectedCandidatePairId);
							const local = x.get(selectedPair.localCandidateId);
							const remote = x.get(selectedPair.remoteCandidateId);
							setRtcWellKnownStats({ localCandidateType: local.candidateType, remoteCandidateType: remote.candidateType });
						}
						console.log(report)
					});
				});

			setStatus("connected");
		};

		rtcPeerConnection.onicecandidate = async (rtcPeerConnectionIceEvent) => {
			const localCandidate = rtcPeerConnectionIceEvent.candidate;
			const remotePeerConnectionId = remotePeerConnectionIdRefObject.current;

			if (!localCandidate || !remotePeerConnectionId) { return; }

			await hubConnection.invoke("SendIceCandidate", deviceId, remotePeerConnectionId, localCandidate.toJSON());
		};

		return () => {};
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	useEffectAsync(async () => {
		const htmlVideoElement = htmlVideoElementRefObject.current;

		if (!connected || !deviceId || !htmlVideoElement || !hubConnection || !rtcPeerConnection) { return; }

		const ensureLocalTracks = async () => {
			if (tracksAddedRefObject.current) {
				return;
			}

			const playPromise = htmlVideoElement.play();
			if (playPromise) {
				await playPromise;
			}

			const mediaStream = (htmlVideoElement as HTMLVideoElement & { captureStream(): MediaStream }).captureStream();
			for (const mediaStreamTrack of mediaStream.getVideoTracks()) {
				rtcPeerConnection.addTrack(mediaStreamTrack, mediaStream);
			}

			tracksAddedRefObject.current = true;
		};

		ensureLocalTracks();

		const rtcIceServers = await hubConnection.invoke("JoinAsDevice", deviceId, null) as RTCIceServer[];
		rtcPeerConnection?.setConfiguration({ iceServers: rtcIceServers });
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	return (
		<div style={{ display: "flex", flex: 1, flexDirection: "column", width: "100%" }}>
			<p style={{ margin: "auto 1rem" }}>Id: {deviceId} - Status: {status} - Heartbeat: {heartbeatStatus} - Local Candidate Type: {rtcWellKnownStats?.localCandidateType} - Remote Candidate Type: {rtcWellKnownStats?.remoteCandidateType}</p>
			<video
				autoPlay
				loop
				muted
				playsInline
				ref={htmlVideoElementRefObject}
				src={countdown}
				style={{ height: "80vh", margin: "auto", width: "80vw" }}
			/>
		</div>
	);
}
