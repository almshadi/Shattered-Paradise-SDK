// Public ranked page: live queue stats, season selector + leaderboard, download link.
(function () {
	"use strict";

	var api = SP.api, esc = SP.esc, num = SP.num;
	var STATS_INTERVAL = 15000;
	var LEADERBOARD_INTERVAL = 60000;

	var $ = function (id) { return document.getElementById(id); };
	var statsTimer = null, lbTimer = null, lastStatsAt = null;
	var lbRequest = 0;

	// ---------- config / download ----------
	async function loadConfig() {
		var res = await api("/config");
		var versionEl = $("required-version");
		if (!res.ok || !res.data) {
			versionEl.textContent = "unknown";
			return;
		}

		var c = res.data;
		if (c.clientDownloadUrl && /^https?:\/\//i.test(c.clientDownloadUrl))
			$("download").href = c.clientDownloadUrl;

		versionEl.textContent = c.requiredClientVersion || "any";
		$("footer-version").textContent = c.requiredClientVersion ? "Ranked build " + c.requiredClientVersion : "";

		if (c.discordConfigured)
			document.querySelectorAll(".discord-only").forEach(function (el) { el.classList.remove("hidden"); });
	}

	// ---------- live stats ----------
	function setLive(state, text) {
		var el = $("live");
		el.classList.toggle("on", state === "on");
		el.classList.toggle("err", state === "err");
		$("live-text").textContent = text;
	}

	function setStat(id, value) {
		var el = $(id);
		var text = value === null || value === undefined || !isFinite(Number(value)) ? "—" : num(value);
		if (el.textContent !== text) el.textContent = text;
	}

	async function loadStats() {
		var res = await api("/queue/stats");
		if (res.ok && res.data) {
			var s = res.data;
			setStat("stat-searching", s.searching);
			setStat("stat-playing", s.playing);
			setStat("stat-matches", s.matchesInProgress);
			$("stats-error").classList.add("hidden");
			lastStatsAt = Date.now();
			setLive("on", "Live");
			updateStatsAge();
		} else {
			$("stats-error").classList.remove("hidden");
			$("stats-error").textContent = res.status === 404
				? "Live queue stats are not available on this server yet."
				: "Live stats are unavailable right now — retrying.";
			setLive(res.status === 0 ? "err" : "", res.status === 0 ? "Offline" : "Stats unavailable");
		}
	}

	function updateStatsAge() {
		if (!lastStatsAt) return;
		var s = Math.round((Date.now() - lastStatsAt) / 1000);
		$("stats-updated").textContent = s < 5 ? "updated just now" : "updated " + s + "s ago";
	}

	// ---------- seasons ----------
	async function loadSeasons() {
		var res = await api("/seasons");
		if (!res.ok || !Array.isArray(res.data) || res.data.length === 0)
			return; // no season system → selector stays hidden, leaderboard shows the current ladder

		var seasons = res.data.slice().sort(function (a, b) {
			if (!!b.isActive !== !!a.isActive) return b.isActive ? 1 : -1;
			return new Date(b.startedAt || 0) - new Date(a.startedAt || 0);
		});

		var select = $("season");
		select.innerHTML = "";
		var hasActive = seasons.some(function (s) { return s.isActive; });
		if (!hasActive) {
			var cur = document.createElement("option");
			cur.value = "";
			cur.textContent = "Current ladder";
			select.appendChild(cur);
		}

		seasons.forEach(function (s) {
			var o = document.createElement("option");
			o.value = s.id;
			o.textContent = (s.name || ("Season " + s.id)) + (s.isActive ? " (current)" : "");
			select.appendChild(o);
		});

		var fromHash = /season=(\d+)/.exec(location.hash);
		if (fromHash && seasons.some(function (s) { return String(s.id) === fromHash[1]; }))
			select.value = fromHash[1];

		$("season-wrap").classList.remove("hidden");
		select.addEventListener("change", function () {
			history.replaceState(null, "", select.value ? "#season=" + select.value : location.pathname);
			loadLeaderboard(true);
		});
	}

	// ---------- leaderboard ----------
	function showState(kind, title, text) {
		$("lb-state").innerHTML = kind
			? '<div class="state ' + kind + '"><strong>' + esc(title) + "</strong>" + esc(text) + "</div>"
			: "";
	}

	function rowHtml(r) {
		var rank = Number(r.rank) || 0;
		var cls = [];
		if (rank === 1) cls.push("top1");
		else if (rank === 2) cls.push("top2");
		else if (rank === 3) cls.push("top3");
		if (r.isYou) cls.push("you");

		var wins = Number(r.wins) || 0, losses = Number(r.losses) || 0;
		var games = Number(r.games) || wins + losses;
		var pct = SP.winPct(wins, games);
		var pctHtml = pct === null ? '<span class="meta">—</span>'
			: '<span class="pct"><span class="bar"><i style="width:' + pct + '%"></i></span>' + pct + "%</span>";

		return '<tr class="' + cls.join(" ") + '">'
			+ '<td class="rank">' + (rank || "—") + "</td>"
			+ '<td class="player">' + esc(r.name || "Unknown") + (r.isYou ? '<span class="badge green">you</span>' : "") + "</td>"
			+ '<td class="num rating">' + num(Math.round(Number(r.mmr))) + "</td>"
			+ '<td class="num wl"><span class="w">' + wins + '</span><span class="sep">/</span><span class="l">' + losses + "</span></td>"
			+ '<td class="num">' + pctHtml + "</td>"
			+ '<td class="num hide-sm">' + games + "</td>"
			+ "</tr>";
	}

	async function loadLeaderboard(showSpinner) {
		var id = ++lbRequest;
		var season = $("season").value;
		var url = "/leaderboard?limit=100" + (season ? "&season=" + encodeURIComponent(season) : "");
		var body = $("lb-body");

		if (showSpinner) {
			body.innerHTML = new Array(4).join('<tr class="skeleton-row"><td></td><td></td><td></td><td></td><td></td><td class="hide-sm"></td></tr>');
			showState("", "", "");
		}

		var res = await api(url);
		if (id !== lbRequest) return; // a newer request (season change) superseded this one

		if (!res.ok || !res.data) {
			body.innerHTML = "";
			$("lb-total").textContent = "";
			if (res.status === 404 && season)
				showState("error", "Season not found", "This season doesn't exist (anymore). Pick another one above.");
			else
				showState("error", "Leaderboard unavailable", res.status === 0
					? "Can't reach the ranked server. We'll keep trying."
					: "The server returned an error (" + res.status + "). Please try again later.");
			return;
		}

		var rows = Array.isArray(res.data.rows) ? res.data.rows : [];
		var total = Number(res.data.total);
		$("lb-total").textContent = isFinite(total) && total > 0 ? total + (total === 1 ? " ranked player" : " ranked players") : "";

		if (rows.length === 0) {
			body.innerHTML = "";
			showState("empty", "No ranked games yet", season
				? "Nobody has finished a ranked game in this season."
				: "Be the first on the ladder — download the game and hit Find match.");
			return;
		}

		body.innerHTML = rows.map(rowHtml).join("");
		var me = res.data.me;
		var meVisible = rows.some(function (r) { return r.isYou; });
		if (me && !meVisible) {
			// During placement the server sends rank: null, placed: false and placementGamesLeft.
			var left = Number(me.placementGamesLeft) || 0;
			$("lb-state").innerHTML = me.rank !== null && me.rank !== undefined
				? '<div class="me-row">Your rank: <b>#' + esc(me.rank) + "</b> · " + num(Math.round(me.mmr)) + " rating</div>"
				: '<div class="me-row">Placement: <b>' + left + "</b> more game" + (left === 1 ? "" : "s") + " until you appear on the leaderboard</div>";
		}
		else
			showState("", "", "");
	}

	// ---------- timers ----------
	function start() {
		stop();
		loadStats();
		statsTimer = setInterval(function () { loadStats(); }, STATS_INTERVAL);
		lbTimer = setInterval(function () { loadLeaderboard(false); }, LEADERBOARD_INTERVAL);
	}

	function stop() {
		clearInterval(statsTimer);
		clearInterval(lbTimer);
	}

	document.addEventListener("visibilitychange", function () {
		if (document.hidden) stop();
		else { start(); loadLeaderboard(false); }
	});

	setInterval(updateStatsAge, 1000);

	loadConfig();
	loadSeasons().then(function () { loadLeaderboard(true); });
	start();
})();
