using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarletRadioControl.Device.Options;
using ScarletRadioControl.Device.Services;
using ScarletRadioControl.Device.Signaling;

namespace ScarletRadioControl.Device.BackgroundServices;

public class WebRtcSignalingBackgroundService(
	IOptions<DeviceOptions> deviceOptions,
	HubConnection hubConnection,
	ILogger<WebRtcSignalingBackgroundService> logger,
	WebRtcSessionManager webRtcSessionManager
) : BackgroundService
{

	private readonly IOptions<DeviceOptions> deviceOptions = deviceOptions;
	private readonly HubConnection hubConnection = hubConnection;
	private readonly ILogger<WebRtcSignalingBackgroundService> logger = logger;
	private readonly WebRtcSessionManager webRtcSessionManager = webRtcSessionManager;

	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		var deviceOptionsValue = this.deviceOptions.Value;
		var deviceId = deviceOptionsValue.DeviceId;

		this.hubConnection.On<string>("ClientJoined", async clientConnectionId =>
		{
			this.logger.LogInformation("Client {ClientConnectionId} joined", clientConnectionId);
			try
			{
				var rtcSessionDescriptionInit = await this.webRtcSessionManager.CreateOfferAsync(clientConnectionId);
				await this.hubConnection.InvokeAsync("SendOffer", deviceId, clientConnectionId, rtcSessionDescriptionInit, cancellationToken);
			}
			catch (Exception exception)
			{
				this.logger.LogError(exception, "Failed to create or send an offer for client {ClientConnectionId}", clientConnectionId);
			}
		});

		this.hubConnection.On<string, RtcSessionDescriptionInit>("ReceiveAnswer", (clientConnectionId, rtcSessionDescriptionInit) => this.webRtcSessionManager.ApplyAnswer(clientConnectionId, rtcSessionDescriptionInit));

		this.hubConnection.On<string, RtcIceCandidateInit>("ReceiveIceCandidate", (clientConnectionId, rtcIceCandidateInit) => this.webRtcSessionManager.AddIceCandidate(clientConnectionId, rtcIceCandidateInit));

		this.webRtcSessionManager.OnIceCandidate += async (clientConnectionId, rtcIceCandidateInit) =>
		{
			if (this.hubConnection.State != HubConnectionState.Connected)
			{
				return;
			}

			try
			{
				await this.hubConnection.InvokeAsync("SendIceCandidate", deviceId, clientConnectionId, rtcIceCandidateInit, cancellationToken);
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "Failed to send an ice candidate to client {ClientConnectionId}", clientConnectionId);
			}
		};

		// Reconnecting yields a new hub connection id, so the group membership is lost and must be re-established.
		// Established peers keep streaming, the media is peer to peer.
		this.hubConnection.Reconnected += async _ =>
		{
			this.logger.LogInformation("Reconnected to hub {HubConnection}, rejoining as device {DeviceId}", this.hubConnection.ConnectionId, deviceId);
			try
			{
				var rtcIceServers = await this.hubConnection.InvokeAsync<ICollection<RtcIceServer>>("JoinAsDevice", deviceId, null, cancellationToken);
				this.webRtcSessionManager.SetIceServers(rtcIceServers);
			}
			catch (Exception exception)
			{
				this.logger.LogError(exception, "Failed to rejoin as device {DeviceId}", deviceId);
			}
		};

		try
		{
			var disconnectedTickCount = 0;
			using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await periodicTimer.WaitForNextTickAsync(cancellationToken))
			{
				if (this.hubConnection.State == HubConnectionState.Disconnected)
				{
					if (disconnectedTickCount % 5 == 0)
					{
						try
						{
							await this.hubConnection.StartAsync(cancellationToken);
							var rtcIceServers = await this.hubConnection.InvokeAsync<ICollection<RtcIceServer>>("JoinAsDevice", deviceId, null, cancellationToken);
							this.webRtcSessionManager.SetIceServers(rtcIceServers);
							this.logger.LogInformation("Connected to hub {HubConnection} as device {DeviceId}", this.hubConnection.ConnectionId, deviceId);
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception exception)
						{
							this.logger.LogWarning(exception, "Failed to connect to hub {HubConnection}", this.hubConnection.ConnectionId);
						}
					}
					disconnectedTickCount++;
					continue;
				}

				disconnectedTickCount = 0;

				if (this.hubConnection.State == HubConnectionState.Connected)
				{
					try
					{
						await this.hubConnection.InvokeAsync("DeviceHeartbeat", deviceId, cancellationToken);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception exception)
					{
						this.logger.LogWarning(exception, "Failed to send the device heartbeat");
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Host shutdown.
		}
		finally
		{
			this.webRtcSessionManager.CloseAll();
			await this.hubConnection.DisposeAsync();
		}
	}

}
