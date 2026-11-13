#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenRA.Mods.Sp.Ranked
{
	/// <summary>Configured in mod.yaml. SP_RANKED_BACKEND_URL overrides the URL (handy for local testing).</summary>
	public class RankedSettings : IGlobalModData
	{
		public readonly string BackendUrl = "http://localhost:8090";

		public string ResolvedBackendUrl =>
			(Environment.GetEnvironmentVariable("SP_RANKED_BACKEND_URL") ?? BackendUrl).TrimEnd('/');
	}

	public sealed class ApiResult
	{
		public bool Ok;
		public int Status;
		public JsonElement Body;
		public string Error;
	}

	/// <summary>Thin HTTP client for the ranked backend. Callbacks are always delivered on the game thread.</summary>
	public static class RankedApi
	{
		static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

		// Lets one machine run two clients as different users: launch with SP_RANKED_PROFILE=2.
		static string SessionFile
		{
			get
			{
				var profile = Environment.GetEnvironmentVariable("SP_RANKED_PROFILE");
				var name = string.IsNullOrEmpty(profile) ? "ranked-session.txt" : $"ranked-session-{profile}.txt";
				return Path.Combine(Platform.SupportDir, name);
			}
		}

		public static string Token { get; private set; }

		public static string BaseUrl => Game.ModData.Manifest.Get<RankedSettings>().ResolvedBackendUrl;

		public static string ClientVersion => Game.ModData.Manifest.Metadata.Version;

		public const string DefaultDownloadUrl = "https://github.com/almshadi/Shattered-Paradise-sdk/releases/latest";

		/// <summary>True unless a /config response explicitly says this client version can't play ranked.</summary>
		public static bool VersionOk(this JsonElement config) =>
			!(config.ValueKind == JsonValueKind.Object && config.TryGetProperty("versionOk", out var v) && v.ValueKind == JsonValueKind.False);

		public static void LoadToken()
		{
			try
			{
				Token = File.Exists(SessionFile) ? File.ReadAllText(SessionFile).Trim() : null;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[ranked] Could not read session: {e.Message}");
				Token = null;
			}
		}

		public static void SaveToken(string token)
		{
			Token = token;
			try
			{
				if (token == null)
					File.Delete(SessionFile);
				else
					File.WriteAllText(SessionFile, token);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[ranked] Could not write session: {e.Message}");
			}
		}

		public static void Get(string path, Action<ApiResult> done) => Send(HttpMethod.Get, path, null, done);
		public static void Post(string path, object body, Action<ApiResult> done) => Send(HttpMethod.Post, path, body, done);
		public static void Delete(string path, Action<ApiResult> done) => Send(HttpMethod.Delete, path, null, done);
		public static void Put(string path, object body, Action<ApiResult> done) => Send(HttpMethod.Put, path, body, done);

		static void Send(HttpMethod method, string path, object body, Action<ApiResult> done)
		{
			var url = BaseUrl + path;
			var token = Token;
			Task.Run(async () =>
			{
				var result = new ApiResult();
				try
				{
					var req = new HttpRequestMessage(method, url);
					if (token != null)
						req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

					req.Headers.Add("X-Client-Version", ClientVersion);
					if (body != null)
						req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

					var res = await Http.SendAsync(req);
					result.Status = (int)res.StatusCode;
					result.Ok = res.IsSuccessStatusCode;
					var text = (await res.Content.ReadAsStringAsync()).TrimStart();
					if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
						result.Body = JsonDocument.Parse(text).RootElement.Clone();

					if (!result.Ok)
						result.Error = result.Body.ValueKind == JsonValueKind.Object && result.Body.TryGetProperty("error", out var e)
							? e.GetString()
							: $"HTTP {result.Status}";
				}
				catch (Exception e)
				{
					result.Error = $"Cannot reach ranked server ({e.GetType().Name})";
				}

				Game.RunAfterTick(() => done(result));
			});
		}

		public static void OpenInBrowser(string url)
		{
			try
			{
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
			}
			catch (Exception e)
			{
				Log.Write("debug", $"[ranked] Could not open browser: {e.Message}");
			}
		}

		public static string Str(this JsonElement e, string name) =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		public static double? Num(this JsonElement e, string name) =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

		public static bool Bool(this JsonElement e, string name) =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

		public static JsonElement Obj(this JsonElement e, string name) =>
			e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

		// Binary downloads (match replays). Separate client: replays can take longer than the 10s API timeout.
		static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

		/// <summary>
		/// GETs raw bytes. The callback (game thread) gets the result, the body on success
		/// and the server-suggested file name from Content-Disposition (may be null).
		/// </summary>
		public static void Download(string path, Action<ApiResult, byte[], string> done)
		{
			var url = BaseUrl + path;
			var token = Token;
			Task.Run(async () =>
			{
				var result = new ApiResult();
				byte[] data = null;
				string fileName = null;
				try
				{
					var req = new HttpRequestMessage(HttpMethod.Get, url);
					if (token != null)
						req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

					req.Headers.Add("X-Client-Version", ClientVersion);
					var res = await DownloadHttp.SendAsync(req);
					result.Status = (int)res.StatusCode;
					result.Ok = res.IsSuccessStatusCode;
					if (result.Ok)
					{
						data = await res.Content.ReadAsByteArrayAsync();
						var cd = res.Content.Headers.ContentDisposition;
						fileName = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"');
					}
					else
					{
						var text = (await res.Content.ReadAsStringAsync()).TrimStart();
						if (text.StartsWith("{", StringComparison.Ordinal))
							result.Body = JsonDocument.Parse(text).RootElement.Clone();

						result.Error = result.Body.ValueKind == JsonValueKind.Object && result.Body.TryGetProperty("error", out var e)
							? e.GetString()
							: $"HTTP {result.Status}";
					}
				}
				catch (Exception e)
				{
					result.Ok = false;
					result.Error = $"Cannot reach ranked server ({e.GetType().Name})";
				}

				Game.RunAfterTick(() => done(result, data, fileName));
			});
		}
	}
}
