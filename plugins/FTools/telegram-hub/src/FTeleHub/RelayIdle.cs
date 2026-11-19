using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FTeleHub
{
	//// 설계 §11 · §11-1 미처리 릴레이 알림(세션 공백). 허브는 릴레이 파일에 쓰지 않는다(I8) - 알림은 텔레그램만.
	//// 판정은 PC 시계에 기대지 않는다(J1) : 경과 시간 = 허브가 처음 본 시각, 처리 여부 = 커밋 조상 관계.
	internal sealed class IdleItem
	{
		public string Key;			//// 커밋 해시 | 파일(J5)
		public string Hash;
		public string File;
		public string Relay;		//// 받는 릴레이 이름(Y)
		public string FirstSeen;	//// 허브 시각(o 형식)
		public List<string> Notified = new List<string>();
		public string AllNotifiedAt;
	}

	internal sealed class IdleState
	{
		public string LastCommit;					//// 기준점 - 이 커밋과 그 조상은 검사 끝
		public string ScanTarget;					//// [J9] 지금 검사 중인 범위의 목표 머리(null = 검사 중인 범위 없음)
		public List<string> ScanQueue = new List<string>();	//// [J9] 그 범위에서 아직 보지 않은 커밋(부모 먼저)
		public string CheckingCommit;				//// [코드 리뷰 R4] 조상 판정 도중 마감으로 멈춘 커밋
		public List<string> CheckedKeys = new List<string>();	//// 그 커밋에 대해 이미 판정한 항목 키(조상 아님) - 다음 주기에 다시 보지 않는다
		public List<IdleItem> Items = new List<IdleItem>();
		public List<string> TgSent = new List<string>();
		public Dictionary<string, string> TgFirst = new Dictionary<string, string>();	//// [코드 리뷰 R3] 지시를 "전달됨" 으로 처음 본 허브 시각
	}

	internal sealed class IdleBudgetException : Exception
	{
	}

	internal sealed class RelayIdle
	{
		private static readonly Regex ActionHeading = new Regex(@"^## \[[^\]]+\] (요청|신호)", RegexOptions.Compiled);
		private static readonly Regex ChannelFile = new Regex(@"^(?<from>[A-Za-z0-9-]+)_to_(?<to>[A-Za-z0-9-]+)\.md$", RegexOptions.Compiled);
		private static readonly Regex UserFile = new Regex(@"^(?<node>[A-Z0-9][A-Z0-9-]*)_to_user\.md$", RegexOptions.Compiled);
		internal const int BudgetMs = 15000;				//// J10 검사 한 번의 시간 상한
		internal const int SendReserveMs = 5000;			//// 알림 하나의 제한(전용 텔레그램 클라이언트 5 s, 429 는 기다리지 않고 다음 주기) - 이만큼 남아야 보낸다
		internal const int MaxIdleMinutes = 1440;			//// J7
		private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

		private readonly string _statePath;
		private IdleState _s;

		public RelayIdle(string dataFolder)
		{
			_statePath = Path.Combine(dataFolder, "idle_state.json");
			_s = Load(_statePath);
		}

		internal IdleState State { get { return _s; } }

		//// ===== 판정 (단위 시험 대상) =====

		internal static bool IsActionHeading(string line)
		{
			return line != null && ActionHeading.IsMatch(line);
		}

		//// "X_to_Y.md" → (X, Y). 텔레그램 파일(user 쪽) · 같은 이름 · 형식 밖은 null.
		internal static Tuple<string, string> ParseChannel(string fileName)
		{
			Match m = ChannelFile.Match(fileName ?? string.Empty);
			if (m.Success == false) { return null; }
			string from = m.Groups["from"].Value, to = m.Groups["to"].Value;
			if (string.Equals(from, "user", StringComparison.OrdinalIgnoreCase) || string.Equals(to, "user", StringComparison.OrdinalIgnoreCase)) { return null; }
			if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { return null; }
			return Tuple.Create(from, to);
		}

		//// git show --unified=0 출력 → 파일(이름만) 별 더해진 요청 · 신호 머리줄 수. 바뀐 파일은 모두 키로 들어간다(0 건 포함).
		internal static Dictionary<string, int> ParseShow(string diff)
		{
			Dictionary<string, int> files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			string cur = null;
			foreach (string raw in (diff ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
			{
				if (raw.StartsWith("+++ ", StringComparison.Ordinal))
				{
					string p = raw.Substring(4).Trim();
					if (p.StartsWith("\"", StringComparison.Ordinal) && p.EndsWith("\"", StringComparison.Ordinal) && p.Length >= 2) { p = p.Substring(1, p.Length - 2); }
					cur = p == "/dev/null" ? null : Path.GetFileName(p.StartsWith("b/", StringComparison.Ordinal) ? p.Substring(2) : p);
					if (cur != null && files.ContainsKey(cur) == false) { files[cur] = 0; }
					continue;
				}
				if (raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("diff --git ", StringComparison.Ordinal))
				{
					if (raw.StartsWith("diff --git ", StringComparison.Ordinal)) { cur = null; }
					continue;
				}
				if (cur != null && raw.StartsWith("+## [", StringComparison.Ordinal) && IsActionHeading(raw.Substring(1)))
				{
					files[cur] = files[cur] + 1;
				}
			}
			return files;
		}

		//// 바뀐 파일을 쓴 쪽의 릴레이 이름(J6) : X_to_*.md → X, <노드>_to_user.md → 그 노드가 속한 릴레이. 모르면 null.
		internal static string WriterRelay(string fileName, Dictionary<string, List<string>> map)
		{
			Tuple<string, string> ch = ParseChannel(fileName);
			if (ch != null) { return ch.Item1; }
			Match m = UserFile.Match(fileName ?? string.Empty);
			if (m.Success)
			{
				string node = m.Groups["node"].Value;
				foreach (KeyValuePair<string, List<string>> kv in map)
				{
					if (kv.Value != null && kv.Value.Any(n => string.Equals(n, node, StringComparison.OrdinalIgnoreCase))) { return kv.Key; }
				}
			}
			return null;
		}

		internal static int ClampIdle(int minutes)
		{
			return minutes <= 0 ? 0 : Math.Min(minutes, MaxIdleMinutes);
		}

		internal static string RelayMessage(IdleItem it, int minutes)
		{
			string shortHash = it.Hash.Length > 7 ? it.Hash.Substring(0, 7) : it.Hash;
			return "🛰 [허브] 미처리 - " + it.File + " 의 요청 · 신호(커밋 " + shortHash + ") 를 허브가 본 지 " + minutes + " 분이 지났는데 받는 PC 의 릴레이 커밋이 없습니다. 세션이 없거나 감시가 멈춘 것 같습니다 - 세션을 열어 주세요.";
		}

		internal static string TgMessage(string id, int minutes)
		{
			return "🛰 [허브] 미처리 - " + id + " 를 전달한 지 " + minutes + " 분이 지났는데 읽음 보고가 없습니다. 그 PC 에 세션이 없거나 감시가 멈춘 것 같습니다 - 세션을 열어 주세요.";
		}

		//// ===== 검사 =====

		//// 시험 · 단순 호출용 : 검사(깃) 와 알림을 한 번에. 허브는 ScanRelay(깃 잠금 안) 와 NotifyRelay(잠금 밖) 를 나눠 부른다(코드 리뷰 R1).
		public void CheckRelay(Func<string, int, string> run, string relDir, string headRef, Dictionary<string, List<string>> map, int idleMinutes, DateTime now, Func<string, string, bool> send, int budgetMs = BudgetMs)
		{
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			if (ScanRelay(run, relDir, headRef, map, idleMinutes, now, sw, budgetMs))
			{
				NotifyRelay(map, idleMinutes, now, send, sw, budgetMs);
			}
		}

		//// run(인수, 제한 ms) : git 명령(실패 · 시간 초과면 예외). headRef : 비교할 머리(허브는 origin/<branch>). map : 릴레이 이름 → 노드(스냅샷).
		//// sw · budgetMs : 이번 검사 전체(동기화 · 검사 · 알림) 의 마감(J10). 깃 잠금을 쥔 채 부른다.
		//// 반환 : 이번에 머리까지 다 봤으면 true(대기 목록 없음, 예외 · 마감 없음). 아니면 호출자는 알리지 않는다(코드 리뷰 R4 5 회차 - 지난 상태로 오보 방지).
		public bool ScanRelay(Func<string, int, string> run, string relDir, string headRef, Dictionary<string, List<string>> map, int idleMinutes, DateTime now, System.Diagnostics.Stopwatch sw, int budgetMs)
		{
			if (ClampIdle(idleMinutes) <= 0) { return false; }
			bool complete = false;
			//// [코드 리뷰 R1] 깃 명령마다 남은 시간만 준다. 마감이 지났으면 명령을 시작하지 않고 멈춘다(진행은 대기 목록에 남는다).
			Func<int> left = () =>
			{
				int rest = budgetMs - (int)sw.ElapsedMilliseconds;
				if (rest <= 0) { throw new IdleBudgetException(); }
				return rest;
			};
			try
			{
				Scan(run, relDir, headRef, map, now, sw, budgetMs, left);
				//// [코드 리뷰 R4 6 회차] 목록이 비었어도 이전 범위만 마쳤을 수 있다 - 기준점이 지금 머리와 같을 때만 "다 봤다"
				complete = _s.ScanQueue.Count == 0 && string.Equals(run("rev-parse " + headRef, left()).Trim(), _s.LastCommit, StringComparison.Ordinal);
			}
			catch (IdleBudgetException)
			{
				HubLog.Info("idle : budget used up - " + _s.ScanQueue.Count + " commit(s) left for the next check");
			}
			finally
			{
				Save();		//// 시간 초과 · 실패여도 지금까지 본 진행을 남긴다(J10)
			}
			return complete;
		}

		//// 알림 - 깃 잠금 밖에서. 마감이 지나면 남은 노드는 다음 주기에(J5 · J10). send 하나의 시간은 텔레그램 클라이언트 제한을 따른다.
		public void NotifyRelay(Dictionary<string, List<string>> map, int idleMinutes, DateTime now, Func<string, string, bool> send, System.Diagnostics.Stopwatch sw, int budgetMs)
		{
			idleMinutes = ClampIdle(idleMinutes);
			if (idleMinutes <= 0 || _s.ScanQueue.Count > 0)
			{
				return;		//// J11 밀린 커밋이 남았으면 알리지 않는다
			}
			Notify(map, idleMinutes, now, send, sw, budgetMs);
			Save();
		}

		private void Scan(Func<string, int, string> run, string relDir, string headRef, Dictionary<string, List<string>> map, DateTime now, System.Diagnostics.Stopwatch sw, int budgetMs, Func<int> left)
		{
			string pathSpec = " -- \"" + relDir.TrimEnd('/') + "\"";
			if (_s.ScanQueue.Count == 0)
			{
				string head = run("rev-parse " + headRef, left()).Trim();
				if (string.IsNullOrEmpty(_s.LastCommit))
				{
					_s.LastCommit = head;		//// J2 최초 기준점 - 설치 이전 항목은 알리지 않는다
					_s.ScanTarget = null;
					HubLog.Info("idle : baseline " + Short(head));
					return;
				}
				if (Exists(run, _s.LastCommit, left()) == false)
				{
					HubLog.Warn("idle : last checked commit " + Short(_s.LastCommit) + " is gone - baseline reset to " + Short(head));
					_s.LastCommit = head;
					_s.ScanTarget = null;
					return;
				}
				if (head == _s.LastCommit)
				{
					return;
				}
				//// J9 범위를 고정한다 - 이 목록을 다 본 뒤에만 기준점이 head 로 간다
				_s.ScanQueue = run("log --topo-order --reverse --format=%H " + _s.LastCommit + ".." + head + pathSpec, left())
					.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
				_s.ScanTarget = head;
			}
			int processed = 0;
			while (_s.ScanQueue.Count > 0 && (processed == 0 || sw.ElapsedMilliseconds < budgetMs))		//// 한 번에 최소 1 개 - 상한이 짧아도 앞으로 간다
			{
				processed++;
				string c = _s.ScanQueue[0];
				Dictionary<string, int> files = ParseShow(run("show --unified=0 --format= " + c + pathSpec, left()));
				//// a) 이 커밋을 쓴 쪽 앞으로 열린 항목 중 이 커밋의 조상인 것을 닫는다(같은 커밋 안의 항목은 닫지 않는다)
				HashSet<string> writers = new HashSet<string>(files.Keys.Select(f => WriterRelay(f, map)).Where(w => w != null), StringComparer.OrdinalIgnoreCase);
				if (writers.Count > 0)
				{
					if (_s.CheckingCommit != c)
					{
						_s.CheckingCommit = c;
						_s.CheckedKeys = new List<string>();
					}
					foreach (IdleItem it in _s.Items.Where(i => writers.Contains(i.Relay) && i.Hash != c && _s.CheckedKeys.Contains(i.Key) == false).ToList())
					{
						if (sw.ElapsedMilliseconds >= budgetMs)
						{
							return;		//// [코드 리뷰 R1 · R4] 마감 - 첫 커밋이라도 멈춘다. 닫은 항목은 빠지고 조상 아님으로 판정한 항목은 CheckedKeys 에 남아 다음 주기에 이어서
						}
						if (IsAncestor(run, it.Hash, c, left()))
						{
							_s.Items.Remove(it);
						}
						else
						{
							_s.CheckedKeys.Add(it.Key);
						}
					}
				}
				//// b) 이 커밋이 더한 요청 · 신호 → 항목(키 = 커밋 | 파일)
				foreach (KeyValuePair<string, int> kv in files)
				{
					if (kv.Value == 0) { continue; }
					Tuple<string, string> ch = ParseChannel(kv.Key);
					if (ch == null || NodesOf(map, ch.Item2).Count == 0) { continue; }
					string key = c + "|" + kv.Key;
					if (_s.Items.Any(i => i.Key == key)) { continue; }
					_s.Items.Add(new IdleItem { Key = key, Hash = c, File = kv.Key, Relay = ch.Item2, FirstSeen = now.ToString("o", CultureInfo.InvariantCulture) });
				}
				_s.ScanQueue.RemoveAt(0);
				_s.CheckingCommit = null;
				_s.CheckedKeys = new List<string>();
			}
			if (_s.ScanQueue.Count == 0 && _s.ScanTarget != null)
			{
				_s.LastCommit = _s.ScanTarget;		//// J9 범위를 다 봤다
				_s.ScanTarget = null;
			}
		}

		private void Notify(Dictionary<string, List<string>> map, int idleMinutes, DateTime now, Func<string, string, bool> send, System.Diagnostics.Stopwatch sw, int budgetMs)
		{
			//// 알림 - 노드마다 성공을 기록, 실패한 노드만 다음 주기에(J5)
			foreach (IdleItem it in _s.Items)
			{
				if (sw.ElapsedMilliseconds + SendReserveMs > budgetMs) { break; }		//// J10 - 남은 항목은 다음 주기
				DateTime seen;
				if (DateTime.TryParse(it.FirstSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out seen) == false) { seen = now; it.FirstSeen = now.ToString("o", CultureInfo.InvariantCulture); }
				int mins = (int)(now - seen).TotalMinutes;
				if (mins < idleMinutes) { continue; }
				List<string> nodes = NodesOf(map, it.Relay);
				foreach (string n in nodes)
				{
					if (it.Notified.Contains(n)) { continue; }
					if (sw.ElapsedMilliseconds + SendReserveMs > budgetMs) { break; }		//// [코드 리뷰 R1] 노드마다 - 보내기 하나의 제한(SendReserveMs) 이 남은 시간 안일 때만
					if (send(n, RelayMessage(it, mins)))
					{
						it.Notified.Add(n);
						HubLog.Info("idle : " + it.File + " " + Short(it.Hash) + " -> " + n);
					}
				}
				if (it.AllNotifiedAt == null && nodes.All(n => it.Notified.Contains(n))) { it.AllNotifiedAt = now.ToString("o", CultureInfo.InvariantCulture); }
			}
			//// 모두 알린 뒤 48 시간이 지난 항목은 닫는다(J2)
			_s.Items.RemoveAll(i =>
			{
				DateTime t;
				return i.AllNotifiedAt != null && DateTime.TryParse(i.AllNotifiedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) && (now - t).TotalHours >= 48;
			});
		}

		public bool TgWasSent(string id) { return _s.TgSent.Contains(id); }

		public void MarkTgSent(string id)
		{
			if (_s.TgSent.Contains(id)) { return; }
			_s.TgSent.Add(id);
			Save();		//// 보낸 뒤에는 바로 남긴다(재전송 방지) - 보내기 자체가 남은 시간 안에서만 일어난다
		}

		//// [코드 리뷰 R3] 전달된 지시를 처음 본 허브 시각(없으면 지금으로 기록) - 경과 시간은 여기부터(수신 뒤 전달 대기는 빼고).
		//// 메모리에만 기록한다 - 호출자가 한 번에 SaveIfDirty(코드 리뷰 R1 3 회차 : 지시마다 파일을 쓰지 않는다).
		public DateTime TgFirstSeen(string id, DateTime now)
		{
			string v;
			DateTime t;
			if (_s.TgFirst.TryGetValue(id, out v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) { return t; }
			_s.TgFirst[id] = now.ToString("o", CultureInfo.InvariantCulture);
			_dirty = true;
			return now;
		}

		private bool _dirty;

		public void SaveIfDirty()
		{
			if (_dirty)
			{
				_dirty = false;
				Save();
			}
		}

		//// [코드 리뷰 R2] 개수로 자르지 않는다 - 지금 "전달됨" 목록에 없는 지시(보고 · 만료 · 취소로 대상에서 빠짐) 의 기록만 지운다.
		public void PruneTg(HashSet<string> deliveredIds)
		{
			int before = _s.TgSent.Count + _s.TgFirst.Count;
			_s.TgSent.RemoveAll(id => deliveredIds.Contains(id) == false);
			foreach (string k in _s.TgFirst.Keys.Where(k => deliveredIds.Contains(k) == false).ToList()) { _s.TgFirst.Remove(k); }
			if (_s.TgSent.Count + _s.TgFirst.Count != before) { _dirty = true; }
		}

		private static List<string> NodesOf(Dictionary<string, List<string>> map, string relay)
		{
			foreach (KeyValuePair<string, List<string>> kv in map)
			{
				if (string.Equals(kv.Key, relay, StringComparison.OrdinalIgnoreCase) && kv.Value != null) { return kv.Value.Where(HubConfig.IsValidNode).ToList(); }
			}
			return new List<string>();
		}

		//// 시간 초과는 존재 판정이 아니다 - 그대로 올려 이번 검사를 멈춘다(기준점을 잘못 다시 잡지 않게)
		private static bool Exists(Func<string, int, string> run, string hash, int timeoutMs)
		{
			try { run("cat-file -e " + hash + "^{commit}", timeoutMs); return true; }
			catch (GitException ex) when (ex.Message.Contains("timed out") == false) { return false; }
		}

		//// 조상이 아니면 git 이 exit 1(GitException). 시간 초과는 올려 이번 검사를 멈춘다 - 그 커밋은 대기 목록에 남아 다음 주기에 다시.
		private static bool IsAncestor(Func<string, int, string> run, string a, string b, int timeoutMs)
		{
			try { run("merge-base --is-ancestor " + a + " " + b, timeoutMs); return true; }
			catch (GitException ex) when (ex.Message.Contains("timed out") == false) { return false; }
		}

		private static string Short(string h) { return string.IsNullOrEmpty(h) ? "-" : (h.Length > 7 ? h.Substring(0, 7) : h); }

		private static IdleState Load(string path)
		{
			try
			{
				if (File.Exists(path))
				{
					IdleState s = Json.Deserialize<IdleState>(File.ReadAllText(path, Encoding.UTF8));
					if (s != null)
					{
						if (s.Items == null) { s.Items = new List<IdleItem>(); }
						if (s.TgSent == null) { s.TgSent = new List<string>(); }
						if (s.ScanQueue == null) { s.ScanQueue = new List<string>(); }
						if (s.TgFirst == null) { s.TgFirst = new Dictionary<string, string>(); }
						if (s.CheckedKeys == null) { s.CheckedKeys = new List<string>(); }
						if (s.ScanQueue.Count == 0) { s.ScanTarget = null; }
						foreach (IdleItem i in s.Items) { if (i.Notified == null) { i.Notified = new List<string>(); } }
						return s;
					}
				}
			}
			catch (Exception ex)
			{
				HubLog.Warn("idle : state read failed - " + ex.Message + " (starting from a new baseline)");
			}
			return new IdleState();
		}

		private void Save()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
				string tmp = _statePath + ".tmp";
				File.WriteAllText(tmp, Json.Serialize(_s), new UTF8Encoding(false));
				if (File.Exists(_statePath)) { File.Replace(tmp, _statePath, null); } else { File.Move(tmp, _statePath); }
			}
			catch (Exception ex)
			{
				HubLog.Warn("idle : state write failed - " + ex.Message);
			}
		}
	}
}
