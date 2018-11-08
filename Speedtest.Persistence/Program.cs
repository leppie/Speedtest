using System;
using System.IO;
using System.Linq;
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
				await ctx.Results.AddRangeAsync(results.Select(r => new Result
				{
					Timestamp = DateTimeOffset.Now, Ping = r.ping, DownloadSpeed = r.kbit, Host = r.host,
					Search = configuration["Search"]
				}));
				await ctx.SaveChangesAsync();
			}
		}
	}
}
