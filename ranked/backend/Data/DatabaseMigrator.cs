using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ranked.Backend.Data;

// Brings the database schema up to date on startup.
// Databases created by the old EnsureCreated() prototype have the full 0001_Initial schema but no
// __EFMigrationsHistory table; those are "baselined" (0001_Initial marked as applied) before migrating.
public static class DatabaseMigrator
{
	public const string InitialMigrationSuffix = "_0001_Initial";

	public static void Migrate(RankedDb db, ILogger log)
	{
		var history = db.GetService<IHistoryRepository>();
		var hasUsers = TableExists(db, "Users");
		if (hasUsers && !history.Exists())
		{
			var initial = db.GetService<IMigrationsAssembly>().Migrations.Keys
				.Single(id => id.EndsWith(InitialMigrationSuffix, StringComparison.Ordinal));

			log.LogWarning("Database was created without migrations (EnsureCreated). Baselining it at {Migration}.", initial);
			using var tx = db.Database.BeginTransaction();
			db.Database.ExecuteSqlRaw(history.GetCreateScript());
			db.Database.ExecuteSqlRaw(history.GetInsertScript(new HistoryRow(initial, EfProductVersion())));
			tx.Commit();
		}

		var pending = db.Database.GetPendingMigrations().ToList();
		if (pending.Count > 0)
			log.LogInformation("Applying database migrations: {Migrations}", string.Join(", ", pending));

		db.Database.Migrate();
	}

	static bool TableExists(RankedDb db, string table)
	{
		return db.Database.SqlQuery<bool>(
			$"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = {table}) AS \"Value\"").Single();
	}

	// Same value EF writes into __EFMigrationsHistory.ProductVersion (e.g. "8.0.10").
	static string EfProductVersion()
	{
		var version = typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
		var plus = version.IndexOf('+');
		return plus >= 0 ? version[..plus] : version;
	}
}
