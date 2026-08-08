import { useEffect, useRef, useState } from "react";
import { useParams } from "react-router-dom";
import useApiClient from "../../hooks/useApiClient";
import useRtcPeerConnection from "../../hooks/useRtcPeerConnection";
import { useSignalRContext } from "../../contexts/SignalRContext";

interface RTCWellKnownStats {
	localCandidateType?: string;
	remoteCandidateType?: string;
}

type Status = "unknown" | "rtc-connection-loaded" | "hub-connection-or-rtc-peer-connection-loaded" | "answer-sent" | "connected" | "error";

export default function Control() {
	const apiClient = useApiClient();
	const { deviceId } = useParams<{ deviceId: string }>();
	const { connected, hubConnection }= useSignalRContext();

	const [rtcConfigurationStatus, setRtcConfigurationStatus] = useState<"disconnected" | "connected">("disconnected");

	const [rtcConfiguration, setRtcConfiguration] = useState<RTCConfiguration | undefined>(undefined);
	const [status, setStatus] = useState<Status>("unknown");
	const [rtcWellKnownStats, setRtcWellKnownStats] = useState<RTCWellKnownStats | undefined>(undefined);

	const htmlVideoElementRefObject = useRef<HTMLVideoElement>(null);
	const rtcIceCandidateInitsRefObject = useRef<RTCIceCandidateInit[]>([]);
	const remotePeerConnectionIdRefObject = useRef<string | null>(null);
	const rtcPeerConnection = useRtcPeerConnection(rtcConfiguration);

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

		hubConnection.on("DeviceHeartbeated", async (connectionId: string) => {
			console.log(`Heartbeat received from ${connectionId}`);
		});

		hubConnection.on("DeviceJoined", async (connectionId: string) => {
			console.log(`Device joined with connection id ${connectionId}`);
		});

		hubConnection.on("ReceiveIceCandidate", async (connectionId: string, rtcIceCandidateInit: RTCIceCandidateInit) => {
			remotePeerConnectionIdRefObject.current ??= connectionId;

			if (rtcPeerConnection.remoteDescription) {
				await rtcPeerConnection.addIceCandidate(rtcIceCandidateInit);
				return;
			}

			rtcIceCandidateInitsRefObject.current.push(rtcIceCandidateInit);
		});

		hubConnection.on("ReceiveOffer", async (connectionId: string, rtcSessionDescriptionInit: RTCSessionDescriptionInit) => {
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

			remotePeerConnectionIdRefObject.current = connectionId;

			await rtcPeerConnection.setRemoteDescription(rtcSessionDescriptionInit);

			const rtcAnswer = await rtcPeerConnection.createAnswer();
			await rtcPeerConnection.setLocalDescription(rtcAnswer);

			await hubConnection.invoke("SendAnswer", deviceId, connectionId, rtcAnswer);
			await flushPendingIceCandidates();
			setStatus("answer-sent");
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

		rtcPeerConnection.ontrack = (rtcPeerConnectionTrackEvent) => {
			const htmlVideoElement = htmlVideoElementRefObject.current;
			const mediaStream = rtcPeerConnectionTrackEvent.streams[0];

			if (!htmlVideoElement || !mediaStream) { return; }

			htmlVideoElement.srcObject = mediaStream;
			htmlVideoElement.play()
				.catch((reason) => {
					console.error(reason);
				});
		};

		setStatus("hub-connection-or-rtc-peer-connection-loaded");

		return () => {};
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	useEffect(() => {
		if (!connected || !deviceId || !hubConnection || !rtcPeerConnection) { return; }

		if (rtcPeerConnection.connectionState !== "new") { return; }

		hubConnection.invoke("JoinAsClient", deviceId, null)
			.catch((reason) => {
				console.error(reason);
				setStatus("error");
			});

		return () => { };
	}, [connected, deviceId, hubConnection, rtcPeerConnection]);

	return (
		<div style={{ display: "flex", flex: 1, flexDirection: "column", width: "100%" }}>
			<p style={{ margin: "auto 1rem" }}>Id: {deviceId} - Status: {status} - Rtc Configuration: {rtcConfigurationStatus} - Local Candidate Type: {rtcWellKnownStats?.localCandidateType} - Remote Candidate Type: {rtcWellKnownStats?.remoteCandidateType}</p>
			<video
				autoPlay
				muted
				playsInline
				ref={htmlVideoElementRefObject}
				style={{ backgroundColor: "#000000", height: "95vh" }}
			/>
		</div>
	);
}
