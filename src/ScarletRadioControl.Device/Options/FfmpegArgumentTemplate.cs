using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ScarletRadioControl.Device.Options;

// Expands the configured ffmpeg command line into an argument list. Splitting happens before
// substitution, and that order is what makes quoting unnecessary: {CameraPath} expands inside an
// already separated token, so "video=HD Pro Webcam C920" reaches ArgumentList as a single argument
// despite its spaces. Quotes written in the template still group a literal value, and are stripped.
public static partial class FfmpegArgumentTemplate
{

	// A quarter second of video at the configured bitrate. The buffer is deliberately small: a larger one
	// lets the encoder answer a busy scene with a burst, and on a cellular uplink that burst sits in the
	// modem's queue as latency that never comes back. Derived rather than configured so raising the bitrate
	// cannot silently leave a buffer sized for the old one.
	private const int BufferSizeDivisor = 4;

	private const int DefaultBitrateKilobitsPerSecond = 1500;

	private const int DefaultFramerate = 30;

	// Two seconds between keyframes. Nothing here answers a picture loss indication, so this interval is
	// also the worst case a viewer waits for its first picture and for recovery after packet loss.
	private const int DefaultKeyframeIntervalSeconds = 2;

	public static List<string> Expand(CameraOptions cameraOptions, FfmpegOptions ffmpegOptions, int rtpPort, int payloadType)
	{
		var framerate = cameraOptions.Framerate > 0 ? cameraOptions.Framerate : DefaultFramerate;
		var bitrateKilobitsPerSecond = ffmpegOptions.BitrateKilobitsPerSecond > 0 ? ffmpegOptions.BitrateKilobitsPerSecond : DefaultBitrateKilobitsPerSecond;
		var keyframeIntervalSeconds = ffmpegOptions.KeyframeIntervalSeconds > 0 ? ffmpegOptions.KeyframeIntervalSeconds : DefaultKeyframeIntervalSeconds;
		var placeholderValues = new Dictionary<string, string>
		{
			["Bitrate"] = $"{bitrateKilobitsPerSecond}k",
			["BufferSize"] = $"{bitrateKilobitsPerSecond / BufferSizeDivisor}k",
			["CameraPath"] = cameraOptions.GetPath(),
			["Framerate"] = $"{framerate}",
			// Expressed in frames because that is the only unit -g understands.
			["GopFrames"] = $"{framerate * keyframeIntervalSeconds}",
			["Height"] = $"{cameraOptions.Height}",
			["PayloadType"] = $"{payloadType}",
			["RtpPort"] = $"{rtpPort}",
			["Width"] = $"{cameraOptions.Width}",
		};

		var arguments = new List<string>();
		foreach (var token in Tokenise(ffmpegOptions.GetArguments()))
		{
			arguments.Add(Substitute(token, placeholderValues));
		}

		return arguments;
	}

	[GeneratedRegex(@"\{(\w+)\}")]
	private static partial Regex PlaceholderPattern();

	private static IEnumerable<string> Tokenise(string arguments)
	{
		var token = new StringBuilder();
		var insideQuotes = false;
		var started = false;

		foreach (var character in arguments)
		{
			if (character == '"')
			{
				// The quotes group the value, they are never part of it. Tracking started separately keeps an
				// explicitly empty "" argument alive.
				insideQuotes = !insideQuotes;
				started = true;
				continue;
			}

			if (!insideQuotes && char.IsWhiteSpace(character))
			{
				if (started)
				{
					yield return token.ToString();
					token.Clear();
					started = false;
				}

				continue;
			}

			token.Append(character);
			started = true;
		}

		if (started)
		{
			yield return token.ToString();
		}
	}

	private static string Substitute(string argument, Dictionary<string, string> placeholderValues)
	{
		return PlaceholderPattern().Replace(
			argument,
			match => placeholderValues.TryGetValue(match.Groups[1].Value, out var placeholderValue)
				? placeholderValue
				// Failing loudly beats spawning ffmpeg with a literal brace in its arguments and reading the
				// confusion out of its stderr later.
				: throw new InvalidOperationException($"The configured ffmpeg arguments use the unknown placeholder {{{match.Groups[1].Value}}}. The supported placeholders are {string.Join(", ", placeholderValues.Keys)}."));
	}

}
