using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using ScarletRadioControl.Device.Options;
using ScarletRadioControl.Device.Signaling;

namespace ScarletRadioControl.Device.Services;

public class WebRtcSignalingClient(
	HubConnection hubConnection,
	IOptions<DeviceOptions> options
)
{

	private readonly HubConnection hubConnection = hubConnection;
	private readonly IOptions<DeviceOptions> options = options;

	public string? ConnectionId => this.hubConnection.ConnectionId;

	public string DeviceId => this.options.Value.DeviceId;

	public bool IsConnected => this.hubConnection.State == HubConnectionState.Connected;

	public bool IsDisconnected => this.hubConnection.State == HubConnectionState.Disconnected;

	public event Func<string?, Task> Reconnected
	{
		add => this.hubConnection.Reconnected += value;
		remove => this.hubConnection.Reconnected -= value;
	}

	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		await this.hubConnection.StartAsync(cancellationToken);
	}

	public async Task DisconnectAsync(CancellationToken cancellationToken)
	{
		await this.hubConnection.StopAsync(cancellationToken);
	}

	public async Task<ICollection<RtcIceServer>> JoinAsDeviceAsync(CancellationToken cancellationToken)
	{
		return await this.hubConnection.InvokeAsync<ICollection<RtcIceServer>>("JoinAsDevice", this.DeviceId, null, cancellationToken);
	}

	public IDisposable OnClientJoined(Func<string, Task> handler)
	{
		return this.hubConnection.On<string>("ClientJoined", handler);
	}

	public IDisposable OnReceiveAnswer(Action<string, RtcSessionDescriptionInit> handler)
	{
		return this.hubConnection.On<string, RtcSessionDescriptionInit>("ReceiveAnswer", handler);
	}

	public IDisposable OnReceiveIceCandidate(Action<string, RtcIceCandidateInit> handler)
	{
		return this.hubConnection.On<string, RtcIceCandidateInit>("ReceiveIceCandidate", handler);
	}

	public async Task SendDeviceHeartbeatAsync(CancellationToken cancellationToken)
	{
		await this.hubConnection.InvokeAsync("DeviceHeartbeat", this.DeviceId, cancellationToken);
	}

	public async Task SendIceCandidateAsync(string clientConnectionId, RtcIceCandidateInit rtcIceCandidateInit, CancellationToken cancellationToken)
	{
		await this.hubConnection.InvokeAsync("SendIceCandidate", this.DeviceId, clientConnectionId, rtcIceCandidateInit, cancellationToken);
	}

	public async Task SendOfferAsync(string clientConnectionId, RtcSessionDescriptionInit rtcSessionDescriptionInit, CancellationToken cancellationToken)
	{
		await this.hubConnection.InvokeAsync("SendOffer", this.DeviceId, clientConnectionId, rtcSessionDescriptionInit, cancellationToken);
	}

	public record RtcIceServer
	{
		public required string? Credential { get; init; }
		public required ICollection<string>? Urls { get; init; }
		public required string? Username { get; init; }
	}

	public record RtcSessionDescriptionInit
	{
		public required string Sdp { get; init; }
		public required string Type { get; init; }
	}

}
