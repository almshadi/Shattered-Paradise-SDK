// Shared helpers for the public page and the admin page. No frameworks.
(function () {
	"use strict";

	// fetch() wrapper that never throws: resolves to { ok, status, data }.
	// status 0 = network error. data is parsed JSON when available, otherwise null.
	async function api(path, opts) {
		opts = opts || {};
		var headers = Object.assign({ "Accept": "application/json" }, opts.headers || {});
		if (opts.token)
			headers["Authorization"] = "Bearer " + opts.token;

		var init = { method: opts.method || "GET", headers: headers, cache: "no-store" };
		if (opts.body !== undefined) {
			headers["Content-Type"] = "application/json";
			init.body = JSON.stringify(opts.body);
		}

		var controller = typeof AbortController !== "undefined" ? new AbortController() : null;
		var timer = null;
		if (controller) {
			init.signal = controller.signal;
			timer = setTimeout(function () { controller.abort(); }, opts.timeout || 15000);
		}

		try {
			var res = await fetch(path, init);
			var data = null;
			var text = await res.text();
			if (text) {
				try { data = JSON.parse(text); } catch (e) { data = null; }
			}
			return { ok: res.ok, status: res.status, data: data };
		} catch (e) {
			return { ok: false, status: 0, data: null };
		} finally {
			if (timer) clearTimeout(timer);
		}
	}

	function esc(value) {
		return String(value === null || value === undefined ? "" : value)
			.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
			.replace(/"/g, "&quot;").replace(/'/g, "&#39;");
	}

	function num(value, digits) {
		var n = Number(value);
		if (!isFinite(n)) return "—";
		return n.toLocaleString(undefined, { maximumFractionDigits: digits || 0, minimumFractionDigits: digits || 0 });
	}

	function winPct(wins, games) {
		var w = Number(wins) || 0, g = Number(games) || 0;
		return g > 0 ? Math.round((w / g) * 100) : null;
	}

	function fmtDate(value, withTime) {
		if (!value) return "—";
		var d = new Date(value);
		if (isNaN(d.getTime())) return "—";
		var opts = { year: "numeric", month: "short", day: "numeric" };
		if (withTime) { opts.hour = "2-digit"; opts.minute = "2-digit"; }
		return d.toLocaleString(undefined, opts);
	}

	function timeAgo(value) {
		if (!value) return "—";
		var d = new Date(value).getTime();
		if (isNaN(d)) return "—";
		var s = Math.round((Date.now() - d) / 1000);
		var future = s < 0;
		s = Math.abs(s);
		var out;
		if (s < 60) out = s + "s";
		else if (s < 3600) out = Math.round(s / 60) + "m";
		else if (s < 86400) out = Math.round(s / 3600) + "h";
		else out = Math.round(s / 86400) + "d";
		return future ? "in " + out : out + " ago";
	}

	function errorText(res) {
		if (!res) return "Unknown error";
		if (res.status === 0) return "Can't reach the server.";
		var d = res.data;
		var msg = d && (d.message || d.error || d.title || d.detail);
		return msg ? String(msg) + " (HTTP " + res.status + ")" : "HTTP " + res.status;
	}

	window.SP = { api: api, esc: esc, num: num, winPct: winPct, fmtDate: fmtDate, timeAgo: timeAgo, errorText: errorText };
})();
