using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SdoTelegramHub
{
	//// hub.json - %LOCALAPPDATA%\SDOPTICS\TelegramHub\hub.json. 토큰은 DPAPI(CurrentUser) 로만 저장(H6 - 저장 상태 보호).
	internal sealed class HubConfig
	{
		public string TokenProtected;					//// base64(DPAPI)
		public long ChatId;								//// 허용 그룹(포럼) ID
		public List<long> AllowedUserIds = new List<long>();
		public Dictionary<string, string> Topics = new Dictionary<string, string>();	//// 토픽(message_thread_id) → 노드 이름
		public bool Paused;								//// /pause 영속(H3)
		public string RepoUrl = "https://github.com/programf-mine/SDLS.git";
		public string RelayDir = "00B. DOC/003. Claude/Automation/relay";
		public string Branch = "master";
		public int PollSeconds = 5;						//// 보고 조회 주기(ls-remote)
		public long LastUpdateId;						//// 확정한 마지막 update_id(offset = +1)
		public bool BaselineDone;						//// 설치 때 기존 보고를 sent 로 잡았는가
		public string LastAlive;						//// 마지막 동작 시각(재개 알림의 멈춤 시간 - H8)

		private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

		public static string DataFolder
		{
			get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDOPTICS", "TelegramHub"); }
		}

		public static string ConfigPath
		{
			get { return Path.Combine(DataFolder, "hub.json"); }
		}

		public static HubConfig Load()
		{
			try
			{
				if (File.Exists(ConfigPath))
				{
					HubConfig c = Json.Deserialize<HubConfig>(File.ReadAllText(ConfigPath, Encoding.UTF8));
					if (c != null)
					{
						if (c.AllowedUserIds == null) { c.AllowedUserIds = new List<long>(); }
						if (c.Topics == null) { c.Topics = new Dictionary<string, string>(); }
						return c;
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
			{
				HubLog.Error("hub.json read failed : " + ex.Message);
			}
			return new HubConfig();
		}

		//// 임시 파일 → 교체(부분 쓰기 없음)
		public void Save()
		{
			lock (Sync)
			{
				Directory.CreateDirectory(DataFolder);
				string tmp = ConfigPath + ".tmp";
				File.WriteAllText(tmp, Json.Serialize(this), new UTF8Encoding(false));
				if (File.Exists(ConfigPath)) { File.Replace(tmp, ConfigPath, null); } else { File.Move(tmp, ConfigPath); }
			}
		}

		public void SetToken(string token)
		{
			byte[] p = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
			TokenProtected = Convert.ToBase64String(p);
			_cachedToken = null;
		}

		[ScriptIgnore]
		public string Token
		{
			get
			{
				if (_cachedToken != null) { return _cachedToken; }
				if (string.IsNullOrEmpty(TokenProtected)) { return null; }
				try
				{
					_cachedToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), Entropy, DataProtectionScope.CurrentUser));
					_lastToken = _cachedToken;
					return _cachedToken;
				}
				catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
				{
					HubLog.Error("token could not be unprotected : " + ex.GetType().Name);
					return null;
				}
			}
		}

		private string _cachedToken;
		private static string _lastToken;
		private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SDOPTICS.SdoTelegramHub.v1");

		//// 로그 · 오류 문구에 토큰이 섞이지 않게(HTTP 오류 메시지에 URL 이 들어갈 수 있다)
		public static string MaskToken(string s)
		{
			if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(_lastToken)) { return s; }
			return s.Replace(_lastToken, "<token>");
		}

		//// 설정 · 토픽 사전은 이 잠금 아래에서만 읽고 쓴다(코드 리뷰 ② - 받기 · 보내기 스레드와 UI)
		[ScriptIgnore]
		public readonly object Sync = new object();

		public string NodeOfThread(long threadId)
		{
			lock (Sync)
			{
				string n;
				return Topics.TryGetValue(threadId.ToString(), out n) ? n : null;
			}
		}

		public long ThreadOfNode(string node)
		{
			lock (Sync)
			{
				foreach (KeyValuePair<string, string> kv in Topics)
				{
					if (string.Equals(kv.Value, node, StringComparison.OrdinalIgnoreCase)) { return long.Parse(kv.Key); }
				}
				return 0;
			}
		}

		public List<KeyValuePair<string, string>> TopicsSnapshot()
		{
			lock (Sync) { return new List<KeyValuePair<string, string>>(Topics); }
		}

		//// 노드 이름 규칙(§4-0) : 대문자 · 숫자 · 하이픈, 16 자 이하
		public static bool IsValidNode(string node)
		{
			return node != null && System.Text.RegularExpressions.Regex.IsMatch(node, "^[A-Z0-9][A-Z0-9-]{0,15}$");
		}
	}
}
