using System;
using System.IO;
using System.Text;

namespace FTeleHub
{
	//// 허브 로그 - 날짜별 파일. 토큰 · 본문 전체는 쓰지 않는다(T8). 사용자 글은 길이만.
	internal static class HubLog
	{
		private static readonly object _lock = new object();
		private static volatile string _folderOverride;

		public static string Folder
		{
			get { string o = _folderOverride; return o ?? Path.Combine(HubConfig.DataFolder, "log"); }
		}

		//// 자가 시험 전용 - 시험 줄이 돌고 있는 허브의 로그(hub_<날짜>.log)에 섞이지 않게 로그 폴더를 시험 출력 폴더로 돌린다.
		//// 프로세스 안에서 한 번, 첫 로그 줄 전에 부른다(--selftest 는 허브 엔진을 띄우지 않는 별도 실행).
		public static void RedirectForSelfTest(string folder)
		{
			_folderOverride = folder;
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
