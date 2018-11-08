using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Speedtest.Persistence
{
	class Program
	{
		static async Task Main(string[] args)
		{
			var builder = new ConfigurationBuilder()
				.SetBasePath(Directory.GetCurrentDirectory())
				.AddJsonFile("appsettings.json")
				.AddCommandLine(args);

			var configuration = builder.Build();
			var results = await Speedtest.Run(configuration);

			using (var ctx = new SpeedtestDbContext(configuration))
			{
				await ctx.Database.EnsureCreatedAsync();
				await ctx.Results.AddRangeAsync(results);
				await ctx.SaveChangesAsync();
			}
		}
	}
}
