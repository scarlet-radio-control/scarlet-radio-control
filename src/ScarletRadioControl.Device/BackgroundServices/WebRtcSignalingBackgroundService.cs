using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScarletRadioControl.Device.Options;
using ScarletRadioControl.Device.Signaling;
using ScarletRadioControl.Device.WebRtc;

namespace ScarletRadioControl.Device.BackgroundServices;

public class WebRtcSignalingBackgroundService(IOptions<DeviceOptions> deviceOptions, WebRtcSessionManager webRtcSessionManager, ILogger<WebRtcSignalingBackgroundService> logger) : BackgroundService
{

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var deviceOptionsValue = deviceOptions.Value;
		var deviceId = deviceOptionsValue.DeviceId;
		var hubUrl = deviceOptionsValue.HubUrl;

		var hubConnection = new HubConnectionBuilder()
			.WithUrl(hubUrl)
			.WithAutomaticReconnect()
			.Build();

		hubConnection.On<string>("ClientJoined", async clientConnectionId =>
		{
			logger.LogInformation("Client {ClientConnectionId} joined", clientConnectionId);
			try
			{
				var rtcSessionDescriptionInit = await webRtcSessionManager.CreateOfferAsync(clientConnectionId);
				await hubConnection.InvokeAsync("SendOffer", deviceId, clientConnectionId, rtcSessionDescriptionInit, stoppingToken);
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to create or send an offer for client {ClientConnectionId}", clientConnectionId);
			}
		});

		hubConnection.On<string, RtcSessionDescriptionInit>("ReceiveAnswer", (clientConnectionId, rtcSessionDescriptionInit) => webRtcSessionManager.ApplyAnswer(clientConnectionId, rtcSessionDescriptionInit));

		hubConnection.On<string, RtcIceCandidateInit>("ReceiveIceCandidate", (clientConnectionId, rtcIceCandidateInit) => webRtcSessionManager.AddIceCandidate(clientConnectionId, rtcIceCandidateInit));

		webRtcSessionManager.OnIceCandidate += async (clientConnectionId, rtcIceCandidateInit) =>
		{
			if (hubConnection.State != HubConnectionState.Connected)
			{
				return;
			}

			try
			{
				await hubConnection.InvokeAsync("SendIceCandidate", deviceId, clientConnectionId, rtcIceCandidateInit, stoppingToken);
			}
			catch (Exception exception)
			{
				logger.LogWarning(exception, "Failed to send an ice candidate to client {ClientConnectionId}", clientConnectionId);
			}
		};

		// Reconnecting yields a new hub connection id, so the group membership is lost and must be re-established.
		// Established peers keep streaming, the media is peer to peer.
		hubConnection.Reconnected += async _ =>
		{
			logger.LogInformation("Reconnected to hub {HubUrl}, rejoining as device {DeviceId}", hubUrl, deviceId);
			try
			{
				var rtcIceServers = await hubConnection.InvokeAsync<ICollection<RtcIceServer>>("JoinAsDevice", deviceId, null, stoppingToken);
				webRtcSessionManager.SetIceServers(rtcIceServers);
			}
			catch (Exception exception)
			{
				logger.LogError(exception, "Failed to rejoin as device {DeviceId}", deviceId);
			}
		};

		try
		{
			var disconnectedTickCount = 0;
			using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
			while (await periodicTimer.WaitForNextTickAsync(stoppingToken))
			{
				if (hubConnection.State == HubConnectionState.Disconnected)
				{
					if (disconnectedTickCount % 5 == 0)
					{
						try
						{
							await hubConnection.StartAsync(stoppingToken);
							var rtcIceServers = await hubConnection.InvokeAsync<ICollection<RtcIceServer>>("JoinAsDevice", deviceId, null, stoppingToken);
							webRtcSessionManager.SetIceServers(rtcIceServers);
							logger.LogInformation("Connected to hub {HubUrl} as device {DeviceId}", hubUrl, deviceId);
						}
						catch (OperationCanceledException)
						{
							throw;
						}
						catch (Exception exception)
						{
							logger.LogWarning(exception, "Failed to connect to hub {HubUrl}", hubUrl);
						}
					}
					disconnectedTickCount++;
					continue;
				}

				disconnectedTickCount = 0;

				if (hubConnection.State == HubConnectionState.Connected)
				{
					try
					{
						await hubConnection.InvokeAsync("DeviceHeartbeat", deviceId, stoppingToken);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception exception)
					{
						logger.LogWarning(exception, "Failed to send the device heartbeat");
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
			webRtcSessionManager.CloseAll();
			await hubConnection.DisposeAsync();
		}
	}

}
