using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Speedtest.Persistence
{
	public class SpeedtestDbContext : DbContext
	{
		public DbSet<Result> Results { get; set; }
		private readonly IConfiguration _configuration;

		public SpeedtestDbContext(IConfiguration configuration)
		{
			_configuration = configuration;
		}

		protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
		{
			optionsBuilder.UseSqlServer(_configuration["ConnectionString"]);
		}

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			modelBuilder.Entity<Result>(b =>
			{
				b.ToTable(nameof(Results));
				b.HasKey(x => new {x.Timestamp, x.Host});
			});
		}
	}
}
