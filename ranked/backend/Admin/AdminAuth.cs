using Microsoft.Extensions.Options;
using Ranked.Backend.Auth;
using Ranked.Backend.Data;

namespace Ranked.Backend.Admin;

// Bound to "Ranked:Admin". Both lists are comma/semicolon/whitespace separated strings so they can be set from one
// env var (Ranked__Admin__AdminEmails=a@x.com,b@y.com). Config admins are admins regardless of User.IsAdmin, which
// lets the first admin be bootstrapped without editing the database.
public class AdminOptions
{
	public string AdminUserIds { get; set; } = "";
	public string AdminEmails { get; set; } = "";
}

// Stateless so it needs no DI registration; options are read from the request services.
public static class AdminAuth
{
	static readonly char[] Separators = { ',', ';', ' ', '\t', '\n', '\r' };

	public static AdminOptions Options(HttpContext ctx) =>
		ctx.RequestServices.GetRequiredService<IOptionsMonitor<AdminOptions>>().CurrentValue;

	public static bool IsConfigAdmin(User u, AdminOptions o)
	{
		var ids = o.AdminUserIds.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
		if (ids.Any(s => Guid.TryParse(s, out var g) && g == u.Id))
			return true;

		if (string.IsNullOrEmpty(u.Email))
			return false;

		var emails = o.AdminEmails.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
		return emails.Any(e => string.Equals(e, u.Email, StringComparison.OrdinalIgnoreCase));
	}

	public static bool IsAdmin(User u, AdminOptions o) => u.IsAdmin || IsConfigAdmin(u, o);

	const string ItemKey = "ranked.admin.user";

	// The admin resolved by the endpoint filter (always set inside the filtered /admin group).
	public static User Admin(HttpContext ctx) => (User)ctx.Items[ItemKey]!;

	// 401 without a valid session, 403 {"error":"forbidden"} for non-admins.
	public static async ValueTask<object?> Filter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
	{
		var http = ctx.HttpContext;
		var db = http.RequestServices.GetRequiredService<RankedDb>();

		var user = await CurrentUser.GetAsync(http, db);
		if (user == null)
			return Results.Unauthorized();

		if (!IsAdmin(user, Options(http)))
			return Results.Json(new { error = "forbidden" }, statusCode: 403);

		http.Items[ItemKey] = user;
		return await next(ctx);
	}
}
