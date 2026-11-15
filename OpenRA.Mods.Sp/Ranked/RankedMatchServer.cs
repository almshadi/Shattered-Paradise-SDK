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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Server;
using FactionInfo = OpenRA.Traits.FactionInfo;
using S = OpenRA.Server.Server;

namespace OpenRA.Mods.Sp.Ranked
{
	/// <summary>
	/// Turns a dedicated server into a single ranked match controlled by the ranked backend.
	/// Inactive unless the backend launched this process (SP_RANKED_MATCH_ID is set), so normal servers are unaffected.
	/// Responsibilities: only admit the two matched players, lock the lobby down, auto-start, and report the result.
	/// </summary>
	public class RankedMatchServer : ServerTrait, INotifyServerStart, IInterpretCommand, IStartGame, ITick, INotifyServerEmpty, IEndGame
	{
		const string AuthCommand = "ranked-auth";

		// Faction and color are chosen in the ranked panel before queueing, so the lobby is fully locked.
		// The game starts by itself shortly after both players have loaded the map.
		static readonly TimeSpan StartCountdown = TimeSpan.FromSeconds(10);

		// Fallbacks when both players picked (nearly) the same color. Taken from the mod's color presets.
		static readonly string[] FallbackColors = { "F21818", "1821F2", "18F26F", "F2BC18", "BA18F2", "78DBF8", "FF7A22", "F218BC" };

		// Grace periods.
		static readonly TimeSpan UnauthenticatedKickAfter = TimeSpan.FromSeconds(20);
		static readonly TimeSpan LobbyLeaveAbortAfter = TimeSpan.FromSeconds(60);
		// Leaving a running game is a loss: OpenRA can't rejoin a started game, so there is nothing to wait for.
		// The short settle time only lets a surrender/defeat sent right before quitting arrive first.
		static readonly TimeSpan ForfeitSettle = TimeSpan.FromSeconds(3);

		static readonly FieldInfo GameInfoField = typeof(S).GetField("gameInfo", BindingFlags.Instance | BindingFlags.NonPublic);

		readonly string matchId = Environment.GetEnvironmentVariable("SP_RANKED_MATCH_ID");
		readonly string backendUrl = Environment.GetEnvironmentVariable("SP_RANKED_BACKEND_URL")?.TrimEnd('/');
		readonly string serverSecret = Environment.GetEnvironmentVariable("SP_RANKED_SERVER_SECRET");

		readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
		readonly List<ExpectedPlayer> expected = new();
		readonly Dictionary<int, ExpectedPlayer> clientToPlayer = new();
		readonly Dictionary<Connection, DateTime> unauthenticatedSince = new();
		readonly Dictionary<string, DateTime> missingSince = new();

		// When each player was first seen gone from a running game (Tick granularity: 0.5 s).
		readonly Dictionary<string, DateTime> leftGameAt = new();
		readonly List<Task> pendingReports = new();
		readonly Stopwatch tickTimer = Stopwatch.StartNew();

		bool gameStarted;
		bool finished;
		bool gameEnded;
		DateTime? startAt;
		int lastAnnouncedSecond = -1;

		bool Active => !string.IsNullOrEmpty(matchId);

		sealed class ExpectedPlayer
		{
			public string UserId;
			public string Name;
			public string TicketHash;
			public string Faction;
			public string Color;
		}

		public void ServerStarted(S server)
		{
			if (!Active)
				return;

			Log.Write("server", $"[ranked] Server for match {matchId} starting, backend {backendUrl}");

			// Blocking here is fine: nobody can connect before ServerStarted returns.
			for (var attempt = 1; attempt <= 10 && expected.Count == 0; attempt++)
			{
				try
				{
					var req = new HttpRequestMessage(HttpMethod.Get, $"{backendUrl}/server/matches/{matchId}");
					req.Headers.Add("X-Server-Secret", serverSecret);
					var res = http.Send(req);
					res.EnsureSuccessStatusCode();
					using var doc = JsonDocument.Parse(res.Content.ReadAsStream());
					foreach (var p in doc.RootElement.GetProperty("players").EnumerateArray())
					{
						expected.Add(new ExpectedPlayer
						{
							UserId = p.GetProperty("userId").GetString(),
							Name = p.GetProperty("name").GetString(),
							TicketHash = p.GetProperty("ticketHash").GetString(),
							Faction = p.TryGetProperty("faction", out var f) ? f.GetString() : null,
							Color = p.TryGetProperty("color", out var c) ? c.GetString() : null
						});
					}
				}
				catch (Exception e)
				{
					Log.Write("server", $"[ranked] Fetching match failed (attempt {attempt}): {e.Message}");
					System.Threading.Thread.Sleep(1000);
				}
			}

			if (expected.Count != 2)
			{
				Log.Write("server", "[ranked] Could not load match from backend, exiting.");
				Exit(server, 2);
			}

			Log.Write("server", $"[ranked] Expecting {string.Join(" vs ", expected.Select(p => p.Name))}");

			// Listeners are already open when ServerStarted runs; tell the backend players can connect.
			Report("ready", new { });
		}

		public bool InterpretCommand(S server, Connection conn, Session.Client client, string cmd)
		{
			if (!Active || client == null)
				return false;

			var name = cmd.Split(' ')[0];
			if (name == AuthCommand)
			{
				Authenticate(server, conn, client, cmd);
				return true;
			}

			var authenticated = clientToPlayer.ContainsKey(client.Index);

			// Clients report "map loaded" via "state NotReady"; let that through, but never "Ready":
			// the game is started by this trait, not by LobbyCommands' all-ready auto start.
			if (name == "state" && cmd.Trim() != "state Ready")
				return false;

			// Swallow everything else so nobody can change map, options, slots, factions, colors, teams, etc.
			server.SendOrderTo(conn, "Message", authenticated
				? "Ranked match: settings are locked. The game starts automatically."
				: "Ranked match: waiting for ranked authentication.");
			return true;
		}

		void Authenticate(S server, Connection conn, Session.Client client, string cmd)
		{
			var parts = cmd.Split(' ');
			var player = parts.Length == 3 ? expected.FirstOrDefault(p => p.UserId == parts[1]) : null;
			if (player == null || !FixedTimeEquals(player.TicketHash, Sha256Hex(parts[2])))
			{
				Log.Write("server", $"[ranked] Rejected ranked auth from client {client.Index}");
				server.SendOrderTo(conn, "ServerError", "Invalid ranked match ticket.");
				server.DropClient(conn);
				return;
			}

			if (clientToPlayer.Any(kv => kv.Value == player && kv.Key != client.Index))
			{
				// Same account connected twice (e.g. a reconnect): keep the new connection.
				var old = clientToPlayer.First(kv => kv.Value == player).Key;
				clientToPlayer.Remove(old);
				var oldConn = server.Conns.FirstOrDefault(c => c.PlayerIndex == old);
				if (oldConn != null)
					server.DropClient(oldConn);
			}

			clientToPlayer[client.Index] = player;
			unauthenticatedSince.Remove(conn);
			missingSince.Remove(player.UserId);

			// Show the nickname from the backend, not whatever the local profile says.
			// Faction stays "Random" in the lobby so the opponent can't see the real pick before the game starts.
			client.Name = player.Name;
			client.Faction = "Random";
			if (player.Color != null && Color.TryParse(player.Color, out var color))
				client.Color = client.PreferredColor = color;

			server.SyncLobbyClients();
			server.SendMessage($"{player.Name} joined the ranked match.");

			Report("joined", new { userId = player.UserId });
			Log.Write("server", $"[ranked] Client {client.Index} authenticated as {player.Name} ({player.UserId})");
		}

		public void GameStarted(S server)
		{
			if (!Active)
				return;

			gameStarted = true;
			var factions = server.LobbyInfo.Clients
				.Where(c => clientToPlayer.ContainsKey(c.Index))
				.Select(c => new { userId = clientToPlayer[c.Index].UserId, faction = c.Faction })
				.ToList();

			Report("started", new { factions });
		}

		public void Tick(S server)
		{
			if (!Active || finished || tickTimer.ElapsedMilliseconds < 500)
				return;

			tickTimer.Restart();
			var now = DateTime.UtcNow;

			if (!gameStarted)
			{
				KickUnauthenticated(server, now);
				CheckLobbyLeavers(server, now);
				CheckAutoStart(server, now);
				return;
			}

			CheckOutcome(server, now);
		}

		void KickUnauthenticated(S server, DateTime now)
		{
			foreach (var conn in server.Conns.Where(c => c.Validated).ToList())
			{
				var client = server.GetClient(conn);
				if (client == null || clientToPlayer.ContainsKey(client.Index))
					continue;

				if (!unauthenticatedSince.TryGetValue(conn, out var since))
					unauthenticatedSince[conn] = now;
				else if (now - since > UnauthenticatedKickAfter)
				{
					unauthenticatedSince.Remove(conn);
					server.SendOrderTo(conn, "ServerError", "This server is reserved for a ranked match.");
					server.DropClient(conn);
				}
			}
		}

		// A matched player left the lobby (or never showed up after joining). Cancel instead of waiting for the backend timeout.
		void CheckLobbyLeavers(S server, DateTime now)
		{
			foreach (var player in clientToPlayer.Values.Distinct().ToList())
			{
				var connected = ConnectedClientIndexes(server).Any(i => clientToPlayer.TryGetValue(i, out var p) && p == player);
				if (connected)
				{
					missingSince.Remove(player.UserId);
					continue;
				}

				if (!missingSince.TryGetValue(player.UserId, out var since))
					missingSince[player.UserId] = now;
				else if (now - since > LobbyLeaveAbortAfter)
					Finish("abort", new { reason = $"{player.Name}_left_lobby" });
			}
		}

		// Both matched players connected and map loaded -> countdown -> apply hidden picks -> start.
		void CheckAutoStart(S server, DateTime now)
		{
			var ready = server.Conns.Where(c => c.Validated)
				.Select(c => server.GetClient(c))
				.Where(c => c != null && clientToPlayer.ContainsKey(c.Index) && !c.IsInvalid)
				.Select(c => clientToPlayer[c.Index])
				.Distinct()
				.Count() == expected.Count;

			if (!ready)
			{
				if (startAt != null)
					server.SendMessage("Countdown cancelled: waiting for both players.");

				startAt = null;
				lastAnnouncedSecond = -1;
				return;
			}

			startAt ??= now + StartCountdown;
			var remaining = (int)Math.Ceiling((startAt.Value - now).TotalSeconds);
			if (remaining > 0)
			{
				if (remaining != lastAnnouncedSecond && (remaining == 10 || remaining <= 3))
					server.SendMessage($"Ranked match starts in {remaining}...");

				lastAnnouncedSecond = remaining;
				return;
			}

			ApplyPicks(server);
			Log.Write("server", "[ranked] Starting game.");
			server.StartGame();
		}

		void ApplyPicks(S server)
		{
			var selectable = server.Map.WorldActorInfo.TraitInfos<FactionInfo>()
				.Where(f => f.Selectable).Select(f => f.InternalName).ToHashSet();

			var clients = server.LobbyInfo.Clients.Where(c => clientToPlayer.ContainsKey(c.Index)).ToList();
			foreach (var c in clients)
			{
				var pick = clientToPlayer[c.Index].Faction;
				c.Faction = pick != null && selectable.Contains(pick) ? pick : "Random";
				c.State = Session.ClientState.Ready;
			}

			// Two near-identical colors would make the game unreadable; recolor the second player.
			if (clients.Count == 2 && ColorDistance(clients[0].Color, clients[1].Color) < 80)
			{
				var replacement = FallbackColors
					.Select(h => Color.TryParse(h, out var col) ? col : clients[0].Color)
					.OrderByDescending(col => ColorDistance(col, clients[0].Color))
					.First();
				clients[1].Color = replacement;
			}

			// Lobby info is sent before the StartGame order, so every client simulates the same factions.
			server.SyncLobbyClients();
		}

		static double ColorDistance(Color a, Color b) =>
			Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

		void CheckOutcome(S server, DateTime now)
		{
			var gameInfo = GameInfoField?.GetValue(server) as GameInformation;
			if (gameInfo == null)
				return;

			var players = gameInfo.Players
				.Where(p => clientToPlayer.ContainsKey(p.ClientIndex))
				.Select(p => (Info: p, Ranked: clientToPlayer[p.ClientIndex]))
				.ToList();

			// Normal end: the server derives defeats from the synced game state, so clients can't fake it.
			var winner = players.FirstOrDefault(p => p.Info.Outcome == WinState.Won);
			if (winner.Ranked != null)
			{
				Finish("result", new { winnerUserId = winner.Ranked.UserId, reason = "defeat" });
				return;
			}

			var loser = players.FirstOrDefault(p => p.Info.Outcome == WinState.Lost);
			if (loser.Ranked != null && players.Count == 2)
			{
				var other = players.First(p => p.Ranked != loser.Ranked);
				Finish("result", new { winnerUserId = other.Ranked.UserId, reason = "defeat" });
				return;
			}

			// Leavers: record when each player was first seen gone.
			var connectedIndexes = ConnectedClientIndexes(server).ToHashSet();
			foreach (var p in players)
			{
				if (!connectedIndexes.Contains(p.Info.ClientIndex) && !leftGameAt.ContainsKey(p.Ranked.UserId))
				{
					leftGameAt[p.Ranked.UserId] = now;
					Log.Write("server", $"[ranked] {p.Ranked.Name} left the game.");
				}
			}

			var stillHere = players.Where(p => connectedIndexes.Contains(p.Info.ClientIndex)).ToList();
			if (stillHere.Count == 0 && players.Count == 2)
			{
				// Both gone: whoever left first loses. Same observation (within 0.5 s, e.g. a crash) = no result.
				var ordered = players.OrderBy(p => leftGameAt[p.Ranked.UserId]).ToList();
				if (leftGameAt[ordered[0].Ranked.UserId] < leftGameAt[ordered[1].Ranked.UserId])
					Finish("result", new { winnerUserId = ordered[1].Ranked.UserId, reason = "opponent_left_first" });
				else
					Finish("abort", new { reason = "all_players_disconnected" });

				return;
			}

			if (stillHere.Count == 0)
			{
				Finish("abort", new { reason = "all_players_disconnected" });
				return;
			}

			if (stillHere.Count == 1 && players.Count == 2)
			{
				var gone = players.First(p => p.Ranked != stillHere[0].Ranked).Ranked;
				if (now - leftGameAt[gone.UserId] >= ForfeitSettle)
				{
					server.SendMessage($"{gone.Name} left the game. {stillHere[0].Ranked.Name} wins.");
					Finish("result", new { winnerUserId = stillHere[0].Ranked.UserId, reason = "opponent_left" });
				}
			}
		}

		static IEnumerable<int> ConnectedClientIndexes(S server) =>
			server.Conns.Where(c => c.Validated).Select(c => c.PlayerIndex);

		public void ServerEmpty(S server)
		{
			if (!Active)
				return;

			// The last player just left: decide by game state first, then by who left first.
			if (!finished && gameStarted)
				CheckOutcome(server, DateTime.UtcNow);

			if (!finished)
				Finish("abort", new { reason = gameStarted ? "all_players_disconnected" : "all_players_left_lobby" });

			// One match per process. Make sure the backend heard about it, then exit
			// (otherwise the dedicated server loop would open a fresh, unmanaged lobby).
			lock (pendingReports)
				Task.WaitAll(pendingReports.ToArray(), TimeSpan.FromSeconds(15));

			Log.Write("server", "[ranked] Match over and server empty, exiting.");
			Exit(server, 0);
		}

		// Server.EndGame (normally run when the server loop shuts down) invokes this; remember it so we don't end twice.
		public void GameEnded(S server)
		{
			gameEnded = true;
		}

		/// <summary>
		/// Every process exit goes through here. The replay recorder only writes its metadata
		/// (players, outcomes, end time) and closes the file in Server.EndGame, so a started game
		/// must be ended before exiting or the stored replay is incomplete.
		/// Called on the server thread (ServerStarted / DropClient -> ServerEmpty), which is the thread
		/// that also feeds the recorder, so this does not race with frame recording.
		/// </summary>
		void Exit(S server, int code)
		{
			if (gameStarted && !gameEnded)
			{
				gameEnded = true;
				try
				{
					server.EndGame();
					Log.Write("server", "[ranked] Game ended, replay finalized.");
				}
				catch (Exception e)
				{
					Log.Write("server", $"[ranked] Ending the game failed: {e.Message}");
				}
			}

			// Log writes are asynchronous; flush them so the last lines survive Environment.Exit.
			try
			{
				Log.Dispose();
			}
			catch (Exception)
			{
				// Exiting anyway.
			}

			Environment.Exit(code);
		}

		void Finish(string endpoint, object body)
		{
			if (finished)
				return;

			finished = true;
			Report(endpoint, body);
		}

		void Report(string endpoint, object body)
		{
			var json = JsonSerializer.Serialize(body);
			Log.Write("server", $"[ranked] -> {endpoint} {json}");
			var task = Task.Run(async () =>
			{
				for (var attempt = 1; attempt <= 5; attempt++)
				{
					try
					{
						var req = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/server/matches/{matchId}/{endpoint}")
						{
							Content = new StringContent(json, Encoding.UTF8, "application/json")
						};

						req.Headers.Add("X-Server-Secret", serverSecret);
						var res = await http.SendAsync(req);
						if (res.IsSuccessStatusCode || (int)res.StatusCode == 409)
							return;

						Log.Write("server", $"[ranked] {endpoint} failed: {(int)res.StatusCode}");
					}
					catch (Exception e)
					{
						Log.Write("server", $"[ranked] {endpoint} failed: {e.Message}");
					}

					await Task.Delay(1000 * attempt);
				}
			});

			lock (pendingReports)
				pendingReports.Add(task);
		}

		static string Sha256Hex(string value) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

		static bool FixedTimeEquals(string a, string b) =>
			CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));
	}
}
