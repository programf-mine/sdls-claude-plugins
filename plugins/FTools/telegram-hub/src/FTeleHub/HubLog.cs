using System;
using System.IO;
using System.Text;

namespace FTeleHub
{
	//// 허브 로그 - 날짜별 파일. 토큰 · 본문 전체는 쓰지 않는다(T8). 사용자 글은 길이만.
	internal static class HubLog
	{
		private static readonly object _lock = new object();

		public static string Folder
		{
			get { return Path.Combine(HubConfig.DataFolder, "log"); }
		}

		public static void Info(string msg) { Write("INFO", msg); }
		public static void Warn(string msg) { Write("WARN", msg); }
		public static void Error(string msg) { Write("ERROR", msg); }

		private static void Write(string level, string msg)
		{
			try
			{
				lock (_lock)
				{
					Directory.CreateDirectory(Folder);
					string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + HubConfig.MaskToken(msg) + Environment.NewLine;
					File.AppendAllText(Path.Combine(Folder, "hub_" + DateTime.Now.ToString("yyyyMMdd") + ".log"), line, new UTF8Encoding(false));
				}
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}
}
