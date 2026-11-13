using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FTeleHub
{
	//// 허브 장부(H3) - 사건을 덧붙이기만 하는 JSON Lines. 쓸 때마다 Flush(true) 로 디스크에 확정한다.
	//// 사건 : received(본문 포함) · held(일시정지 중 수신) · delivered · dropped · noreply
	////        보낸 보고 ID 는 sent_ids.txt 에 영구 보관(H5)
	//// 모든 공개 메서드는 _sync 로 줄 세운다 - 받기 · 보내기 스레드와 UI 가 함께 쓴다(코드 리뷰 ②).
	internal sealed class LedgerRecord
	{
		public string ev;		//// received | held | delivered | dropped | noreply
		public string id;		//// tg-<update_id> 또는 tg-<n>-cancel
		public string kind;		//// request | cancel
		public string node;
		public string text;		//// 사용자 글 전체(received · held)
		public long from;
		public long chat;
		public long thread;
		public long msg;		//// 사용자 원글 메시지 ID(답글 대상)
		public string ts;		//// 받은 시각(로컬, yyyy-MM-dd HH:mm)
		public string exp;		//// 만료(시작 가능 기한)
		public string target;	//// cancel 이 가리키는 원래 tg ID
		public string commit;	//// delivered 커밋
	}

	internal sealed class Ledger
	{
		private readonly object _sync = new object();
		private readonly string _path;
		private readonly string _sentPath;
		private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
		private readonly Dictionary<string, LedgerRecord> _items = new Dictionary<string, LedgerRecord>(StringComparer.Ordinal);
		private readonly Dictionary<string, string> _state = new Dictionary<string, string>(StringComparer.Ordinal);
		private readonly HashSet<string> _sent = new HashSet<string>(StringComparer.Ordinal);
		private readonly Dictionary<long, string> _byMsg = new Dictionary<long, string>();
		public bool Corrupt { get; private set; }

		public Ledger(string folder)
		{
			Directory.CreateDirectory(folder);
			_path = Path.Combine(folder, "ledger.jsonl");
			_sentPath = Path.Combine(folder, "sent_ids.txt");
			Load();
		}

		private void Load()
		{
			if (File.Exists(_path))
			{
				byte[] raw = File.ReadAllBytes(_path);
				string all = new UTF8Encoding(false).GetString(raw);
				string[] lines = all.Split('\n');
				long goodBytes = 0;			//// 마지막 정상 줄 끝(개행 포함)까지의 바이트 수
				long pos = 0;
				for (int i = 0; i < lines.Length; i++)
				{
					string line = lines[i];
					bool hasNewline = i < lines.Length - 1;
					long lineBytes = new UTF8Encoding(false).GetByteCount(line) + (hasNewline ? 1 : 0);
					if (line.Trim().Length == 0)
					{
						pos += lineBytes;
						if (hasNewline) { goodBytes = pos; }
						continue;
					}
					LedgerRecord r = null;
					try { r = _json.Deserialize<LedgerRecord>(line); }
					catch (ArgumentException) { r = null; }
					catch (InvalidOperationException) { r = null; }
					bool last = true;
					for (int k = i + 1; k < lines.Length; k++) { if (lines[k].Trim().Length > 0) { last = false; break; } }
					if (r == null || r.id == null || r.ev == null || (hasNewline == false && last))
					{
						if (last)
						{
							//// 마지막 줄 손상(쓰는 중 종료) - 그 줄을 잘라 내 다음 기록이 붙어 망가지지 않게 한다(코드 리뷰 ①)
							HubLog.Warn("ledger : last line incomplete - truncated (H3)");
							break;
						}
						Corrupt = true;
						HubLog.Error("ledger : line " + (i + 1) + " broken - intake stopped");
						pos += lineBytes;
						continue;
					}
					Apply(r);
					pos += lineBytes;
					goodBytes = pos;
				}
				if (Corrupt == false && goodBytes < raw.Length)
				{
					using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read))
					{
						fs.SetLength(goodBytes);
						fs.Flush(true);
					}
				}
			}
			if (File.Exists(_sentPath))
			{
				foreach (string s in File.ReadAllLines(_sentPath, Encoding.UTF8))
				{
					if (s.Trim().Length > 0) { _sent.Add(s.Trim()); }
				}
			}
		}

		private void Apply(LedgerRecord r)
		{
			if (r.ev == "received" || r.ev == "held")
			{
				_items[r.id] = r;
				if (r.msg > 0 && r.kind == "request") { _byMsg[r.msg] = r.id; }
			}
			_state[r.id] = r.ev;
			if (r.ev == "delivered" && _items.ContainsKey(r.id)) { _items[r.id].commit = r.commit; }
		}

		public void Append(params LedgerRecord[] recs)
		{
			lock (_sync)
			{
				using (FileStream fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
				{
					foreach (LedgerRecord r in recs)
					{
						byte[] b = new UTF8Encoding(false).GetBytes(_json.Serialize(r) + "\n");
						fs.Write(b, 0, b.Length);
					}
					fs.Flush(true);
				}
				foreach (LedgerRecord r in recs) { Apply(r); }
			}
		}

		public string StateOf(string id)
		{
			lock (_sync)
			{
				string s;
				return _state.TryGetValue(id, out s) ? s : null;
			}
		}

		public LedgerRecord Get(string id)
		{
			lock (_sync)
			{
				LedgerRecord r;
				return _items.TryGetValue(id, out r) ? r : null;
			}
		}

		public string IdOfMessage(long msg)
		{
			lock (_sync)
			{
				string id;
				return _byMsg.TryGetValue(msg, out id) ? id : null;
			}
		}

		private List<LedgerRecord> InState(string st)
		{
			lock (_sync)
			{
				List<LedgerRecord> list = new List<LedgerRecord>();
				foreach (KeyValuePair<string, LedgerRecord> kv in _items)
				{
					string s;
					if (_state.TryGetValue(kv.Key, out s) && s == st) { list.Add(kv.Value); }
				}
				return list;
			}
		}

		public List<LedgerRecord> Pending() { return InState("received"); }
		public List<LedgerRecord> Held() { return InState("held"); }
		public List<LedgerRecord> Delivered() { return InState("delivered"); }

		public bool WasSent(string rid)
		{
			lock (_sync) { return _sent.Contains(rid); }
		}

		//// 디스크에 확정한 뒤에 메모리를 바꾼다(코드 리뷰 ⑧) - 쓰기 실패면 예외, 메모리는 그대로
		public void MarkSent(IEnumerable<string> rids)
		{
			lock (_sync)
			{
				List<string> add = new List<string>();
				foreach (string r in rids) { if (_sent.Contains(r) == false && add.Contains(r) == false) { add.Add(r); } }
				if (add.Count == 0) { return; }
				using (FileStream fs = new FileStream(_sentPath, FileMode.Append, FileAccess.Write, FileShare.Read))
				{
					byte[] b = new UTF8Encoding(false).GetBytes(string.Join("\n", add) + "\n");
					fs.Write(b, 0, b.Length);
					fs.Flush(true);
				}
				foreach (string r in add) { _sent.Add(r); }
			}
		}
	}
}
