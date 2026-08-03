using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace ScarletRadioControl.Web.Hubs;

public class WebRtcHub : Hub<WebRtcHub.IWebRtcClient>
{

	public async Task DeviceHeartbeat(string deviceId)
	{
		await this.Clients.OthersInGroup(deviceId).DeviceHeartbeated(this.Context.ConnectionId);
	}

	public async Task JoinAsClient(string deviceId)
	{
		await this.Groups.AddToGroupAsync(this.Context.ConnectionId, deviceId);
		await this.Clients.OthersInGroup(deviceId).ClientJoined(this.Context.ConnectionId);
	}

	public async Task JoinAsDevice(string deviceId)
	{
		await this.Groups.AddToGroupAsync(this.Context.ConnectionId, deviceId);
		await this.Clients.OthersInGroup(deviceId).DeviceJoined(this.Context.ConnectionId);
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

}
