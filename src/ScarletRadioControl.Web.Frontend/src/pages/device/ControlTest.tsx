import { useEffect, useRef, useState } from "react";
import { useParams } from "react-router-dom";
import countdown from "../../assets/countdown.mp4";
import useApiClient from "../../hooks/useApiClient";
import useRtcPeerConnection from "../../hooks/useRtcPeerConnection";
import { useSignalRContext } from "../../contexts/SignalRContext";

interface RTCWellKnownStats {
	localCandidateType?: string;
	remoteCandidateType?: string;
}

type Status = "unknown" | "rtc-connection-loaded" | "hub-connection-or-rtc-peer-connection-loaded" | "offer-sent" | "connected" | "error";

export default function ControlTest() {
	const apiClient = useApiClient();
	const { deviceId } = useParams<{ deviceId: string }>();
	const {connected, hubConnection}= useSignalRContext();

	const [heartbeatStatus, setHeartbeatStatus] = useState<"disconnected" | "connected">("disconnected");
	const [rtcConfigurationStatus, setRtcConfigurationStatus] = useState<"disconnected" | "connected">("disconnected");

	const [rtcConfiguration, setRtcConfiguration] = useState<RTCConfiguration | undefined>(undefined);
	const [status, setStatus] = useState<Status>("unknown");
	const [rtcWellKnownStats, setRtcWellKnownStats] = useState<RTCWellKnownStats | undefined>(undefined);

	const htmlVideoElementRefObject = useRef<HTMLVideoElement>(null);
	const rtcIceCandidateInitsRefObject = useRef<RTCIceCandidateInit[]>([]);
	const remotePeerConnectionIdRefObject = useRef<string | null>(null);
	const tracksAddedRefObject = useRef(false);
	const rtcPeerConnection = useRtcPeerConnection(rtcConfiguration);

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
		apiClient.api.v1.webRtc.rtcConfiguration.get()
			.then((response) => {
				setRtcConfiguration(response as RTCConfiguration);
				setRtcConfigurationStatus("connected");
			}
		).catch((reason) => {
			console.error(reason);
			setRtcConfigurationStatus("disconnected");
		});

		return () => {};
	}, [apiClient]);

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

	useEffect(() => {
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

		hubConnection.invoke("JoinAsDevice", deviceId, null)
			.catch((reason) => {
				console.error(reason);
				setStatus("error");
			});

		return () => { };
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	return (
		<div style={{ display: "flex", flex: 1, flexDirection: "column", width: "100%" }}>
			<p style={{ margin: "auto 1rem" }}>Id: {deviceId} - Status: {status} - Heartbeat: {heartbeatStatus} - Rtc Configuration: {rtcConfigurationStatus} - Local Candidate Type: {rtcWellKnownStats?.localCandidateType} - Remote Candidate Type: {rtcWellKnownStats?.remoteCandidateType}</p>
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
