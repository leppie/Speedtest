using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text;

namespace Speedtest
{
	class Program
	{
		private const int Milliseconds = 5000;
		private const int Connections = 6;
		private const int BufferSize = 4096;
		private const int PingCount = 10;

		static async Task Main(string[] args)
		{
			var epoch = new DateTime(1970,1,1);
			var enc = Encoding.Default;

			foreach (var server in args)
			{
				var pingTimes = 0L;

				using (var ws = new ClientWebSocket())
				{
					await ws.ConnectAsync(new Uri($"wss://{server}.prod.hosts.ooklaserver.net:8080/ws"),
						CancellationToken.None);

					await ws.SendAsync(enc.GetBytes("HI"), WebSocketMessageType.Text, true, CancellationToken.None);
					await ws.ReceiveAsync(new byte[128], CancellationToken.None);
					await ws.SendAsync(enc.GetBytes("PING "), WebSocketMessageType.Text, true, CancellationToken.None);

					var offset = 0L;
					{
						var buffer = new byte[30];
						var rr = await ws.ReceiveAsync(buffer, CancellationToken.None);
						var rec = enc.GetString(buffer, 0, rr.Count).TrimEnd('\n');
						var end = long.Parse(rec.Substring(5));
						var now = (long) (DateTime.UtcNow - epoch).TotalMilliseconds;

						offset = now - end;
					}

					var pc = PingCount;
					while (pc-- > 0)
					{
						var buffer = new byte[30];
						var start = (long) (DateTime.UtcNow - epoch).TotalMilliseconds;
						var msg = $"PING {start}";
						var bytes = enc.GetBytes(msg);
						await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
						var rr = await ws.ReceiveAsync(buffer, CancellationToken.None);
						var rec = enc.GetString(buffer, 0, rr.Count).TrimEnd('\n');
						var end = long.Parse(rec.Substring(5));

						pingTimes += (long) TimeSpan.FromMilliseconds(end - start + offset).TotalMilliseconds;
					}
				}

				using (var client = new HttpClient())
				{
					var tasks = Enumerable.Range(0, Connections)
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

					await Task.Delay(Milliseconds, source.Token);

					source.Cancel();

					var elapsed = sw.ElapsedMilliseconds;
					Console.WriteLine($"{(counts.Sum() * 8) / elapsed,8} kbit | {pingTimes / PingCount,4} ms | {server}");

					async Task Read(int i)
					{
						var buffer = new byte[BufferSize];
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
		}

		static string GetUrl(string server, Guid guid)
		{
			return
				$"https://{server}.prod.hosts.ooklaserver.net:8080/download?nocache={guid}&size=25000000";
		}
	}
}
