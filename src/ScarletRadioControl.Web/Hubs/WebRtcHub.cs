using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace ScarletRadioControl.Web.Hubs;

public class WebRtcHub : Hub<WebRtcHub.IWebRtcClient>
{

	private readonly ICollection<RtcIceServer> rtcIceServers = new HashSet<RtcIceServer> {
		new RtcIceServer { Credential = null, Urls = new List<string> { "stun:stun.l.google.com:19302", }, Username = null, },
		new RtcIceServer { Credential = null, Urls = new List<string> { "stun:stun.relay.metered.ca:80", }, Username = null, },
		new RtcIceServer { Credential = "xkw2mfGQr0ZAODKl", Urls = new List<string> { "turn:global.relay.metered.ca:80", }, Username = "b6e796d3b6bc333d4bf58b84", },
		new RtcIceServer { Credential = "xkw2mfGQr0ZAODKl", Urls = new List<string> { "turn:global.relay.metered.ca:80?transport=tcp", }, Username = "b6e796d3b6bc333d4bf58b84", },
		new RtcIceServer { Credential = "xkw2mfGQr0ZAODKl", Urls = new List<string> { "turn:global.relay.metered.ca:443", }, Username = "b6e796d3b6bc333d4bf58b84", },
		new RtcIceServer { Credential = "xkw2mfGQr0ZAODKl", Urls = new List<string> { "turns:global.relay.metered.ca:443?transport=tcp", }, Username = "b6e796d3b6bc333d4bf58b84", },
	};

	public async Task DeviceHeartbeat(string deviceId)
	{
		await this.Clients.OthersInGroup(deviceId).DeviceHeartbeated(this.Context.ConnectionId);
	}

	public async Task<ICollection<RtcIceServer>> JoinAsClient(string deviceId, RtcSessionDescriptionInit? _)
	{
		await this.Groups.AddToGroupAsync(this.Context.ConnectionId, deviceId);
		await this.Clients.OthersInGroup(deviceId).ClientJoined(this.Context.ConnectionId);
		return this.rtcIceServers;
	}

	public async Task<ICollection<RtcIceServer>> JoinAsDevice(string deviceId, RtcSessionDescriptionInit? _)
	{
		await this.Groups.AddToGroupAsync(this.Context.ConnectionId, deviceId);
		await this.Clients.OthersInGroup(deviceId).DeviceJoined(this.Context.ConnectionId);
		return this.rtcIceServers;
	}

	public async Task SendOffer(string deviceId, string targetConnectionId, object rtcSessionDescriptionInit)
	{
		await this.Clients.Client(targetConnectionId).ReceiveOffer(this.Context.ConnectionId, rtcSessionDescriptionInit);
	}

	public async Task SendAnswer(string deviceId, string targetConnectionId, object rtcSessionDescriptionInit)
	{
		await this.Clients.Client(targetConnectionId).ReceiveAnswer(this.Context.ConnectionId, rtcSessionDescriptionInit);
	}

	public async Task SendIceCandidate(string deviceId, string targetConnectionId, object rtcIceCandidate)
	{
		await this.Clients.Client(targetConnectionId).ReceiveIceCandidate(this.Context.ConnectionId, rtcIceCandidate);
	}

	public interface IWebRtcClient
	{
		Task ClientJoined(string connectionId);

		Task DeviceHeartbeated(string fromConnectionId);

		Task DeviceJoined(string connectionId);

		Task ReceiveOffer(string fromConnectionId, object rtcSessionDescriptionInit);

		Task ReceiveAnswer(string fromConnectionId, object rtcSessionDescriptionInit);

		Task ReceiveIceCandidate(string fromConnectionId, object rtcIceCandidate);
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
