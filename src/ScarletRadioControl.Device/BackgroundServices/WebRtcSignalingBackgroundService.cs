using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarletRadioControl.Device.Services;

namespace ScarletRadioControl.Device.BackgroundServices;

public class WebRtcSignalingBackgroundService(
	ILogger<WebRtcSignalingBackgroundService> logger,
	WebRtcSessionManager webRtcSessionManager,
	WebRtcSignalingClient webRtcSignalingClient
) : BackgroundService
{

	private readonly ILogger<WebRtcSignalingBackgroundService> logger = logger;
	private readonly WebRtcSessionManager webRtcSessionManager = webRtcSessionManager;
	private readonly WebRtcSignalingClient webRtcSignalingClient = webRtcSignalingClient;

	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		var deviceId = this.webRtcSignalingClient.DeviceId;

		this.webRtcSignalingClient.OnClientJoined(async clientConnectionId =>
		{
			this.logger.LogInformation("Client {ClientConnectionId} joined", clientConnectionId);
			try
			{
				var rtcSessionDescriptionInit = await this.webRtcSessionManager.CreateOfferAsync(clientConnectionId);
				await this.webRtcSignalingClient.SendOfferAsync(clientConnectionId, rtcSessionDescriptionInit, cancellationToken);
			}
			catch (Exception exception)
			{
				this.logger.LogError(exception, "Failed to create or send an offer for client {ClientConnectionId}", clientConnectionId);
			}
		});

		this.webRtcSignalingClient.OnReceiveAnswer((clientConnectionId, rtcSessionDescriptionInit) => this.webRtcSessionManager.ApplyAnswer(clientConnectionId, rtcSessionDescriptionInit));

		this.webRtcSignalingClient.OnReceiveIceCandidate((clientConnectionId, rtcIceCandidateInit) => this.webRtcSessionManager.AddIceCandidate(clientConnectionId, rtcIceCandidateInit));

		this.webRtcSessionManager.OnIceCandidate += async (clientConnectionId, rtcIceCandidateInit) =>
		{
			if (!this.webRtcSignalingClient.IsConnected)
			{
				return;
			}

			try
			{
				await this.webRtcSignalingClient.SendIceCandidateAsync(clientConnectionId, rtcIceCandidateInit, cancellationToken);
			}
			catch (Exception exception)
			{
				this.logger.LogWarning(exception, "Failed to send an ice candidate to client {ClientConnectionId}", clientConnectionId);
			}
		};

		// Reconnecting yields a new hub connection id, so the group membership is lost and must be re-established.
		// Established peers keep streaming, the media is peer to peer.
		this.webRtcSignalingClient.Reconnected += async _ =>
		{
			this.logger.LogInformation("Reconnected to hub {HubConnection}, rejoining as device {DeviceId}", this.webRtcSignalingClient.ConnectionId, deviceId);
			try
			{
				var rtcIceServers = await this.webRtcSignalingClient.JoinAsDeviceAsync(cancellationToken);
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
				if (this.webRtcSignalingClient.IsDisconnected)
				{
					if (disconnectedTickCount % 5 == 0)
					{
						try
						{
							await this.webRtcSignalingClient.ConnectAsync(cancellationToken);
							var rtcIceServers = await this.webRtcSignalingClient.JoinAsDeviceAsync(cancellationToken);
							this.webRtcSessionManager.SetIceServers(rtcIceServers);
							this.logger.LogInformation("Connected to hub {HubConnection} as device {DeviceId}", this.webRtcSignalingClient.ConnectionId, deviceId);
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception exception)
						{
							this.logger.LogWarning(exception, "Failed to connect to hub {HubConnection}", this.webRtcSignalingClient.ConnectionId);
						}
					}
					disconnectedTickCount++;
					continue;
				}

				disconnectedTickCount = 0;

				if (this.webRtcSignalingClient.IsConnected)
				{
					try
					{
						await this.webRtcSignalingClient.SendDeviceHeartbeatAsync(cancellationToken);
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
			await this.webRtcSignalingClient.DisconnectAsync(CancellationToken.None);
		}
	}

}
