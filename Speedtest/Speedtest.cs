﻿using System;
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
		public int PingCount { get; set; } = 20;
		public string[] Servers { get; set; } = { };
		public string Search { get; set; }
		public bool Debug { get; set; } = false;
		public bool Interactive { get; set; } = true;
		public int CandidateCount { get; set; } = 8;
		public int CandidatePingMax { get; set; } = 8;
		public int CandidateTests { get; set; } = 1;

		public override string ToString()
		{
			return $@"Settings:
{nameof(DownloadTime),-24}: {DownloadTime}
{nameof(DownloadConnections),-24}: {DownloadConnections}
{nameof(BufferSize),-24}: {BufferSize}
{nameof(PingCount),-24}: {PingCount}
{nameof(CandidateCount),-24}: {CandidateCount}
{nameof(CandidatePingMax),-24}: {CandidatePingMax}
{nameof(CandidateTests),-24}: {CandidateTests}
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

		public static IEnumerable<long> RemoveUpperOutliers(this long[] set)
		{
			var avg = set.Average();
			var sd = Math.Sqrt(Variance());

			foreach (var i in set)
			{
				if (i > 0 && i - avg <= sd)
				{
					yield return i;
				}
			}

			double Variance()
			{
				double mean = avg, sumSq = 0;
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
		}
	}

	public class Speedtest
	{
		static readonly Settings Settings = new Settings();

		class Server : IEquatable<Server>
		{
			public string cc { get; set; }
			public string country { get; set; }
			public string host { get; set; }
			public int distance { get; set; }
			public int https_functional { get; set; }
			public string sponsor { get; set; }
			public string name { get; set; }

			public double ping { get; set; } = double.MaxValue;

			public override string ToString()
			{
				return $"{sponsor,-36} ({ping,6:f1} ms): {host}";
			}

			public bool Equals(Server other)
			{
				if (ReferenceEquals(null, other))
				{
					return false;
				}

				if (ReferenceEquals(this, other))
				{
					return true;
				}

				return string.Equals(host, other.host) && string.Equals(sponsor, other.sponsor) && string.Equals(name, other.name);
			}

			public override bool Equals(object obj)
			{
				if (ReferenceEquals(null, obj))
				{
					return false;
				}

				if (ReferenceEquals(this, obj))
				{
					return true;
				}

				if (obj.GetType() != this.GetType())
				{
					return false;
				}

				return Equals((Server) obj);
			}

			public override int GetHashCode()
			{
				unchecked
				{
					var hashCode = (host != null ? host.GetHashCode() : 0);
					hashCode = (hashCode * 397) ^ (sponsor != null ? sponsor.GetHashCode() : 0);
					hashCode = (hashCode * 397) ^ (name != null ? name.GetHashCode() : 0);
					return hashCode;
				}
			}

			public static bool operator ==(Server left, Server right)
			{
				return Equals(left, right);
			}

			public static bool operator !=(Server left, Server right)
			{
				return !Equals(left, right);
			}
		}

		static async Task<int> Main(string[] args)
		{
			var builder = new ConfigurationBuilder()
				.SetBasePath(Directory.GetCurrentDirectory())
				.AddJsonFile("appsettings.json")
				.AddCommandLine(args);

			var configuration = builder.Build();

			var results = await Run(configuration);
			return !results.Any() ? 1 : 0;
		}

		public static async Task<IEnumerable<Result>> Run(IConfiguration configuration)
		{
			string[] args = { };

			configuration.Bind(Settings);

			var results = new List<Result>();

			if (Settings.Debug)
			{
				Console.WriteLine(Settings);
			}

			if (Settings.Servers.Length == 0)
			{
				var servers = await GetServers(Settings.Search);
				var candidates = servers.Take(Settings.CandidateTests).ToArray();
				if (!candidates.Any())
				{
					Console.Error.WriteLine($"Could not find server: {Settings.Search}");
					return results;
				}

				if (Settings.Debug)
				{
					Console.WriteLine($"Auto Server(s): {string.Join(", ", candidates.Select(x => x.sponsor))}");
				}

				args = candidates.Select(x => x.host).ToArray();
			}
			else
			{
				args = Settings.Servers;
			}

			foreach (var server in args)
			{
				results.Add(await Run(server));
			}

			return results;
		}

		public static async Task<Result> Run(string server)
		{
			var ping = await GetPing(server, Settings.PingCount);

			if (Settings.Interactive)
			{
				Console.CursorVisible = false;
				Update($"{ping,6:f1} ms | {0,8} kbit | {server}");
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
				var kbit = (counts.Sum() * 8) / elapsed;

				Update($"{ping,6:f1} ms | {kbit,8} kbit | {server}");
				Console.WriteLine();

				if (Settings.Interactive)
				{
					Console.CursorVisible = true;
				}

				return new Result
				{
					Ping = ping, 
					DownloadSpeed = kbit,
					Host =  server,
					Timestamp = DateTimeOffset.Now

				};

				async Task Read(int i)
				{
					var buffer = new byte[Settings.BufferSize];
					await tasks[i].Result.ReadAsync(buffer, 0, buffer.Length, source.Token)
						.ContinueWith(async x =>
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

		private static void Update(string msg, int left = 0)
		{
			if (Settings.Interactive)
			{
				Console.CursorLeft = left;
			}

			Console.Write(msg);
		}

		private static async Task<IEnumerable<Server>> GetServers(string search)
		{
			var url = $"https://www.speedtest.net/api/js/servers?search={search}";
			using (var client = new HttpClient())
			{
				var result = await client.GetStringAsync(url);
				var allservers = new List<Server>(JsonConvert.DeserializeObject<Server[]>(result));

				var check = new HashSet<Server>();

				var servers = new List<Server>();

				foreach (var server in allservers)
				{
					if (server.https_functional == 1 && !check.Contains(server))
					{
						servers.Add(server);
						check.Add(server);
					}
				}

				foreach (var server in servers.Take(Settings.CandidateCount))
				{
					server.ping = await GetPing(server.host, Settings.CandidatePingMax, 750);
					if (server.ping < double.MaxValue)
					{
						try
						{
							await client.GetStreamAsync(GetUrl(server.host, Guid.NewGuid()));
						}
						catch (Exception e)
						{
							if (Settings.Debug)
							{
								Console.WriteLine(e);
							}

							server.ping = double.MaxValue;
						}
					}
				}

				servers = servers.OrderBy(x => x.ping).Take(Settings.CandidateCount).Where(x => x.ping < double.MaxValue).ToList();

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

		private static async Task<double> GetPing(string host, int pingCount, int pingTimeLimit = 2000)
		{
			using (var ping = new Ping())
			{
				var url = new Uri("https://" + host);

				var initial = await ping.SendPingAsync(url.Host, 1000);

				if (initial.Status != IPStatus.Success)
				{
					if (Settings.Debug)
					{
						Console.WriteLine($"ping error ({initial.Status}): {host}");
					}

					return double.MaxValue;
				}

				// adjust max ping count so it does not take too long
				pingCount = (int) Math.Min(pingCount, pingTimeLimit / initial.RoundtripTime);

				var pingTimes = new long[pingCount];
				var pc = pingCount;

				pingTimes[--pc] = initial.RoundtripTime;

				while (pc-- > 0)
				{
					var reply = await ping.SendPingAsync(url.Host, 1000);
					pingTimes[pc] = reply.RoundtripTime;
				}

				if (Settings.Debug)
				{
					Console.WriteLine(
						$"avg: {pingTimes.Average():f3} min: {pingTimes.Min()} max: {pingTimes.Max()} sd: {pingTimes.StdDev():f2} var: {pingTimes.Variance():f2} data: [{string.Join(",", pingTimes)}]");
				}

				pingTimes = pingTimes.RemoveUpperOutliers().ToArray();
				if (Settings.Debug)
				{
					Console.WriteLine(
						$"avg: {pingTimes.Average():f3} min: {pingTimes.Min()} max: {pingTimes.Max()} sd: {pingTimes.StdDev():f2} var: {pingTimes.Variance():f2} count: {pingTimes.Length} data: [{string.Join(",", pingTimes)}]");
				}

				return pingTimes.Where(x => x != 0).Average();
			}
		}

		static string GetUrl(string server, Guid guid)
		{
			return
				$"https://{server}/download?nocache={guid}&size=25000000";
		}
	}
}
