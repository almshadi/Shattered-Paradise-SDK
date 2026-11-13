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
| `Ranked__RequiredClientVersion` | empty (default) / e.g. `ranked-0.1.0` | Clients must send exactly this version (`X-Client-Version`) to queue; others get HTTP 409 `version_mismatch`. Empty = automatic: the `Version` from `mods/sp/mod.yaml` of the SDK checkout the game servers run from (re-read when the file changes). Set the mod version with `make version VERSION=...` |
| `Ranked__ClientDownloadUrl` | URL (default: GitHub latest release) | Where outdated clients are sent to download the matching build (returned in `/config` and in `version_mismatch` errors) |

Restart the backend after changing settings. Ratings use Glicko-2, see [Rating system](#rating-system).

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

## Queue rules

Options live in `Ranked:Queue` (env `Ranked__Queue__...`).

- **Dodge penalty.** Leaving a match before it started (`POST /matches/{id}/leave` while Starting/WaitingForPlayers), or never
  connecting to the game server before the start timeout, is a dodge. Each dodge blocks the queue for
  `DodgePenaltyMinutes[n-1]` (default `[2, 5, 15]`, the last value repeats); the counter resets when the previous dodge is
  older than `DodgeResetHours` (24). Only the dodger is penalised; the opponent is not re-queued automatically.
  While blocked, `POST /queue` → 409 `{"error":"queue_cooldown","until":iso,"seconds":int,"dodges":int}`.
- **Bans.** A user with `BannedUntil` in the future gets 409 `{"error":"banned","until":iso|null,"reason":string|null}`
  (`until` is null for permanent bans) and is removed from the queue. Check order: client version → ban → cooldown.
- **Map vetoes.** `POST /queue {"faction","color","vetoes":[mapUid,...]}`: up to `MaxMapVetoes` (2) maps from the pool;
  unknown uids and duplicates are ignored. The map is picked at random from the pool minus both players' vetoes (full pool
  if nothing is left). `/config` returns `mapPool` and `maxMapVetoes`.
- **Queue visibility.** `GET /queue/stats` (public) → `{"searching":int,"playing":int,"matchesInProgress":int}`.

## Hosting (live: ranked.tabletstrats.com)

The live stack runs on the tabletstrats.com server, next to other sites, isolated in its own Docker project:

| Piece | Where |
|---|---|
| Compose project | `/opt/sp-ranked` (`docker-compose.yml` = `ranked/deploy/docker-compose.prod.yml`, secrets in `.env`, chmod 600) |
| Backend + game servers | container `sp-ranked-backend-1`, host network, API on `127.0.0.1:8090`, `mem_limit 900m` |
| Database | container `sp-ranked-db-1`, Postgres 16, `127.0.0.1:5443` only, `mem_limit 256m`, volume `sp-ranked_db` |
| Reverse proxy / TLS | nginx site `/etc/nginx/sites-available/sp-ranked` (certbot-managed Let's Encrypt cert) |
| Firewall | `ufw allow 12340:12342/tcp` (game servers; max 3 concurrent matches on this small box) |

**Deploy a new build** (after `make` / a release, from this checkout):

```bash
ranked/deploy/deploy.sh          # builds image sp-ranked:<mod version>, uploads via ssh, restarts
```

The image bundles the backend, the .NET 6 runtime and the game files; the backend requires exactly the image's mod version.

**First-time setup on a new server** (what was done for tabletstrats.com):
1. DNS A record `ranked.<domain>` → server IP.
2. `apt-get install docker.io docker-compose-v2`, `mkdir /opt/sp-ranked`, create `.env` from `ranked/deploy/env.prod.example` (random `POSTGRES_PASSWORD`/`ServerSecret`, Google credentials).
3. `ranked/deploy/deploy.sh`.
4. nginx: copy `ranked/deploy/nginx-ranked.conf` to `sites-available/sp-ranked`, symlink to `sites-enabled`, `nginx -t && systemctl reload nginx`, then `certbot --nginx -d ranked.<domain> --redirect`.
5. `ufw allow 12340:12342/tcp`.
6. Google Cloud: add `https://ranked.<domain>/auth/google/callback` as redirect URI.
7. `RankedSettings: BackendUrl` in `mods/sp/mod.yaml` → `https://ranked.<domain>`, then release a client build.

**Rollback / removal:** `cd /opt/sp-ranked && docker compose down` (add `-v` to delete the database), remove `/etc/nginx/sites-enabled/sp-ranked` + reload nginx, `ufw delete allow 12340:12342/tcp`.

**Local development** against a backend on this machine: start the game with `SP_RANKED_BACKEND_URL=http://localhost:8090`.

## Known limitations (prototype)

- Schema is managed with EF Core migrations (`backend/Data/Migrations`), applied automatically on startup. Databases created by the old `EnsureCreated` prototype are baselined at `0001_Initial` first (`Data/DatabaseMigrator.cs`).
  Schema change: edit `Data/RankedDb.cs`, then from `backend/` run `dotnet ef migrations add <Name> -o Data/Migrations` (tool: `dotnet tool install --global dotnet-ef --version 8.0.10`); `dotnet ef migrations script <From> <To>` shows the SQL.
- One backend instance (in-process lock). Game servers run on the backend host.
- Reading the server's win/loss record uses reflection on `Server.gameInfo` (private). If the engine is updated, check that `RankedMatchServer` still works.
- Map hacks / client-side cheats are not addressed.

## Releasing a new version

`ranked/release.sh` builds the Windows package and publishes it on the fork:

```
ranked/release.sh ranked-0.1.0 --dry-run   # set version, build, zip; then restore mod.yaml
ranked/release.sh ranked-0.1.0             # same + commit "Release ...", tag, push to `fork`, GitHub release
```

- The version must look like `ranked-X.Y.Z` and is also the git tag. The working tree must have no uncommitted changes to tracked files.
- It runs `make version` (keeping `mods/sp/mod.yaml` CRLF) and `make TARGETPLATFORM=win-x64` (log in `dist/build-<version>.log`).
- Output: `dist/ShatteredParadise-<version>-win64.zip` with a `ShatteredParadise-<version>/` folder: the game tree plus `play.cmd` and `README.txt` from `ranked/launcher/` (the player needs the .NET 6 Desktop Runtime; `play.cmd` checks for it).
- With the GitHub CLI (`gh`) installed the release is created automatically; otherwise the script prints the manual upload steps (https://github.com/almshadi/Shattered-Paradise-sdk/releases/new).
- Both players' clients and the game servers must run the same version.

## Admin

A user is an admin if `Users.IsAdmin` is set **or** their id / Google email is listed in the `Ranked:Admin` config
(comma-separated, emails case-insensitive). Use the config to bootstrap the first admin without touching the database:

```
Ranked__Admin__AdminEmails=you@gmail.com        # in ranked/.env or the server .env
Ranked__Admin__AdminUserIds=<user guid>,<guid>  # e.g. for dev-login users (no email)
```

That admin can then grant/revoke `IsAdmin` for others (`POST /admin/users/{id}/admin`). Config admins stay admins regardless of the flag.

All endpoints take `Authorization: Bearer <token>`; non-admins get `403 {"error":"forbidden"}`. Every mutating call is recorded in `AdminActions`.

| Endpoint | Purpose |
|---|---|
| `GET /admin/me` | `{isAdmin, name}` for any signed-in user |
| `GET /admin/users?search=&limit=50` | search by name / email / Discord id / user id |
| `POST /admin/users/{id}/ban` `{"minutes":int\|null,"reason":""}` | `null` = permanent (until 9999-12-31); also removes them from the queue |
| `POST /admin/users/{id}/unban` | |
| `POST /admin/users/{id}/rating` `{"mmr":0..5000,"deviation":30..350\|null,"reason":""}` | set rating |
| `POST /admin/users/{id}/admin` `{"isAdmin":bool}` | grant / revoke admin (not on yourself) |
| `GET /admin/matches?status=&userId=&limit=50` | status = a match status or `active` |
| `POST /admin/matches/{id}/void` `{"reason":""}` | active match: closed as Void; completed match: rating/stat changes reverted, then Void |
| `GET /admin/seasons`, `POST /admin/seasons/start` `{"name":""}`, `POST /admin/seasons/end` | season control (501 while seasons are not implemented) |
| `GET /admin/actions?limit=100` | audit log, newest first |

## Rating system

**Glicko-2** (Glickman 2012), implemented in `ranked/backend/Rating/Glicko2.cs`; one match = one rating period. Each player has a rating (`mmr`, starts at 1500), a rating deviation (`deviation`, uncertainty, starts at 350) and a volatility (0.06). New players' ratings move a lot; as the deviation shrinks, changes get smaller. Inactivity: before each rated game the deviation grows with the days since the player's last rated game (`phi* = sqrt(phi² + σ²·days/7)`, capped at 350), so returning players re-calibrate faster. With `RatingUpdates=false` (test mode) matches are recorded in the history, but neither ratings nor games/wins/losses/placement progress change.

**Placement:** a player appears on `/leaderboard` once they have played `PlacementGames` (default 5) games in the current season and are not banned. `/me` returns `placed`, `placementGamesLeft` and `deviation`; the leaderboard's `me` row is present during placement too (`rank: null, placed: false`).

**Seasons** (`Rating/SeasonService.cs`, started/ended by admins): starting a new season archives the final standings of the active one (placed, non-banned players by rating → `SeasonStandings`), ends it, and soft-resets everyone: `mmr = 1500 + (mmr − 1500) · 0.5`, `deviation = max(deviation, 200)`, games/wins/losses = 0 (everyone plays placements again). `GET /seasons` lists all seasons; `GET /leaderboard?season={id}` returns the archived standings of a past season (default: current season, `season` field in the response).

| Setting (`Ranked__Rating__*`) | Default | Effect |
|---|---|---|
| `Tau` | `0.5` | Glicko-2 system constant (how fast volatility changes) |
| `PlacementGames` | `5` | Games before a player is listed on the leaderboard |
| `InitialDeviation` / `InitialVolatility` | `350` / `0.06` | Starting values for new players (start rating: `Ranked__Matchmaking__InitialRating`) |
| `InactivityPeriodDays` | `7` | Days of inactivity that count as one rating period of deviation growth |
| `SoftResetFactor` / `SoftResetMinDeviation` | `0.5` / `200` | Season soft reset |

Tests: `cd ranked/backend.Tests && dotnet test` (reproduces the example from Glickman's paper).

## Match history & replays

- `GET /matches/history?limit=20&before=<iso>` (auth): the player's matches, newest first (`limit` 1–100, page with `before` = `createdAt` of the last entry). Each entry: `id, createdAt, startedAt, endedAt, durationSeconds, status, reason, rated, mapUid, queueType, me {faction, result, mmrBefore, mmrAfter, placement}, opponents [{name, faction, result, team}], hasReplay`. Opponent factions are `null` until the game started. `reason` is the raw status reason (the client maps it to text).
- `GET /matches/{id}/replay`: the recorded `.orarep` (e.g. `2026-11-13_Alice-vs-Bob.orarep`), 404 if there is none. `GET /matches/{id}` has `hasReplay`.
- Game servers run with `Server.RecordReplays=True` and their own support dir `ranked/.server-support/<matchId>`; the engine writes `<supportDir>/Replays/sp/<mod version>/sp-Server-<timestamp>.orarep` once a game actually starts. When the server process exits, `GameServerManager` moves the newest `.orarep` into the replay store as `<matchId>.orarep`, sets `Match.ReplayFile` and deletes the support dir.
- Replay store: `Ranked:Replays:Directory` (default `ranked/.replays`; production: docker volume `replays` at `/app/replays`). Files older than `Ranked:Replays:RetentionDays` (default 30, `0` = keep forever) are deleted daily and their matches lose `hasReplay`.

## Web pages & Discord

| URL | What |
|---|---|
| `/` | Public page: live queue stats (`/queue/stats`, refreshed every 15 s), season selector + leaderboard (`/seasons`, `/leaderboard?season=`), download button and required version (`/config`), "How to play ranked". |
| `/admin.html` | Admin page. Sign in with Google (or Discord, or dev login when enabled), then access is checked with `/admin/me`. Tabs: Users (search, ban/unban, set rating, admin on/off), Matches (filter, void), Seasons (start/end), Audit log. The token is kept in the browser's `localStorage`; *Sign out* revokes it. |

The pages are static files in `backend/wwwroot/` (plain HTML/CSS/JS, no build step). `dotnet publish` and the Docker image include them automatically.

### Discord options (`Ranked__Discord__*`)

| Setting | Default | Effect |
|---|---|---|
| `WebhookUrl` | empty | Channel webhook for announcements. Empty = no Discord posts. |
| `PostQueueJoins` | `true` | Post "Alice is looking for a ranked match (2 searching)". At most one post per player every `QueueJoinThrottleMinutes` (default 10). |
| `PostMatchResults` | `true` | Post "Alice defeated Bob on `<map>` · +16 / −16 · GDI vs Nod" when a match completes. |
| `Username`, `AvatarUrl` | "Shattered Paradise Ranked", empty | Name/avatar shown on the webhook posts. |
| `ClientId`, `ClientSecret` | empty | Discord OAuth app for the optional *Sign in with Discord*. Without them, `/auth/discord/*` shows "Discord sign-in is not configured". |

Posts run in the background. If Discord is down or the webhook is wrong, this only shows up as a warning in the backend log; matchmaking is unaffected.

**Create the webhook:** Discord → Server Settings → Integrations → Webhooks → *New Webhook* → pick the channel → *Copy Webhook URL* → `Ranked__Discord__WebhookUrl=<url>` in `.env`. Treat the URL as a secret.

**Discord sign-in:**
1. https://discord.com/developers/applications → *New Application* (e.g. "SP Ranked").
2. **OAuth2** → copy the *Client ID*, reset/copy the *Client Secret*.
3. **OAuth2 → Redirects**: add `https://<host>/auth/discord/callback` (and `http://localhost:8090/auth/discord/callback` for local testing). The redirect is built from `Ranked__PublicBaseUrl`.
4. Set `Ranked__Discord__ClientId` and `Ranked__Discord__ClientSecret`, then restart.

Only the `identify` scope is requested, and only the Discord account id is stored (`Users.DiscordId`). As with Google, new players choose a nickname after their first login. `POST /auth/login` returns `discordConfigured` and `discordBrowserUrl` (open that instead of `browserUrl`), and the client keeps polling `/auth/login/{id}/poll` the same way. `/config` also reports `discordConfigured`.
