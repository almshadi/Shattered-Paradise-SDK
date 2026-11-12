using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ranked.Backend.Data;

// Used only by the `dotnet ef` tooling, so migrations can be added/scripted without running Program's startup.
// The connection string is only needed for commands that touch a database (e.g. `dotnet ef database update`).
public class DesignTimeDbFactory : IDesignTimeDbContextFactory<RankedDb>
{
	public RankedDb CreateDbContext(string[] args)
	{
		var conn = Environment.GetEnvironmentVariable("ConnectionStrings__Db")
			?? "Host=localhost;Port=5442;Database=ranked;Username=ranked;Password=ranked";
		var options = new DbContextOptionsBuilder<RankedDb>().UseNpgsql(conn).Options;
		return new RankedDb(options);
	}
}
