# Shattered Paradise – Ranked matchmaking (prototype)

```
Game client (Ranked menu)  ──HTTP──▶  Backend (ASP.NET Core + Postgres)  ──spawns──▶  Dedicated server per match
  login / queue / join                  users, queue, matches, ratings                  RankedMatchServer trait
                         ◀──────────────────────────────────────────────── result ──  (who won, forfeits, aborts)
```

| Piece | Where |
|---|---|
| Backend API, matchmaker, game-server spawner | `ranked/backend/` |
| Server-side match control (auth, lobby lock, result) | `OpenRA.Mods.Sp/Ranked/RankedMatchServer.cs` |
| Client UI + API client | `OpenRA.Mods.Sp/Ranked/RankedLogic.cs`, `RankedApi.cs`, `mods/sp/chrome/ranked.yaml` |
| Backend URL used by the game | `mods/sp/mod.yaml` → `RankedSettings: BackendUrl` (env `SP_RANKED_BACKEND_URL` overrides) |

## Run locally

```bash
cp ranked/.env.example ranked/.env     # then set Ranked__ServerSecret to a long random string
make                                   # builds engine + mod (game servers use engine/bin)
ranked/run-backend.sh                  # starts Postgres (docker, port 5442) + backend on :8090
```

The game servers need the .NET 6 runtime (`dotnet --list-runtimes`).

## Switches (`ranked/.env`)

| Setting | Values | Effect |
|---|---|---|
| `Ranked__Matchmaking__Mode` | `fifo` / `mmr` | `fifo` pairs the first two players in the queue. `mmr` pairs by rating, and the allowed rating gap grows the longer someone waits. |
| `Ranked__Matchmaking__RatingUpdates` | `true` / `false` | `false` = matches and results are recorded, but ratings never change (test mode) |
| `Ranked__Auth__DevLogin` | `true` / `false` | Log in as a named test user without Google. **Never enable on a public server.** |
| `Ranked__RequiredClientVersion` | e.g. `sp-ranked-0.1` | Reject clients with a different version (set the mod version with `make version VERSION=...`) |

Restart the backend after changing settings. Ratings use Elo (K=32) for now; the rating calculation is a single function (`RatingSystem` in `Matchmaker.cs`), so swapping in Glicko-2 later won't affect the rest of the code.

## Google sign-in setup

1. https://console.cloud.google.com → create a project (e.g. "SP Ranked").
2. **APIs & Services → OAuth consent screen**: user type *External*, publishing status *Testing*.
   Scopes: `openid`, `email`, `profile`. Add every tester's Gmail address under **Test users**.
3. **APIs & Services → Credentials → Create credentials → OAuth client ID** → type *Web application*.
   Authorized redirect URIs:
   - `http://localhost:8090/auth/google/callback` (local)
   - `https://<your-backend-domain>/auth/google/callback` (hosted)
4. Put the client ID and secret into `ranked/.env` (`Ranked__Auth__GoogleClientId`, `Ranked__Auth__GoogleClientSecret`).
   When hosted, also set `Ranked__PublicBaseUrl=https://<your-backend-domain>`.

Login flow: the game asks the backend to start a login → opens the browser → Google → backend callback verifies the ID token and creates the user → the game picks up our own session token by polling. Google tokens never reach the game; the repo contains no secrets.

## Testing on one PC

- `Ranked__Auth__DevLogin=true`, start the backend.
- Start the game twice: `play.cmd` and `play-test2.cmd` (the second keeps a separate ranked login).
- In each: **Ranked → Dev login** with different names → **Find match** → both join the lobby automatically → pick a faction → **Ready** → the game starts.
- Surrender in one client; the other wins. Check `GET http://localhost:8090/leaderboard`.

## How a match plays out

1. Two queued players get paired. The backend starts a dedicated server with a random password, a map from `MapPool`, and the match id.
2. The clients poll `/queue/status`, receive host/port/password plus a personal ticket, connect, and send `ranked-auth <userId> <ticket>`.
3. The server compares the ticket against the backend's hash. Unknown connections are kicked after 20s.
   Players can only change their own faction/color/spawn and toggle Ready; map, options, slots, teams and kicks are locked.
4. Both players ready → the game starts. The server records the result from the game state that both clients must agree on, so a player can't fake a win.
   - Opponent disconnects for 30s → the remaining player wins.
   - Both disconnect → the match is voided.
   - Desync → no result, and the match is voided when the players leave.
5. The server reports the result → the backend updates ratings (if enabled) → the server exits once both players have left.

Timeouts: the queue drops a player who stops polling (30s); a match is cancelled if the game hasn't started within 5 min; games longer than 3 h are voided.

## Hosting (for testing with someone else)

Both players must reach the backend (HTTPS) and the game server ports (`12340-12360/tcp`). Easiest is a small Linux VPS:
clone the fork, `make`, install the .NET 6 runtime + Docker, run `ranked/run-backend.sh` behind a reverse proxy with TLS, and set:

```
Ranked__PublicBaseUrl=https://ranked.example.com
Ranked__GameServers__PublicHost=<vps public ip or hostname>
Ranked__Auth__DevLogin=false
```

Then set `RankedSettings: BackendUrl` in `mods/sp/mod.yaml` to the public URL and build the client release.

## Known limitations (prototype)

- Schema is created with `EnsureCreated`; on schema changes drop the DB volume (`docker compose down -v`). Switch to EF migrations before keeping real data.
- One backend instance (in-process lock). Game servers run on the backend host.
- Reading the server's win/loss record uses reflection on `Server.gameInfo` (private). If the engine is updated, check that `RankedMatchServer` still works.
- Map hacks / client-side cheats are not addressed.
