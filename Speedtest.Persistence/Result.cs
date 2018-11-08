using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Speedtest.Persistence
{
	public class Result
	{
		public DateTimeOffset Timestamp { get; set; }
		public string Host { get; set; }
		public double Ping { get; set; }
		public long DownloadSpeed { get; set; }
		public string Search { get; set; }
	}
}
