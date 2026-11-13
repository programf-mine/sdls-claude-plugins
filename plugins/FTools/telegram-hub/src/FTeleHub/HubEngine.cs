using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FTeleHub
{
	//// 허브 처리 흐름(H2 · H3 · H5 · H7 · H8). 스레드 둘 : 받기(롱폴링) · 보내기(보고 조회 · 전달 재시도).
	//// 깃 작업은 _gitLock 하나로 줄 세운다(전용 클론 하나). 설정 · 토픽은 _c.Sync, 장부는 Ledger 내부 잠금.
	internal sealed class HubEngine
	{
		private readonly HubConfig _c;
		private readonly Ledger _ledger;
		private readonly GitRelay _git;
		private readonly object _gitLock = new object();
		private readonly object _seenLock = new object();
		private volatile bool _running;
		private Thread _inbound, _outbound;
		private volatile TelegramClient _tg;
		private string _lastHead;
		private volatile bool _reportsDirty = true;		//// 보내지 못한 보고가 남았을 수 있다 - 커밋 변경과 무관하게 다시 본다(코드 리뷰 ⑨)
		private DateTime _lastTelegramOk = DateTime.MinValue, _lastGithubOk = DateTime.MinValue, _lastNoReplyCheck = DateTime.MinValue;
		private DateTime _lastTick = DateTime.Now;
		private readonly DateTime _started = DateTime.Now;
		private readonly HashSet<string> _tgSeen = new HashSet<string>(StringComparer.Ordinal);		//// 회신(무엇이든) 받은 지시
		private readonly HashSet<string> _tgDone = new HashSet<string>(StringComparer.Ordinal);		//// 완료 · 취소됨 받은 지시
		private volatile bool _announcePending = true;
		private DateTime? _announceSince;			//// 재개 알림의 멈춤 시작(시작 때는 저장된 LastAlive, 절전 복귀 때는 마지막 동작 시각)
		public volatile bool DeliverFailing;
		public volatile bool HasNew;
		public string LastReceive = "-";
		public event Action Changed;

		public HubEngine(HubConfig c)
		{
			_c = c;
			_ledger = new Ledger(HubConfig.DataFolder);
			_git = new GitRelay(c);
			DateTime last;
			if (DateTime.TryParse(c.LastAlive, out last)) { _announceSince = last; }		//// 보내기 스레드가 덮어쓰기 전에 읽어 둔다(코드 리뷰 ⑩)
		}

		public HubConfig Config { get { return _c; } }
		public Ledger Ledger { get { return _ledger; } }

		public HubState State
		{
			get
			{
				bool tokenMissing = string.IsNullOrEmpty(_c.TokenProtected);
				TimeSpan tg = DateTime.Now - (_lastTelegramOk == DateTime.MinValue ? _started : _lastTelegramOk);
				TimeSpan gh = DateTime.Now - (_lastGithubOk == DateTime.MinValue ? _started : _lastGithubOk);
				if (tokenMissing == false && (tg.TotalMinutes > 5 || gh.TotalMinutes > 5)) { return HubState.Offline; }
				if (_c.Paused) { return HubState.Paused; }
				if (tokenMissing || _ledger.Corrupt || DeliverFailing || _ledger.Held().Count > 0) { return HubState.Warn; }
				return HasNew ? HubState.NewItem : HubState.Normal;
			}
		}

		public string StatusLine
		{
			get
			{
				string s;
				switch (State)
				{
					case HubState.Offline: s = "끊김"; break;
					case HubState.Paused: s = "일시정지"; break;
					case HubState.Warn:
						if (string.IsNullOrEmpty(_c.TokenProtected)) { s = "토큰 없음"; }
						else if (_ledger.Corrupt) { s = "장부 오류"; }
						else if (DeliverFailing) { s = "전달 재시도 중"; }
						else { s = "보류 " + _ledger.Held().Count + " 건"; }
						break;
					case HubState.NewItem: s = "새 글"; break;
					default: s = "정상"; break;
				}
				return s + " · 마지막 수신 " + LastReceive + " · 미전달 " + _ledger.Pending().Count;
			}
		}

		//// 전달됐지만 완료 · 취소됨 회신이 아직 없는 지시 수(코드 리뷰 ⑪)
		public int AwaitingCount(string node)
		{
			lock (_seenLock)
			{
				return _ledger.Delivered().Count(r => r.node == node && r.kind == "request" && _tgDone.Contains(r.id) == false);
			}
		}

		public void Start()
		{
			_running = true;
			_inbound = new Thread(InboundLoop) { IsBackground = true, Name = "hub-inbound" };
			_outbound = new Thread(OutboundLoop) { IsBackground = true, Name = "hub-outbound" };
			_inbound.Start();
			_outbound.Start();
		}

		public void Stop() { _running = false; }

		//// 절전 복귀(H8) - 살아 있는 프로세스도 재개 알림을 보낸다
		public void OnResume()
		{
			_announceSince = _lastTick;
			_announcePending = true;
			_lastTelegramOk = DateTime.MinValue;
			_reportsDirty = true;
		}

		public void Notify()
		{
			Action a = Changed;
			if (a != null) { a(); }
		}

		public void SaveConfig() { _c.Save(); }

		private TelegramClient Tg()
		{
			TelegramClient t = _tg;
			if (t != null) { return t; }
			string tok = _c.Token;
			if (string.IsNullOrEmpty(tok)) { return null; }
			t = new TelegramClient(tok);
			_tg = t;
			return t;
		}

		public void ResetClient()
		{
			TelegramClient t = _tg;
			_tg = null;
			_reportsDirty = true;
			if (t != null) { t.Dispose(); }
		}

		//// ===== 받기 =====
		private void InboundLoop()
		{
			int backoff = 2;
			while (_running)
			{
				try
				{
					TelegramClient tg = Tg();
					if (tg == null || _ledger.Corrupt)
					{
						Thread.Sleep(3000);
						Notify();
						continue;
					}
					if (_announcePending && _c.ChatId != 0 && Announce(tg))
					{
						_announcePending = false;		//// 보냈을 때만 소진(코드 리뷰 R2)
					}
					List<Dictionary<string, object>> ups = tg.GetUpdates(_c.LastUpdateId + 1, 50);
					_lastTelegramOk = DateTime.Now;
					backoff = 2;
					if (ups.Count == 0)
					{
						Notify();
						continue;
					}
					List<Reply> replies = new List<Reply>();
					bool anyReceived = false;
					long maxId = _c.LastUpdateId;
					foreach (Dictionary<string, object> u in ups)
					{
						long uid = J.Long(u, "update_id");
						if (uid > maxId) { maxId = uid; }
						if (uid <= _c.LastUpdateId) { continue; }
						//// 업데이트마다 바로 장부에 확정한다 - 같은 배치의 뒤 명령(/cancel · /drop · /deliver)이 앞 글의 상태를 본다(코드 리뷰 ⑦)
						List<LedgerRecord> recs = new List<LedgerRecord>();
						Handle(uid, u, recs, replies);
						if (recs.Count > 0)
						{
							_ledger.Append(recs.ToArray());
							if (recs.Any(x => x.ev == "received")) { anyReceived = true; }
						}
					}
					//// H3 2 ~ 3 : 배치 전체를 장부에 확정한 뒤에만 offset 을 넘긴다
					lock (_c.Sync)
					{
						_c.LastUpdateId = maxId;
						_c.Save();
					}
					foreach (Reply r in replies) { SendSafe(tg, r.Chat, r.Thread, r.Text, r.ReplyTo); }
					if (anyReceived)
					{
						LastReceive = DateTime.Now.ToString("HH:mm");
						Deliver();
					}
					Notify();
				}
				catch (RetryAfterException ex)
				{
					HubLog.Warn(ex.Message);
					Thread.Sleep(ex.Seconds * 1000 + 500);
				}
				catch (Exception ex)
				{
					//// 어떤 예외로도 받기 스레드가 죽지 않게 한다(코드 리뷰 ⑤)
					HubLog.Warn("inbound : " + ex.GetType().Name + " " + ex.Message);
					Thread.Sleep(backoff * 1000);
					backoff = Math.Min(backoff * 2, 300);
					Notify();
				}
			}
		}

		private struct Reply
		{
			public long Chat;
			public long Thread;
			public string Text;
			public long ReplyTo;
		}

		private void Handle(long uid, Dictionary<string, object> u, List<LedgerRecord> recs, List<Reply> replies)
		{
			Dictionary<string, object> edited = J.Obj(u, "edited_message");
			Dictionary<string, object> m = J.Obj(u, "message") ?? edited;
			if (m == null) { return; }
			Dictionary<string, object> chat = J.Obj(m, "chat");
			Dictionary<string, object> from = J.Obj(m, "from");
			long chatId = J.Long(chat, "id");
			long fromId = J.Long(from, "id");
			long msgId = J.Long(m, "message_id");
			long thread = J.Bool(m, "is_topic_message") ? J.Long(m, "message_thread_id") : 0;
			string text = J.Str(m, "text");
			bool userOk;
			lock (_c.Sync) { userOk = _c.AllowedUserIds.Contains(fromId); }
			bool allowed = chatId == _c.ChatId && userOk && J.Bool(from, "is_bot") == false && J.Obj(m, "sender_chat") == null;

			if (text != null && text.Trim().StartsWith("/whoami", StringComparison.Ordinal))
			{
				//// 설정용 - 허용 전에도 받은 그 대화로만 ID 를 알려 준다(코드 리뷰 ⑥)
				string t0 = "🛰 [허브] 사용자 ID " + fromId + " · 대화 ID " + chatId + " · 토픽 " + thread + (allowed ? " (허용됨)" : " (아직 허용 안 됨 - hub.json 에 추가)");
				replies.Add(new Reply { Chat = chatId, Thread = thread, ReplyTo = msgId, Text = t0 });
				return;
			}
			if (allowed == false)
			{
				HubLog.Info("ignored update " + uid + " (chat " + chatId + ", from " + fromId + ")");
				return;
			}
			Action<string> say = s => replies.Add(new Reply { Chat = chatId, Thread = thread, ReplyTo = msgId, Text = "🛰 [허브] " + s });
			if (edited != null)
			{
				say("편집한 글은 반영하지 않습니다. 새로 보내 주세요.");
				return;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				say("글자만 받습니다.");
				return;
			}

			string t = text.Trim();
			if (t.StartsWith("/", StringComparison.Ordinal))
			{
				Command(t, thread, msgId, m, recs, say);
				return;
			}

			//// 배치 중 꺼진 뒤 같은 update 를 다시 받으면 이미 장부에 있는 지시는 그대로 둔다(보류 상태를 덮어쓰지 않음 - 코드 리뷰 R1)
			if (_ledger.StateOf("tg-" + uid) != null)
			{
				HubLog.Info("update " + uid + " already in ledger - replay ignored");
				return;
			}
			string node = thread > 0 ? _c.NodeOfThread(thread) : null;
			if (node == null)
			{
				say(thread > 0 ? "등록되지 않은 토픽입니다. 이 토픽에서 /bind <노드 이름> 으로 등록하세요." : "노드 토픽에 써 주세요(일반 토픽은 전달하지 않습니다).");
				return;
			}
			DateTime now = DateTime.Now;
			bool paused = _c.Paused;
			LedgerRecord r = new LedgerRecord
			{
				ev = paused ? "held" : "received", id = "tg-" + uid, kind = "request", node = node, text = text,
				from = fromId, chat = chatId, thread = thread, msg = msgId,
				ts = now.ToString("yyyy-MM-dd HH:mm"), exp = now.AddHours(24).ToString("yyyy-MM-dd HH:mm")
			};
			recs.Add(r);
			if (paused) { say("일시정지 중 - 보류(" + r.id + "). /resume 뒤 /deliver " + r.id + " 로 보낼 수 있습니다."); }
		}

		private void Command(string t, long thread, long msgId, Dictionary<string, object> m, List<LedgerRecord> recs, Action<string> say)
		{
			string[] parts = t.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
			string cmd = parts[0].Split('@')[0].ToLowerInvariant();
			string arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;
			switch (cmd)
			{
				case "/bind":
				{
					string node = arg.ToUpperInvariant();
					if (thread <= 0)
					{
						say("노드 토픽 안에서 쓰세요.");
						return;
					}
					if (HubConfig.IsValidNode(node) == false)
					{
						say("노드 이름은 대문자 · 숫자 · 하이픈, 16 자 이하입니다(예 D1, D252-TM).");
						return;
					}
					lock (_c.Sync)
					{
						foreach (string k in _c.Topics.Where(kv => kv.Value == node).Select(kv => kv.Key).ToList()) { _c.Topics.Remove(k); }
						_c.Topics[thread.ToString()] = node;
						_c.Save();
					}
					_reportsDirty = true;
					say("이 토픽 = " + node + ". 그 PC 의 tg_node.txt 에 " + node + " 를 적어야 지시가 갑니다.");
					return;
				}
				case "/pause":
					lock (_c.Sync)
					{
						_c.Paused = true;
						_c.Save();
					}
					say("전달을 멈췄습니다. 이후 받은 글은 보류(held)합니다.");
					return;
				case "/resume":
				{
					lock (_c.Sync)
					{
						_c.Paused = false;
						_c.Save();
					}
					List<LedgerRecord> held = _ledger.Held();
					string tail = held.Count > 0 ? " 보류 " + held.Count + " 건은 자동으로 보내지 않습니다 : " + string.Join(", ", held.Select(h => h.id)) + " (/deliver 또는 /drop)" : string.Empty;
					say("전달을 다시 켭니다." + tail);
					return;
				}
				case "/deliver":
				case "/drop":
				{
					LedgerRecord h = _ledger.Get(arg);
					if (h == null || _ledger.StateOf(arg) != "held")
					{
						say(arg + " 는 보류 목록에 없습니다.");
						return;
					}
					if (cmd == "/drop")
					{
						recs.Add(new LedgerRecord { ev = "dropped", id = h.id });
						say(h.id + " 를 버렸습니다.");
						return;
					}
					LedgerRecord again = Clone(h);
					again.ev = "received";
					recs.Add(again);
					say(h.id + " 를 전달합니다.");
					return;
				}
				case "/cancel":
				{
					Dictionary<string, object> rep = J.Obj(m, "reply_to_message");
					string target = rep == null ? null : _ledger.IdOfMessage(J.Long(rep, "message_id"));
					if (target == null)
					{
						say("취소할 원래 지시 메시지에 답글로 /cancel 을 보내세요.");
						return;
					}
					LedgerRecord orig = _ledger.Get(target);
					if (_ledger.StateOf(target + "-cancel") != null)
					{
						say(target + " 의 취소 요청은 이미 받았습니다.");
						return;
					}
					if (_ledger.StateOf(target) == "dropped")
					{
						//// 보류 중 버린 지시(재처리 포함) - PC 는 받은 적이 없으므로 취소를 보내지 않는다(코드 리뷰 F1)
						say(target + " 는 이미 버린 지시입니다.");
						return;
					}
					if (_ledger.StateOf(target) == "held")
					{
						//// 아직 보내지 않은 보류 지시는 PC 에 갈 필요 없이 버린다
						recs.Add(new LedgerRecord { ev = "dropped", id = target });
						say(target + " 는 보류 중이라 보내지 않고 버렸습니다.");
						return;
					}
					DateTime now = DateTime.Now;
					recs.Add(new LedgerRecord
					{
						ev = "received", id = target + "-cancel", kind = "cancel", node = orig.node, target = target, from = orig.from, chat = orig.chat,
						thread = orig.thread, msg = msgId, ts = now.ToString("yyyy-MM-dd HH:mm"), exp = now.AddHours(24).ToString("yyyy-MM-dd HH:mm")
					});
					say("취소 요청을 전달합니다(" + target + "). 확정은 그 PC 의 회신(취소됨 · 취소 불가)으로 합니다.");
					return;
				}
				case "/status":
				{
					List<KeyValuePair<string, string>> topics = _c.TopicsSnapshot();
					string nodes = topics.Count > 0 ? "\n노드 : " + string.Join(", ", topics.Select(kv => kv.Value + "(회신 대기 " + AwaitingCount(kv.Value) + ")")) : "\n등록된 노드 없음";
					say(StatusLine + nodes);
					return;
				}
				default:
					say("모르는 명령입니다. /status · /pause · /resume · /deliver tg-N · /drop tg-N · /cancel(답글) · /bind <노드> · /whoami");
					return;
			}
		}

		private static LedgerRecord Clone(LedgerRecord h)
		{
			return new LedgerRecord { ev = h.ev, id = h.id, kind = h.kind, node = h.node, text = h.text, from = h.from, chat = h.chat, thread = h.thread, msg = h.msg, ts = h.ts, exp = h.exp, target = h.target };
		}

		//// ===== 전달(H3 6 ~ 7) =====
		public void Deliver()
		{
			List<LedgerRecord> pending = _ledger.Pending();
			if (pending.Count == 0)
			{
				DeliverFailing = false;
				return;
			}
			lock (_gitLock)
			{
				for (int attempt = 1; attempt <= 5; attempt++)
				{
					try
					{
						_git.EnsureClone();
						_git.SyncHard();
						List<LedgerRecord> already = new List<LedgerRecord>();
						List<LedgerRecord> added = new List<LedgerRecord>();
						Dictionary<string, string> remote = new Dictionary<string, string>(StringComparer.Ordinal);
						Dictionary<string, StringBuilder> files = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
						foreach (LedgerRecord r in pending)
						{
							string rel = "user_to_" + r.node + ".md";
							string cur;
							if (remote.TryGetValue(rel, out cur) == false)
							{
								//// 표식은 원격 커밋의 내용으로 대조한다(로컬 파일이 아니라 - 코드 리뷰 ③)
								cur = _git.RemoteFile(_c.RelayDir + "/" + rel);
								remote[rel] = cur;
							}
							if (cur.Contains(RelayEntries.Marker(r.id)))
							{
								already.Add(r);
								continue;
							}
							StringBuilder sb;
							if (files.TryGetValue(rel, out sb) == false)
							{
								sb = new StringBuilder(cur.Length > 0 ? cur : "# 사용자 → " + r.node + " (텔레그램 허브가 씀 - 회신은 " + r.node + "_to_user.md)\n");
								files[rel] = sb;
							}
							sb.Append(r.kind == "cancel" ? RelayEntries.CancelEntry(r) : RelayEntries.RequestEntry(r));
							added.Add(r);
						}
						string commit = _git.HeadCommit();
						if (added.Count > 0)
						{
							List<string> rels = new List<string>();
							Directory.CreateDirectory(_git.RelayFolder);
							foreach (KeyValuePair<string, StringBuilder> kv in files)
							{
								File.WriteAllText(Path.Combine(_git.RelayFolder, kv.Key), kv.Value.ToString(), new UTF8Encoding(false));
								rels.Add(_c.RelayDir + "/" + kv.Key);
							}
							string nodes = string.Join(" · ", added.Select(a => a.node).Distinct());
							_git.CommitAndPush("relay(hub→" + nodes + "): 요청 " + string.Join(", ", added.Select(a => a.id)) + " (텔레그램)", rels.ToArray());
							commit = _git.HeadCommit();
						}
						_lastGithubOk = DateTime.Now;
						List<LedgerRecord> done = new List<LedgerRecord>();
						foreach (LedgerRecord r in already.Concat(added)) { done.Add(new LedgerRecord { ev = "delivered", id = r.id, commit = commit }); }
						_ledger.Append(done.ToArray());
						DeliverFailing = false;
						HasNew = true;
						TelegramClient tg = Tg();
						if (tg != null)
						{
							foreach (LedgerRecord r in already.Concat(added))
							{
								string what = r.kind == "cancel" ? r.target + " 취소 요청" : r.id;
								SendSafe(tg, _c.ChatId, r.thread, "🛰 [허브] 전달됨 " + what + " (커밋 " + commit.Substring(0, 8) + ")", r.msg);
							}
						}
						return;
					}
					catch (Exception ex) when (ex is GitException || ex is IOException || ex is UnauthorizedAccessException)
					{
						HubLog.Warn("deliver attempt " + attempt + " : " + ex.Message);
						Thread.Sleep(1500 * attempt);
					}
				}
				DeliverFailing = true;
			}
		}

		//// ===== 보내기(H5) =====
		private void OutboundLoop()
		{
			while (_running)
			{
				try
				{
					_lastTick = DateTime.Now;
					lock (_c.Sync) { _c.LastAlive = _lastTick.ToString("yyyy-MM-dd HH:mm:ss"); }
					if (_ledger.Pending().Count > 0) { Deliver(); }
					lock (_gitLock) { _git.EnsureClone(); }
					string head = _git.RemoteHead();
					_lastGithubOk = DateTime.Now;
					if (head != _lastHead || _reportsDirty)
					{
						if (ForwardReports())
						{
							_lastHead = head;
							_reportsDirty = false;
						}
						else
						{
							_reportsDirty = true;
						}
					}
					if ((DateTime.Now - _lastNoReplyCheck).TotalMinutes >= 10)
					{
						CheckNoReply();
						_lastNoReplyCheck = DateTime.Now;
					}
				}
				catch (Exception ex)
				{
					//// 어떤 예외로도 보내기 스레드가 죽지 않게 한다(코드 리뷰 ⑤)
					HubLog.Warn("outbound : " + ex.GetType().Name + " " + ex.Message);
				}
				Notify();
				Thread.Sleep(Math.Max(3, _c.PollSeconds) * 1000);
			}
		}

		//// 반환 : 남은 일 없이 끝났으면 true(보내지 못한 보고가 있으면 false - 다음 주기에 다시)
		private bool ForwardReports()
		{
			List<Tuple<string, ReportEntry>> fresh = new List<Tuple<string, ReportEntry>>();
			lock (_gitLock)
			{
				_git.SyncHard();
				if (Directory.Exists(_git.RelayFolder) == false) { return true; }
				foreach (string f in Directory.GetFiles(_git.RelayFolder, "*_to_user.md"))
				{
					string node = Path.GetFileName(f);
					node = node.Substring(0, node.Length - "_to_user.md".Length);
					foreach (ReportEntry e in RelayEntries.ParseReports(File.ReadAllText(f, Encoding.UTF8)))
					{
						if (e.Tg != null)
						{
							lock (_seenLock)
							{
								_tgSeen.Add(e.Tg);
								if (e.Kind == "완료" || e.Kind == "취소됨") { _tgDone.Add(e.Tg); }
							}
						}
						if (_ledger.WasSent(e.Rid) == false) { fresh.Add(Tuple.Create(node, e)); }
					}
				}
			}
			if (_c.BaselineDone == false)
			{
				//// 설치 시점 이전의 보고는 보내지 않는다(H5 기준점)
				_ledger.MarkSent(fresh.Select(x => x.Item2.Rid));
				lock (_c.Sync)
				{
					_c.BaselineDone = true;
					_c.Save();
				}
				HubLog.Info("baseline : " + fresh.Count + " existing report(s) marked as sent");
				return true;
			}
			if (fresh.Count == 0) { return true; }
			TelegramClient tg = Tg();
			if (tg == null) { return false; }		//// 토큰이 생기면 다시 본다(코드 리뷰 ⑨)
			foreach (Tuple<string, ReportEntry> x in fresh)
			{
				long thread = _c.ThreadOfNode(x.Item1);
				if (thread == 0)
				{
					HubLog.Info("report " + x.Item2.Rid + " : node " + x.Item1 + " has no topic - skipped");
					_ledger.MarkSent(new[] { x.Item2.Rid });
					continue;
				}
				long replyTo = 0;
				if (x.Item2.Tg != null)
				{
					LedgerRecord orig = _ledger.Get(x.Item2.Tg);
					if (orig != null) { replyTo = orig.msg; }
				}
				try
				{
					tg.SendMessage(_c.ChatId, thread, RelayEntries.FormatMessage(x.Item2, x.Item1), replyTo);
				}
				catch (RetryAfterException ex)
				{
					HubLog.Warn("report " + x.Item2.Rid + " : " + ex.Message);
					Thread.Sleep(ex.Seconds * 1000 + 500);
					return false;
				}
				catch (Exception ex) when (ex is TelegramException || ex is System.Net.Http.HttpRequestException || ex is System.Threading.Tasks.TaskCanceledException || ex is InvalidOperationException || ex is ObjectDisposedException)
				{
					//// 보내지 못했다 - 표시하지 않고 다음 주기에 다시(응답을 잃었다면 같은 ID 로 한 번 더 갈 수 있다 - 최소 한 번)
					HubLog.Warn("report " + x.Item2.Rid + " : " + ex.GetType().Name + " " + ex.Message);
					return false;
				}
				_ledger.MarkSent(new[] { x.Item2.Rid });
				HasNew = true;
				Thread.Sleep(1000);		//// 초당 1 건 이하
			}
			return true;
		}

		private void CheckNoReply()
		{
			TelegramClient tg = Tg();
			if (tg == null) { return; }
			foreach (LedgerRecord r in _ledger.Delivered())
			{
				if (r.kind != "request") { continue; }
				DateTime exp;
				if (DateTime.TryParse(r.exp, out exp) == false || DateTime.Now < exp) { continue; }
				bool seen;
				lock (_seenLock) { seen = _tgSeen.Contains(r.id); }
				if (seen) { continue; }
				SendSafe(tg, _c.ChatId, r.thread, "🛰 [허브] 회신 없음 - " + r.id + " (만료 시각 " + r.exp + " 지남). 만료는 그 PC 세션이 확정합니다.", r.msg);
				_ledger.Append(new LedgerRecord { ev = "noreply", id = r.id });
			}
		}

		//// 반환 : 보냈으면 true. 실패면 멈춤 시각을 그대로 두고 다음 주기에 다시(코드 리뷰 R2)
		private bool Announce(TelegramClient tg)
		{
			string gap = string.Empty;
			if (_announceSince.HasValue)
			{
				TimeSpan d = DateTime.Now - _announceSince.Value;
				if (d.TotalMinutes >= 10) { gap = " - " + (int)d.TotalHours + " 시간 " + d.Minutes + " 분 멈춤"; }
				if (d.TotalHours >= 24) { gap += ". 24 시간 넘게 멈춰 그사이 보낸 글은 빠졌을 수 있습니다"; }
			}
			bool ok = SendSafe(tg, _c.ChatId, 0, "🛰 [허브] 재개" + gap + ". 미전달 " + _ledger.Pending().Count + " · 보류 " + _ledger.Held().Count + " 건.", 0);
			if (ok) { _announceSince = null; }
			return ok;
		}

		private bool SendSafe(TelegramClient tg, long chat, long thread, string text, long replyTo)
		{
			if (chat == 0) { return false; }
			try
			{
				tg.SendMessage(chat, thread, text, replyTo);
				return true;
			}
			catch (RetryAfterException ex)
			{
				Thread.Sleep(ex.Seconds * 1000 + 500);
				try
				{
					tg.SendMessage(chat, thread, text, replyTo);
					return true;
				}
				catch (Exception e2)
				{
					HubLog.Warn("send : " + e2.Message);
					return false;
				}
			}
			catch (Exception ex)
			{
				HubLog.Warn("send : " + ex.GetType().Name + " " + ex.Message);
				return false;
			}
		}
	}
}
