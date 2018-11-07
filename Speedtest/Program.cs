using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Configuration;

namespace Speedtest
{
	class Settings
	{
		public int DownloadTime { get; set; } = 5000;
		public int DownloadConnections { get; set; } = 6;
		public int BufferSize { get; set; } = 4096;
		public int PingCount { get; set; } = 10;
		public string[] Servers { get; set; } = { };
		public string Search { get; set; }
		public bool Debug { get; set; } = false;
		public bool Interactive { get; set; } = true;

		public override string ToString()
		{
			return $@"Settings:
{nameof(DownloadTime),-24}: {DownloadTime}
{nameof(DownloadConnections),-24}: {DownloadConnections}
{nameof(BufferSize),-24}: {BufferSize}
{nameof(PingCount),-24}: {PingCount}
{nameof(Debug),-24}: {Debug}
{nameof(Interactive),-24}: {Interactive}
{nameof(Search),-24}: ""{Search}""
{nameof(Servers),-24}: [{string.Join(", ", Servers)}]
";
		}
	}

	static class StatsExtensions
	{
		public static double Variance(this IEnumerable<long> set)
		{
			double mean = set.Average(), sumSq = 0;
			var n = 0;

			foreach (var i in set)
			{
				var delta = i - mean;
				sumSq += delta * delta;
				n++;
			}

			if (n <= 1)
			{
				return 0;
			}

			return sumSq / (n - 1);
		}

		public static double StdDev(this IEnumerable<long> set)
		{
			return Math.Sqrt(set.Variance());
		}

		public static IEnumerable<long> RemoveUpperOutliers(this IEnumerable<long> set)
		{
			var avg = set.Average();
			var sd = set.StdDev();

			foreach (var i in set)
			{
				if (i - avg <= sd)
				{
					yield return i;
				}
			}
		}
	}

	class Program
	{
		static readonly Settings Settings = new Settings();

		class Server
		{
			public string cc { get; set; }
			public string country { get; set; }
			public string host { get; set; }
			public int distance { get; set; }
			public int https_functional { get; set; }
			public string sponsor { get; set; }
			public string name { get; set; }

			public override string ToString()
			{
				return $"{sponsor,-36}: {host}";
			}
		}

		static async Task<int> Main(string[] args)
		{
			var builder = new ConfigurationBuilder()
				.SetBasePath(Directory.GetCurrentDirectory())
				.AddJsonFile("appsettings.json")
				.AddCommandLine(args);

			var configuration = builder.Build();
			configuration.Bind(Settings);

			if (Settings.Debug)
			{
				Console.WriteLine(Settings);
			}
			
			if (Settings.Servers.Length == 0)
			{
				var servers = await GetServers(Settings.Search);
				var candidate = servers.FirstOrDefault(x => x.https_functional == 1)?.host;
				if (candidate == null)
				{
					Console.Error.WriteLine($"Could not find server: {Settings.Search}");
					return 1;
				}

				if (Settings.Debug)
				{
					Console.WriteLine($"Auto Server: {candidate}");
				}

				args = new[] { candidate };
			}
			else
			{
				args = Settings.Servers;
			}
			
			foreach (var server in args)
			{
				var pingTimes = new long[Settings.PingCount];

				var ping = new Ping();
				var url = new Uri("https://" + server);

				// sent dummy ping to ensure DNS is resolved
				await ping.SendPingAsync(url.Host);

				var pc = Settings.PingCount;
				while (pc-- > 0)
				{
					var reply = await ping.SendPingAsync(url.Host);
					pingTimes[pc] = reply.RoundtripTime;
				}

				if (Settings.Debug)
				{
					Console.WriteLine($"avg: {pingTimes.Average():f3} min: {pingTimes.Min()} max: {pingTimes.Max()} sd: {pingTimes.StdDev():f2} var: {pingTimes.Variance():f2} data: [{string.Join(",", pingTimes)}]");
				}

				pingTimes = pingTimes.RemoveUpperOutliers().ToArray();

				if (Settings.Debug)
				{
					Console.WriteLine(
						$"avg: {pingTimes.Average():f3} min: {pingTimes.Min()} max: {pingTimes.Max()} sd: {pingTimes.StdDev():f2} var: {pingTimes.Variance():f2} count: {pingTimes.Length} data: [{string.Join(",", pingTimes)}]");
				}

				if (Settings.Interactive)
				{
					Console.CursorVisible = false;
					Update($"{pingTimes.Average(),6:f1} ms | ");
				}

				using (var client = new HttpClient())
				{
					var tasks = Enumerable.Range(0, Settings.DownloadConnections)
						.Select(_ => client.GetStreamAsync(GetUrl(server, Guid.NewGuid())))
						.ToArray();
					var counts = new int[tasks.Length];

					await Task.WhenAll(tasks);

					var source = new CancellationTokenSource();

					var sw = Stopwatch.StartNew();
					for (var i = 0; i < tasks.Length; i++)
					{
						await Read(i);
					}

					while (sw.ElapsedMilliseconds < Settings.DownloadTime)
					{
						await Task.Delay(50, source.Token);
						if (Settings.Interactive)
						{
							Update($"{(counts.Sum() * 8) / sw.ElapsedMilliseconds,8} kbit", 12);
						}
					}

					source.Cancel();

					var elapsed = sw.ElapsedMilliseconds;

					Update($"{pingTimes.Average(),6:f1} ms | {(counts.Sum() * 8) / elapsed,8} kbit | {server}");
					Console.WriteLine();

					if (Settings.Interactive)
					{
						Console.CursorVisible = true;
					}

					async Task Read(int i)
					{
						var buffer = new byte[Settings.BufferSize];
						await tasks[i].Result.ReadAsync(buffer, 0, buffer.Length, source.Token).ContinueWith(async x =>
						{
							var c = await x;
							counts[i] += c;

							if (c == 0)
							{
								tasks[i] = Task.FromResult(await client.GetStreamAsync(GetUrl(server, Guid.NewGuid())));
							}

							await Read(i);
						}, source.Token);
					}
				}
			}

			return 0;
		}

		private static void Update(string msg, int left = 0)
		{
			if (Settings.Interactive)
			{
				Console.CursorLeft = left;
			}

			Console.Write(msg);
		}

		private static async Task<Server[]> GetServers(string search)
		{
			var url = $"https://www.speedtest.net/api/js/servers?search={search}";
			using (var client = new HttpClient())
			{
				var result = await client.GetStringAsync(url);
				var servers = JsonConvert.DeserializeObject<Server[]>(result);
				if (Settings.Debug)
				{
					Console.WriteLine($"Server search: {search}");
					foreach (var svr in servers)
					{
						Console.WriteLine(svr);
					}
					Console.WriteLine();
				}

				return servers;
			}
		}

		static string GetUrl(string server, Guid guid)
		{
			return
				$"https://{server}/download?nocache={guid}&size=25000000";
		}
	}
}
