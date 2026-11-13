// Admin page: sign-in through the backend's Google/Discord login flow, then users / matches / seasons / audit log.
(function () {
	"use strict";

	var esc = SP.esc, num = SP.num;
	var TOKEN_KEY = "spRankedAdminToken";
	var $ = function (id) { return document.getElementById(id); };

	var token = null;
	var me = null;
	var config = {};
	var pollTimer = null;
	var loaded = {};

	try { token = localStorage.getItem(TOKEN_KEY); } catch (e) { token = null; }

	function saveToken(t) {
		token = t;
		try {
			if (t) localStorage.setItem(TOKEN_KEY, t);
			else localStorage.removeItem(TOKEN_KEY);
		} catch (e) { /* private mode: token lives for this page only */ }
	}

	// Authenticated API call. 401 → back to sign-in.
	async function call(path, method, body) {
		var res = await SP.api(path, { method: method, body: body, token: token });
		if (res.status === 401) {
			saveToken(null);
			showSignin("Your session expired. Please sign in again.");
		}
		return res;
	}

	// ---------- UI helpers ----------
	function show(view) {
		["view-signin", "view-denied", "view-admin"].forEach(function (id) {
			$(id).classList.toggle("hidden", id !== view);
		});
	}

	function toast(text, isError) {
		var el = document.createElement("div");
		el.className = "toast" + (isError ? " err" : "");
		el.textContent = text;
		$("toasts").appendChild(el);
		setTimeout(function () { el.remove(); }, isError ? 7000 : 3500);
	}

	function stateHtml(kind, title, text) {
		return '<div class="state ' + kind + '"><strong>' + esc(title) + "</strong>" + esc(text || "") + "</div>";
	}

	function apiErrorState(res, what) {
		if (res.status === 403) return stateHtml("error", "Not allowed", "Your account is not allowed to view " + what + ".");
		if (res.status === 404 || res.status === 501 || res.status === 405)
			return stateHtml("error", "Not available", "This server does not support " + what + " yet (HTTP " + res.status + ").");
		return stateHtml("error", "Could not load " + what, SP.errorText(res));
	}

	function actionResult(res, okText) {
		if (res.ok) {
			toast(okText);
			return true;
		}
		if (res.status === 403) toast("Not allowed (HTTP 403).", true);
		else if (res.status === 404 || res.status === 501) toast("Not supported by this server (HTTP " + res.status + ").", true);
		else if (res.status !== 401) toast("Failed: " + SP.errorText(res), true);
		return false;
	}

	// Generic modal form. fields: [{ name, label, type, value, required, options:[[value,label]], placeholder, min, max, step, showIf }]
	// Resolves to an object of values, or null when cancelled.
	function dialog(opts) {
		return new Promise(function (resolve) {
			var dlg = $("dlg"), form = $("dlg-form");
			var html = "<h3>" + esc(opts.title) + "</h3>";
			if (opts.text) html += "<p>" + esc(opts.text) + "</p>";
			(opts.fields || []).forEach(function (f) {
				var attrs = ' name="' + esc(f.name) + '" id="dlg-' + esc(f.name) + '"'
					+ (f.required ? " required" : "")
					+ (f.placeholder ? ' placeholder="' + esc(f.placeholder) + '"' : "")
					+ (f.min !== undefined ? ' min="' + esc(f.min) + '"' : "")
					+ (f.max !== undefined ? ' max="' + esc(f.max) + '"' : "")
					+ (f.step !== undefined ? ' step="' + esc(f.step) + '"' : "")
					+ (f.maxlength ? ' maxlength="' + esc(f.maxlength) + '"' : "");
				var input;
				if (f.type === "select") {
					input = "<select" + attrs + ">" + f.options.map(function (o) {
						return '<option value="' + esc(o[0]) + '"' + (String(o[0]) === String(f.value) ? " selected" : "") + ">" + esc(o[1]) + "</option>";
					}).join("") + "</select>";
				} else if (f.type === "textarea") {
					input = "<textarea rows=\"3\"" + attrs + ">" + esc(f.value || "") + "</textarea>";
				} else {
					input = '<input type="' + esc(f.type || "text") + '"' + attrs + ' value="' + esc(f.value === undefined || f.value === null ? "" : f.value) + '" autocomplete="off">';
				}
				html += '<label class="field" data-field="' + esc(f.name) + '">' + esc(f.label) + input + "</label>";
			});
			html += '<div class="row"><button class="btn ghost" value="cancel" type="button" id="dlg-cancel">Cancel</button>'
				+ '<button class="btn ' + (opts.danger ? "danger" : "primary") + '" value="ok" type="submit">' + esc(opts.confirm || "OK") + "</button></div>";
			form.innerHTML = html;

			function syncVisibility() {
				(opts.fields || []).forEach(function (f) {
					if (!f.showIf) return;
					var visible = f.showIf(readValues());
					var wrap = form.querySelector('[data-field="' + f.name + '"]');
					wrap.classList.toggle("hidden", !visible);
					var el = $("dlg-" + f.name);
					el.disabled = !visible;
				});
			}

			function readValues() {
				var v = {};
				(opts.fields || []).forEach(function (f) {
					var el = $("dlg-" + f.name);
					v[f.name] = el ? el.value.trim() : "";
				});
				return v;
			}

			var done = false;
			function finish(result) {
				if (done) return;
				done = true;
				form.onsubmit = null;
				form.oninput = null;
				dlg.onclose = null;
				if (dlg.open) dlg.close();
				resolve(result);
			}

			form.oninput = syncVisibility;
			form.onsubmit = function (e) {
				e.preventDefault();
				var values = readValues();
				if (opts.validate) {
					var problem = opts.validate(values);
					if (problem) { toast(problem, true); return; }
				}
				finish(values);
			};
			$("dlg-cancel").onclick = function () { finish(null); };
			dlg.onclose = function () { finish(null); };

			syncVisibility();
			if (typeof dlg.showModal === "function") dlg.showModal();
			else dlg.setAttribute("open", "");
			var first = form.querySelector("input:not([disabled]), select, textarea");
			if (first) first.focus();
		});
	}

	function confirmBox(title, text, confirmLabel) {
		return dialog({ title: title, text: text, confirm: confirmLabel || "Confirm", danger: true }).then(function (v) { return v !== null; });
	}

	// ---------- sign-in ----------
	function showSignin(message) {
		stopPolling();
		me = null;
		$("who").classList.add("hidden");
		show("view-signin");
		$("signin-status").textContent = message || "";
		$("signin-discord").classList.toggle("hidden", !config.discordConfigured);
		$("dev-wrap").classList.toggle("hidden", !config.devLogin);
		setSigninBusy(false);
	}

	function setSigninBusy(busy) {
		["signin-google", "signin-discord", "signin-dev"].forEach(function (id) { $(id).disabled = busy; });
	}

	function stopPolling() {
		if (pollTimer) clearTimeout(pollTimer);
		pollTimer = null;
	}

	async function startLogin(provider) {
		stopPolling();
		// Open the tab synchronously (inside the click) so popup blockers allow it; navigate once we know the URL.
		var tab = window.open("", "_blank");
		if (tab) {
			try { tab.opener = null; tab.document.title = "Signing in…"; tab.document.body.style.background = "#07090b"; } catch (e) { /* ignore */ }
		}

		setSigninBusy(true);
		$("signin-status").textContent = "Starting sign-in…";
		var res = await SP.api("/auth/login", { method: "POST" });
		if (!res.ok || !res.data || !res.data.loginId) {
			if (tab) tab.close();
			setSigninBusy(false);
			$("signin-status").textContent = "Could not start sign-in: " + SP.errorText(res);
			return;
		}

		var d = res.data;
		var url = provider === "discord" ? d.discordBrowserUrl : d.browserUrl;
		var configured = provider === "discord" ? d.discordConfigured : d.googleConfigured;
		if (!url || configured === false) {
			if (tab) tab.close();
			setSigninBusy(false);
			$("signin-status").textContent = (provider === "discord" ? "Discord" : "Google") + " sign-in is not configured on this server.";
			return;
		}

		if (tab) tab.location.href = url;
		else {
			$("signin-status").innerHTML = 'Your browser blocked the new tab. <a href="' + esc(url) + '" target="_blank" rel="noopener">Open the sign-in page</a> and come back here.';
		}

		if (tab) $("signin-status").textContent = "Finish signing in in the new tab… waiting.";
		var deadline = Date.now() + 10 * 60 * 1000;

		async function poll() {
			var p = await SP.api("/auth/login/" + encodeURIComponent(d.loginId) + "/poll", { method: "POST", body: { pollSecret: d.pollSecret } });
			var status = p.data && p.data.status;
			if (p.ok && status === "complete" && p.data.token) {
				saveToken(p.data.token);
				$("signin-status").textContent = "";
				setSigninBusy(false);
				gate();
				return;
			}
			if (p.ok && (status === "failed" || status === "expired" || status === "collected")) {
				setSigninBusy(false);
				$("signin-status").textContent = status === "failed"
					? "Sign-in failed" + (p.data.error ? " (" + p.data.error + ")" : "") + ". Please try again."
					: status === "expired" ? "The sign-in expired. Please try again." : "This sign-in was already used. Please try again.";
				return;
			}
			if (Date.now() > deadline) {
				setSigninBusy(false);
				$("signin-status").textContent = "Timed out waiting for sign-in. Please try again.";
				return;
			}
			pollTimer = setTimeout(poll, 2000);
		}

		pollTimer = setTimeout(poll, 2000);
	}

	async function devLogin() {
		var name = $("dev-name").value.trim();
		if (!name) { $("signin-status").textContent = "Enter a name."; return; }
		setSigninBusy(true);
		var res = await SP.api("/auth/dev-login", { method: "POST", body: { name: name } });
		setSigninBusy(false);
		if (res.ok && res.data && res.data.token) {
			saveToken(res.data.token);
			gate();
		} else {
			$("signin-status").textContent = "Dev login failed: " + SP.errorText(res);
		}
	}

	async function signOut() {
		if (token) await SP.api("/auth/logout", { method: "POST", token: token });
		saveToken(null);
		loaded = {};
		showSignin("Signed out.");
	}

	function denied(title, text) {
		$("denied-title").textContent = title;
		$("denied-text").textContent = text;
		show("view-denied");
	}

	async function gate() {
		if (!token) { showSignin(""); return; }

		var res = await SP.api("/admin/me", { token: token });
		if (res.status === 401) {
			saveToken(null);
			showSignin("Your session expired. Please sign in again.");
			return;
		}

		var name = res.data && res.data.name;
		if (!name) {
			var who = await SP.api("/me", { token: token });
			name = who.ok && who.data ? who.data.name : "";
		}
		$("who-name").textContent = name || "(no nickname)";
		$("who").classList.remove("hidden");

		if (res.status === 403 || (res.ok && res.data && res.data.isAdmin === false)) {
			denied("No admin access", "The account " + (name ? "“" + name + "” " : "") + "is not an admin. Sign out and sign in with an admin account.");
			return;
		}
		if (res.status === 404 || res.status === 501 || res.status === 405) {
			denied("Admin API not available", "This server does not provide the admin API yet (HTTP " + res.status + ").");
			return;
		}
		if (!res.ok) {
			denied("Can't check access", SP.errorText(res) + ". Reload the page to try again.");
			return;
		}

		me = res.data;
		show("view-admin");
		selectTab(currentTab());
	}

	// ---------- tabs ----------
	function currentTab() {
		var t = (location.hash || "").replace("#", "");
		return ["users", "matches", "seasons", "audit"].indexOf(t) >= 0 ? t : "users";
	}

	function selectTab(name) {
		document.querySelectorAll(".tab").forEach(function (b) { b.setAttribute("aria-selected", String(b.dataset.tab === name)); });
		document.querySelectorAll("[data-pane]").forEach(function (p) { p.classList.toggle("hidden", p.dataset.pane !== name); });
		if (location.hash !== "#" + name) history.replaceState(null, "", "#" + name);
		if (!loaded[name]) {
			loaded[name] = true;
			({ users: loadUsers, matches: loadMatches, seasons: loadSeasons, audit: loadAudit })[name]();
		}
	}

	// ---------- users ----------
	var usersById = {};

	function isBanned(u) {
		return u.bannedUntil && new Date(u.bannedUntil).getTime() > Date.now();
	}

	function banText(u) {
		var until = new Date(u.bannedUntil);
		var permanent = until.getTime() - Date.now() > 50 * 365 * 86400000; // far-future date = permanent ban
		return (permanent ? "Banned permanently" : "Banned until " + SP.fmtDate(u.bannedUntil, true)) + (u.banReason ? " — " + u.banReason : "");
	}

	function userRow(u) {
		var badges = (u.isAdmin ? '<span class="badge gold">admin</span>' : "") + (u.isDevUser ? '<span class="badge blue">dev</span>' : "");
		var wins = Number(u.wins) || 0, losses = Number(u.losses) || 0;
		var rating = u.mmr === null || u.mmr === undefined ? "—" : num(Math.round(u.mmr))
			+ (u.deviation !== null && u.deviation !== undefined ? '<span class="sub">± ' + num(Math.round(u.deviation)) + "</span>" : "");
		var status = isBanned(u)
			? '<span class="badge red">banned</span><span class="sub">' + esc(banText(u)) + "</span>"
			: '<span class="badge green">active</span>';
		var actions = (isBanned(u)
			? '<button class="btn small" data-act="unban" data-id="' + esc(u.id) + '" type="button">Unban</button>'
			: '<button class="btn small danger" data-act="ban" data-id="' + esc(u.id) + '" type="button">Ban</button>')
			+ '<button class="btn small" data-act="rating" data-id="' + esc(u.id) + '" type="button">Rating</button>'
			+ '<button class="btn small" data-act="admin" data-id="' + esc(u.id) + '" type="button">' + (u.isAdmin ? "Revoke admin" : "Make admin") + "</button>";

		return "<tr>"
			+ "<td><b>" + esc(u.name || "(no nickname)") + "</b>" + badges
			+ '<span class="sub">' + esc(u.email || "") + (u.email ? " · " : "") + '<span class="mono" title="' + esc(u.id) + '">' + esc(String(u.id || "").slice(0, 8)) + "</span>"
			+ (u.createdAt ? " · joined " + esc(SP.fmtDate(u.createdAt)) : "") + "</span></td>"
			+ '<td class="num">' + rating + "</td>"
			+ '<td class="num hide-sm wl"><span class="w">' + wins + '</span><span class="sep">/</span><span class="l">' + losses + "</span></td>"
			+ "<td>" + status + "</td>"
			+ '<td class="hide-sm" title="' + esc(SP.fmtDate(u.lastSeenAt, true)) + '">' + esc(SP.timeAgo(u.lastSeenAt)) + "</td>"
			+ '<td><div class="actions">' + actions + "</div></td>"
			+ "</tr>";
	}

	async function loadUsers() {
		var q = $("users-search").value.trim();
		var limit = $("users-limit").value;
		$("users-state").innerHTML = stateHtml("", "Loading…", "");
		var res = await call("/admin/users?search=" + encodeURIComponent(q) + "&limit=" + encodeURIComponent(limit));
		if (res.status === 401) return;
		if (!res.ok || !Array.isArray(res.data)) {
			$("users-body").innerHTML = "";
			$("users-state").innerHTML = apiErrorState(res, "users");
			return;
		}

		usersById = {};
		res.data.forEach(function (u) { usersById[u.id] = u; });
		$("users-body").innerHTML = res.data.map(userRow).join("");
		$("users-state").innerHTML = res.data.length ? "" : stateHtml("empty", "No users found", q ? "Nothing matches “" + q + "”." : "");
	}

	async function userAction(act, id) {
		var u = usersById[id];
		if (!u) return;
		var label = u.name || u.email || String(id).slice(0, 8);
		var res;

		if (act === "ban") {
			var v = await dialog({
				title: "Ban " + label,
				text: "Banned players cannot queue or play ranked games.",
				confirm: "Ban",
				danger: true,
				fields: [
					{ name: "duration", label: "Duration", type: "select", value: "1440", options: [
						["60", "1 hour"], ["1440", "1 day"], ["10080", "7 days"], ["43200", "30 days"], ["custom", "Custom (minutes)…"], ["permanent", "Permanent"]] },
					{ name: "minutes", label: "Minutes", type: "number", min: 1, step: 1, required: true, showIf: function (x) { return x.duration === "custom"; } },
					{ name: "reason", label: "Reason (shown to the player)", type: "textarea", required: true, maxlength: 500 }
				],
				validate: function (x) { return x.reason ? null : "A reason is required."; }
			});
			if (!v) return;
			var minutes = v.duration === "permanent" ? null : v.duration === "custom" ? parseInt(v.minutes, 10) : parseInt(v.duration, 10);
			if (minutes !== null && !(minutes > 0)) { toast("Invalid duration.", true); return; }
			res = await call("/admin/users/" + encodeURIComponent(id) + "/ban", "POST", { minutes: minutes, reason: v.reason });
			if (actionResult(res, label + " banned.")) loadUsers();
		} else if (act === "unban") {
			if (!await confirmBox("Unban " + label + "?", "The player can queue again immediately.", "Unban")) return;
			res = await call("/admin/users/" + encodeURIComponent(id) + "/unban", "POST", {});
			if (actionResult(res, label + " unbanned.")) loadUsers();
		} else if (act === "rating") {
			var r = await dialog({
				title: "Set rating — " + label,
				text: "Overrides the player's current rating. Use sparingly; it is logged.",
				confirm: "Set rating",
				danger: true,
				fields: [
					{ name: "mmr", label: "Rating", type: "number", step: 1, min: 0, max: 5000, required: true, value: u.mmr === null || u.mmr === undefined ? "" : Math.round(u.mmr) },
					{ name: "deviation", label: "Deviation (optional, 30–350)", type: "number", step: 1, min: 30, max: 350, value: "" },
					{ name: "reason", label: "Reason", type: "textarea", required: true, maxlength: 500 }
				],
				validate: function (x) {
					if (!isFinite(parseFloat(x.mmr))) return "Enter a rating.";
					if (x.deviation && !isFinite(parseFloat(x.deviation))) return "Deviation must be a number.";
					return x.reason ? null : "A reason is required.";
				}
			});
			if (!r) return;
			res = await call("/admin/users/" + encodeURIComponent(id) + "/rating", "POST", {
				mmr: parseFloat(r.mmr), deviation: r.deviation ? parseFloat(r.deviation) : null, reason: r.reason
			});
			if (actionResult(res, "Rating of " + label + " set to " + r.mmr + ".")) loadUsers();
		} else if (act === "admin") {
			var makeAdmin = !u.isAdmin;
			var self = me && me.name && u.name === me.name;
			if (!await confirmBox(makeAdmin ? "Make " + label + " an admin?" : "Revoke admin from " + label + "?",
				makeAdmin ? "Admins can ban players, change ratings, void matches and manage seasons."
					: self ? "This is your own account — you will lose access to this page." : "They will lose access to this page.",
				makeAdmin ? "Make admin" : "Revoke")) return;
			res = await call("/admin/users/" + encodeURIComponent(id) + "/admin", "POST", { isAdmin: makeAdmin });
			if (actionResult(res, makeAdmin ? label + " is now an admin." : label + " is no longer an admin.")) {
				if (self && !makeAdmin) gate();
				else loadUsers();
			}
		}
	}

	// ---------- matches ----------
	var matchesById = {};

	function delta(p) {
		if (p.mmrAfter === null || p.mmrAfter === undefined || p.mmrBefore === null || p.mmrBefore === undefined) return "";
		var d = Math.round(p.mmrAfter - p.mmrBefore);
		return ' <span class="' + (d >= 0 ? "res-Win" : "res-Loss") + '">' + (d >= 0 ? "+" : "−") + Math.abs(d) + "</span>";
	}

	// Map title from /config (GameServers:MapTitles), else the shortened uid.
	function mapCell(uid) {
		var title = uid && config.mapTitles ? config.mapTitles[uid] : null;
		if (title) return '<td class="hide-sm" title="' + esc(uid) + '">' + esc(title) + "</td>";
		return '<td class="hide-sm mono" title="' + esc(uid || "") + '">' + esc(uid ? String(uid).slice(0, 8) : "—") + "</td>";
	}

	function matchRow(m) {
		var players = (m.players || []).map(function (p) {
			var res = p.result && p.result !== "None" ? ' <span class="status-pill res-' + esc(p.result) + '">' + esc(p.result) + "</span>" : "";
			return "<div>" + esc(p.name || String(p.userId || "").slice(0, 8)) + res + (m.rated === false ? "" : delta(p)) + "</div>";
		}).join("");
		var canVoid = m.status !== "Void" && m.status !== "Cancelled";

		return "<tr>"
			+ '<td><span title="' + esc(SP.fmtDate(m.createdAt, true)) + '">' + esc(SP.timeAgo(m.createdAt)) + "</span>"
			+ '<span class="sub mono" title="' + esc(m.id) + '">' + esc(String(m.id || "").slice(0, 8)) + "</span></td>"
			+ '<td><span class="status-pill st-' + esc(m.status) + '">' + esc(m.status || "?") + "</span>"
			+ (m.rated === false ? '<span class="badge">unrated</span>' : "")
			+ (m.reason ? '<span class="sub">' + esc(m.reason) + "</span>" : "") + "</td>"
			+ "<td>" + (players || '<span class="meta">—</span>') + "</td>"
			+ mapCell(m.mapUid)
			+ '<td><div class="actions">' + (canVoid ? '<button class="btn small danger" data-act="void" data-id="' + esc(m.id) + '" type="button">Void</button>' : "") + "</div></td>"
			+ "</tr>";
	}

	async function loadMatches() {
		var status = $("matches-status").value;
		var limit = $("matches-limit").value;
		$("matches-state").innerHTML = stateHtml("", "Loading…", "");
		var res = await call("/admin/matches?status=" + encodeURIComponent(status) + "&limit=" + encodeURIComponent(limit));
		if (res.status === 401) return;
		if (!res.ok || !Array.isArray(res.data)) {
			$("matches-body").innerHTML = "";
			$("matches-state").innerHTML = apiErrorState(res, "matches");
			return;
		}

		matchesById = {};
		res.data.forEach(function (m) { matchesById[m.id] = m; });
		$("matches-body").innerHTML = res.data.map(matchRow).join("");
		$("matches-state").innerHTML = res.data.length ? "" : stateHtml("empty", "No matches", status ? "No matches with status " + status + "." : "");
	}

	async function voidMatch(id) {
		var m = matchesById[id];
		if (!m) return;
		var names = (m.players || []).map(function (p) { return p.name; }).filter(Boolean).join(" vs ");
		var v = await dialog({
			title: "Void match?",
			text: (names ? names + ". " : "") + "The match is marked void" + (m.status === "Completed" ? " and its result no longer counts" : "") + ". This is logged.",
			confirm: "Void match",
			danger: true,
			fields: [{ name: "reason", label: "Reason", type: "textarea", required: true, maxlength: 500 }],
			validate: function (x) { return x.reason ? null : "A reason is required."; }
		});
		if (!v) return;
		var res = await call("/admin/matches/" + encodeURIComponent(id) + "/void", "POST", { reason: v.reason });
		if (actionResult(res, "Match voided.")) loadMatches();
	}

	// ---------- seasons ----------
	var activeSeason = null;

	async function loadSeasons() {
		$("seasons-state").innerHTML = stateHtml("", "Loading…", "");
		var res = await SP.api("/seasons", { token: token });
		if (!res.ok || !Array.isArray(res.data)) {
			$("seasons-body").innerHTML = "";
			$("seasons-state").innerHTML = apiErrorState(res, "seasons");
			activeSeason = null;
			return;
		}

		var list = res.data.slice().sort(function (a, b) { return new Date(b.startedAt || 0) - new Date(a.startedAt || 0); });
		activeSeason = list.filter(function (s) { return s.isActive; })[0] || null;
		$("season-end").disabled = !activeSeason;
		$("seasons-body").innerHTML = list.map(function (s) {
			return "<tr><td>" + esc(s.id) + "</td><td><b>" + esc(s.name || "") + "</b></td>"
				+ "<td>" + esc(SP.fmtDate(s.startedAt, true)) + "</td>"
				+ "<td>" + esc(s.endedAt ? SP.fmtDate(s.endedAt, true) : "—") + "</td>"
				+ "<td>" + (s.isActive ? '<span class="badge green">active</span>' : '<span class="badge">ended</span>') + "</td></tr>";
		}).join("");
		$("seasons-state").innerHTML = list.length ? "" : stateHtml("empty", "No seasons yet", "Start the first season to begin a fresh ladder.");
	}

	async function startSeason() {
		var v = await dialog({
			title: "Start a new season",
			text: activeSeason
				? "This ends “" + activeSeason.name + "” (its final standings are frozen) and starts a new season."
				: "Starts a new ranked season.",
			confirm: "Start season",
			danger: !!activeSeason,
			fields: [{ name: "name", label: "Season name", type: "text", required: true, maxlength: 64, placeholder: "Season 2" }],
			validate: function (x) { return x.name ? null : "Enter a name."; }
		});
		if (!v) return;
		var res = await call("/admin/seasons/start", "POST", { name: v.name });
		if (actionResult(res, "Season “" + v.name + "” started.")) loadSeasons();
	}

	async function endSeason() {
		if (!activeSeason) return;
		if (!await confirmBox("End “" + activeSeason.name + "”?", "Final standings are frozen. Until a new season starts there is no active season.", "End season")) return;
		var res = await call("/admin/seasons/end", "POST", {});
		if (actionResult(res, "Season ended.")) loadSeasons();
	}

	// ---------- audit ----------
	function detailsText(d) {
		if (d === null || d === undefined) return "";
		if (typeof d === "string") return d;
		try { return JSON.stringify(d); } catch (e) { return String(d); }
	}

	async function loadAudit() {
		$("audit-state").innerHTML = stateHtml("", "Loading…", "");
		var res = await call("/admin/actions?limit=" + encodeURIComponent($("audit-limit").value));
		if (res.status === 401) return;
		var rows = Array.isArray(res.data) ? res.data : res.data && Array.isArray(res.data.rows) ? res.data.rows : null;
		if (!res.ok || !rows) {
			$("audit-body").innerHTML = "";
			$("audit-state").innerHTML = apiErrorState(res, "the audit log");
			return;
		}

		$("audit-body").innerHTML = rows.map(function (a) {
			var target = a.targetName || a.targetUserName
				|| (a.targetUserId ? "user " + String(a.targetUserId).slice(0, 8) : "")
				|| (a.targetMatchId ? "match " + String(a.targetMatchId).slice(0, 8) : "");
			return '<tr><td title="' + esc(SP.fmtDate(a.createdAt, true)) + '">' + esc(SP.fmtDate(a.createdAt, true)) + "</td>"
				+ "<td>" + esc(a.adminName || String(a.adminUserId || "").slice(0, 8)) + "</td>"
				+ '<td class="mono">' + esc(a.action || "") + "</td>"
				+ "<td>" + esc(target || "—") + "</td>"
				+ '<td class="hide-sm" style="max-width:360px;word-break:break-word">' + esc(detailsText(a.details)) + "</td></tr>";
		}).join("");
		$("audit-state").innerHTML = rows.length ? "" : stateHtml("empty", "No admin actions yet", "");
	}

	// ---------- wiring ----------
	$("signin-google").addEventListener("click", function () { startLogin("google"); });
	$("signin-discord").addEventListener("click", function () { startLogin("discord"); });
	$("signin-dev").addEventListener("click", devLogin);
	$("dev-name").addEventListener("keydown", function (e) { if (e.key === "Enter") devLogin(); });
	$("signout").addEventListener("click", signOut);
	$("denied-signout").addEventListener("click", signOut);

	document.querySelectorAll(".tab").forEach(function (b) {
		b.addEventListener("click", function () { selectTab(b.dataset.tab); });
	});

	$("users-form").addEventListener("submit", function (e) { e.preventDefault(); loadUsers(); });
	$("users-limit").addEventListener("change", loadUsers);
	var searchDebounce = null;
	$("users-search").addEventListener("input", function () {
		clearTimeout(searchDebounce);
		searchDebounce = setTimeout(loadUsers, 350);
	});
	$("users-body").addEventListener("click", function (e) {
		var b = e.target.closest("button[data-act]");
		if (b) userAction(b.dataset.act, b.dataset.id);
	});

	$("matches-form").addEventListener("submit", function (e) { e.preventDefault(); loadMatches(); });
	$("matches-status").addEventListener("change", loadMatches);
	$("matches-limit").addEventListener("change", loadMatches);
	$("matches-body").addEventListener("click", function (e) {
		var b = e.target.closest("button[data-act=void]");
		if (b) voidMatch(b.dataset.id);
	});

	$("season-start").addEventListener("click", startSeason);
	$("season-end").addEventListener("click", endSeason);
	$("seasons-refresh").addEventListener("click", loadSeasons);
	$("audit-refresh").addEventListener("click", loadAudit);
	$("audit-limit").addEventListener("change", loadAudit);

	// Boot: read public config (which sign-in options exist), then check the stored token.
	SP.api("/config").then(function (res) {
		config = res.ok && res.data ? res.data : {};
		gate();
	});
})();
