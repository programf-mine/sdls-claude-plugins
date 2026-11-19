using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FTeleHub
{
	//// 설계 §11 · §11-1 · §11-2 미처리 알림 자체 시험(I-T1 · J-T1 ~ J-T9). 결과 줄 : '+' 통과 / '-' 실패 + 이름(+ " - 세부").
	//// 임시 깃 저장소로 커미터 시각(시계 차) · 재시작 · 노드별 부분 성공 · 병합 · 묶음 분할을 본다.
	internal static class SelfTestIdle
	{
		private static List<string> _r;

		private static void Check(string name, bool ok, string detail = "")
		{
			_r.Add((ok ? "+" : "-") + name + (detail.Length > 0 ? " - " + detail : string.Empty));
		}

		public static List<string> Run(string folder)
		{
			_r = new List<string>();
			Units();
			Repo(folder);
			return _r;
		}

		private static void Units()
		{
			Check("idle : action heading 요청 / 신호", RelayIdle.IsActionHeading("## [2026-11-19 10:57] 요청 - x") && RelayIdle.IsActionHeading("## [2026-11-19 10:57] 신호 3 - x"));
			Check("idle : 공지 · 회신 · 전달 · 결과 are not actions", RelayIdle.IsActionHeading("## [2026-11-19 10:57] 공지 - x") == false && RelayIdle.IsActionHeading("## [t] 회신 - x") == false && RelayIdle.IsActionHeading("## [t] 전달 (요청) - x") == false && RelayIdle.IsActionHeading("## [t] 결과 - x") == false);
			Check("idle : channel parse", RelayIdle.ParseChannel("o1_to_laptop.md").Item2 == "laptop" && RelayIdle.ParseChannel("user_to_D1.md") == null && RelayIdle.ParseChannel("D1_to_user.md") == null && RelayIdle.ParseChannel("README.md") == null);
			Dictionary<string, List<string>> map = HubConfig.DefaultRelayNodes();
			Check("idle : writer relay of P2-TM_to_user.md is prod2", RelayIdle.WriterRelay("P2-TM_to_user.md", map) == "prod2" && RelayIdle.WriterRelay("laptop_to_o1.md", map) == "laptop" && RelayIdle.WriterRelay("README.md", map) == null);
			Check("J-T6 config clamp : key missing = 10 (default), 0 / negative off, 2000 -> 1440", new HubConfig().IdleMinutes == 10 && RelayIdle.ClampIdle(-3) == 0 && RelayIdle.ClampIdle(0) == 0 && RelayIdle.ClampIdle(10) == 10 && RelayIdle.ClampIdle(2000) == 1440);
			Dictionary<string, int> sh = RelayIdle.ParseShow("diff --git a/r/o1_to_laptop.md b/r/o1_to_laptop.md\n--- a/r/o1_to_laptop.md\n+++ b/r/o1_to_laptop.md\n@@ -1 +1,3 @@\n+## [t] 요청 - a\n+## [t] 공지 - b\ndiff --git a/r/x_to_y.md b/r/x_to_y.md\n--- /dev/null\n+++ b/r/x_to_y.md\n+## [t] 회신 - c\n");
			Check("idle : show parse - counts actions per file, lists every changed file", sh.Count == 2 && sh["o1_to_laptop.md"] == 1 && sh["x_to_y.md"] == 0, string.Join(",", sh.Select(k => k.Key + "=" + k.Value)));
			IdleItem it0 = new IdleItem { Hash = "0123456789abcdef", File = "o1_to_laptop.md" };
			string m0 = RelayIdle.RelayMessage(it0, 12);
			Check("J-T7 message has file, short commit, minutes and no title", m0.Contains("o1_to_laptop.md") && m0.Contains("0123456") && m0.Contains("0123456789") == false && m0.Contains("12 분"), m0);
		}

		private static string Git(string dir, string args, string committerDate = null)
		{
			System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("git", args)
			{
				WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
			};
			psi.EnvironmentVariables["GIT_CONFIG_PARAMETERS"] = "'safe.directory=*' 'core.quotepath=false' 'user.name=selftest' 'user.email=selftest@local' 'commit.gpgsign=false' 'core.autocrlf=false'";
			if (committerDate != null)
			{
				psi.EnvironmentVariables["GIT_COMMITTER_DATE"] = committerDate;
				psi.EnvironmentVariables["GIT_AUTHOR_DATE"] = committerDate;
			}
			using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
			{
				System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
				string o = p.StandardOutput.ReadToEnd();
				p.WaitForExit();
				if (p.ExitCode != 0) { throw new GitException("git " + args + " exit " + p.ExitCode + " : " + err.Result.Trim()); }
				return o;
			}
		}

		private static void Append(string repo, string file, string heading, string date)
		{
			File.AppendAllText(Path.Combine(repo, "relay", file), "\n" + heading + "\n- body\n", new UTF8Encoding(false));
			Git(repo, "add -A");
			Git(repo, "commit -q -m " + file, date);
		}

		private static string Iso(DateTime t)
		{
			return t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + t.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");
		}

		private static void ForceDelete(string dir)
		{
			foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { File.SetAttributes(f, FileAttributes.Normal); }
			Directory.Delete(dir, true);
		}

		private static void Repo(string folder)
		{
			Dictionary<string, List<string>> map = HubConfig.DefaultRelayNodes();
			string repo = Path.Combine(folder, "idle_repo");
			string state = Path.Combine(folder, "idle_state");
			try
			{
				if (Directory.Exists(repo)) { ForceDelete(repo); }
				if (Directory.Exists(state)) { Directory.Delete(state, true); }
				Directory.CreateDirectory(Path.Combine(repo, "relay"));
				Directory.CreateDirectory(state);
				Git(repo, "init -q -b main");
				foreach (string f in new[] { "o1_to_laptop.md", "laptop_to_o1.md", "o1_to_prod2.md", "prod2_to_o1.md", "demo1_to_o1.md", "o1_to_demo1.md" })
				{
					File.WriteAllText(Path.Combine(repo, "relay", f), "# " + f + "\n", new UTF8Encoding(false));
				}
				DateTime t0 = DateTime.Now;
				Git(repo, "add -A");
				Git(repo, "commit -q -m init", Iso(t0));
				Func<string, int, string> run = (a, ms) => Git(repo, a);
				List<string> sent = new List<string>();
				HashSet<string> failNodes = new HashSet<string>();
				Func<string, string, bool> send = (n, text) => { if (failNodes.Contains(n)) { return false; } sent.Add(n + " " + text); return true; };
				RelayIdle ri = new RelayIdle(state);
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0, send);
				Check("J-T1 baseline : first check only sets the start point", ri.State.LastCommit != null && ri.State.Items.Count == 0 && sent.Count == 0);

				//// 요청 - 보내는 PC 시계가 8 분 빠름
				Append(repo, "o1_to_laptop.md", "## [x] 요청 - a", Iso(t0.AddMinutes(8)));
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(1), send);
				Check("J-T1 request seen, not yet due", ri.State.Items.Count == 1 && sent.Count == 0, ri.State.Items.Count + " / " + sent.Count);
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(12), send);
				Check("J-T1 due after 10 min (hub clock) : LAPTOP once", sent.Count == 1 && sent[0].StartsWith("LAPTOP "), string.Join(" | ", sent));
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(14), send);
				Check("J-T1 no resend", sent.Count == 1);
				//// 회신 - 받는 PC 시계가 8 분 느림(커미터 시각은 요청보다 앞)
				Append(repo, "laptop_to_o1.md", "## [x] 회신 - a", Iso(t0.AddMinutes(-8)));
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(15), send);
				Check("J-T2 reply with an earlier committer time still closes (commit order)", ri.State.Items.Count == 0, ri.State.Items.Count.ToString());

				//// P2 두 노드 - 한 노드 실패
				sent.Clear();
				Append(repo, "o1_to_prod2.md", "## [x] 신호 1 - b", Iso(t0.AddMinutes(16)));
				failNodes.Add("P2-TM");
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(16), send);
				ri.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(27), send);
				Check("J-T4 partial : P2-VM sent, P2-TM failed", sent.Count == 1 && sent[0].StartsWith("P2-VM "), string.Join(" | ", sent));
				//// 재시작 - 새 인스턴스, 실패 노드만 다시
				failNodes.Clear();
				RelayIdle ri2 = new RelayIdle(state);
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(29), send);
				Check("J-T3 / J-T4 restart : only the failed node is retried", sent.Count == 2 && sent[1].StartsWith("P2-TM "), string.Join(" | ", sent));
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(31), send);
				Check("J-T3 restart : no resend after both nodes succeeded", sent.Count == 2);
				//// P2-TM 의 텔레그램 보고 파일 커밋으로 prod2 항목이 닫힌다(J6)
				File.WriteAllText(Path.Combine(repo, "relay", "P2-TM_to_user.md"), "# r\n## [x] 읽음 r-P2-TM-20261119000000-aaaa\n", new UTF8Encoding(false));
				Git(repo, "add -A");
				Git(repo, "commit -q -m p2tm", Iso(t0.AddMinutes(32)));
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(32), send);
				Check("J-T6 P2-TM report file closes the prod2 item", ri2.State.Items.Count == 0, ri2.State.Items.Count.ToString());

				//// 공지만 - 항목 아님
				sent.Clear();
				Append(repo, "o1_to_laptop.md", "## [x] 공지 - c", Iso(t0.AddMinutes(33)));
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(50), send);
				Check("J-T4 notice only : no item, no message", ri2.State.Items.Count == 0 && sent.Count == 0);

				//// 병합 이력 : 곁가지에 요청, 본줄에 다른 커밋, 병합
				Git(repo, "checkout -q -b side");
				Append(repo, "o1_to_demo1.md", "## [x] 요청 - side", Iso(t0.AddMinutes(51)));
				Git(repo, "checkout -q main");
				Append(repo, "o1_to_laptop.md", "## [x] 공지 - main", Iso(t0.AddMinutes(52)));
				Git(repo, "merge -q --no-edit side", Iso(t0.AddMinutes(53)));
				string head = Git(repo, "rev-parse HEAD").Trim();
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(53), send);
				Check("J-T8 merge : side-branch request found once, base moved to the merge head", ri2.State.Items.Count == 1 && ri2.State.Items[0].File == "o1_to_demo1.md" && ri2.State.LastCommit == head && ri2.State.ScanQueue.Count == 0, ri2.State.Items.Count + " " + ri2.State.LastCommit);
				Append(repo, "demo1_to_o1.md", "## [x] 회신 - side", Iso(t0.AddMinutes(54)));
				ri2.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(70), send);
				Check("J-T8 reply after the merge closes it, no message", ri2.State.Items.Count == 0 && sent.Count == 0, ri2.State.Items.Count + " / " + sent.Count);

				//// 묶음 분할 : 요청 · 회신이 두 검사에 나뉘어도 오보 없음(상한을 아주 짧게)
				Append(repo, "o1_to_laptop.md", "## [x] 요청 - split", Iso(t0.AddMinutes(71)));
				Append(repo, "laptop_to_o1.md", "## [x] 회신 - split", Iso(t0.AddMinutes(72)));
				RelayIdle ri3 = new RelayIdle(state);
				int shows = 0;
				Func<string, int, string> cutAfterOne = (a, ms) =>
				{
					if (a.StartsWith("show", StringComparison.Ordinal) && ++shows > 1) { throw new IdleBudgetException(); }		//// 두 번째 커밋에서 마감이 온 것처럼
					return Git(repo, a);
				};
				ri3.CheckRelay(cutAfterOne, "relay", "HEAD", map, 10, t0.AddMinutes(100), send);
				Check("J-T9 first batch : one commit seen, queue left, no message", ri3.State.ScanQueue.Count == 1 && sent.Count == 0, ri3.State.ScanQueue.Count + " / " + sent.Count);
				ri3.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(102), send);
				Check("J-T9 second batch closes it - no false alarm", ri3.State.ScanQueue.Count == 0 && ri3.State.Items.Count == 0 && sent.Count == 0, ri3.State.Items.Count + " / " + sent.Count);

				//// 코드 리뷰 R4 6 회차 : 이전 목록을 마치는 주기에 새 회신이 이미 올라와 있으면, 그 회신을 보기 전에는 알리지 않는다
				Append(repo, "o1_to_laptop.md", "## [x] 요청 - late", Iso(t0.AddMinutes(110)));
				Append(repo, "o1_to_laptop.md", "## [x] 공지 - late2", Iso(t0.AddMinutes(111)));
				int shows2 = 0;
				Func<string, int, string> cut2 = (a, ms) =>
				{
					if (a.StartsWith("show", StringComparison.Ordinal) && ++shows2 > 1) { throw new IdleBudgetException(); }
					return Git(repo, a);
				};
				ri3.CheckRelay(cut2, "relay", "HEAD", map, 10, t0.AddMinutes(110), send);		//// 요청만 보고 목록(공지 1 개) 남김
				Append(repo, "laptop_to_o1.md", "## [x] 회신 - late", Iso(t0.AddMinutes(112)));		//// 그 사이 회신
				ri3.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(130), send);		//// 이전 목록만 마침 - 머리(회신) 는 아직 안 봄 → 알리면 안 된다
				Check("R4 finishing the old range does not notify before the newer reply is scanned", sent.Count == 0 && ri3.State.Items.Count == 1, sent.Count + " / " + ri3.State.Items.Count);
				ri3.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(132), send);
				Check("R4 next check scans the reply and closes it - still no message", sent.Count == 0 && ri3.State.Items.Count == 0, sent.Count + " / " + ri3.State.Items.Count);

				//// 코드 리뷰 R1 4 회차 : 실제 GitRelay.Run 의 제한(종료 + 출력 수집) - 3 초 걸리는 명령에 1 초 제한
				GitRelay gr = new GitRelay(new HubConfig());
				System.Diagnostics.Stopwatch gw = System.Diagnostics.Stopwatch.StartNew();
				string gres = "no exception";
				try { gr.Run(repo, "-c \"alias.zz=!sleep 3\" zz", 1000); }
				catch (GitException ex) { gres = ex.Message; }
				long gms = gw.ElapsedMilliseconds;
				Check("R1 real GitRelay.Run with a 1 s limit stops a 3 s command near the limit", gres.Contains("timed out") && gms < 1800, gms + " ms : " + gres);
				string fast = gr.Run(repo, "rev-parse HEAD", 1000).Trim();
				Check("R1 real GitRelay.Run with a limit returns full output when in time", fast.Length == 40, fast);

				ri3.MarkTgSent("tg-1");
				Check("J-T5 telegram idle record is kept in idle_state (not the ledger)", new RelayIdle(state).TgWasSent("tg-1"));

				//// 코드 리뷰 R3 : 경과 시간은 "전달됨" 을 처음 본 시각부터, 재시작해도 유지
				RelayIdle rt = new RelayIdle(state);
				DateTime f1 = rt.TgFirstSeen("tg-2", t0.AddMinutes(200));
				rt.SaveIfDirty();
				DateTime f2 = new RelayIdle(state).TgFirstSeen("tg-2", t0.AddMinutes(230));
				Check("R3 first-seen time of a delivered request is kept across restarts", f1 == f2 && f1 == t0.AddMinutes(200), f1.ToString("o") + " / " + f2.ToString("o"));
				//// 코드 리뷰 R2 : 개수로 자르지 않고, 전달 목록에서 빠진 ID 만 지운다
				RelayIdle rp = new RelayIdle(state);
				for (int k = 0; k < 1100; k++) { rp.State.TgSent.Add("tg-x" + k); }
				rp.MarkTgSent("tg-last");
				Check("R2 more than 1000 records are not truncated", rp.TgWasSent("tg-x0") && rp.State.TgSent.Count == 1102, rp.State.TgSent.Count.ToString());
				rp.PruneTg(new HashSet<string>(new[] { "tg-x5", "tg-2" }, StringComparer.Ordinal));
				rp.SaveIfDirty();
				Check("R2 prune keeps only ids still delivered", rp.State.TgSent.Count == 1 && rp.TgWasSent("tg-x5") && rp.State.TgFirst.ContainsKey("tg-2") && new RelayIdle(state).TgWasSent("tg-x0") == false, rp.State.TgSent.Count.ToString());

				//// 코드 리뷰 R1 · J-T10 : 느린 깃 - 명령마다 400 ms 를 쓰되 받은 제한(ms) 을 넘으면 시간 초과로 끝나는 흉내. 상한 1.5 s.
				//// 한 검사가 상한 + 명령 하나(최대 400 ms) 안에 돌아오고 다음 주기에 이어진다.
				for (int k = 0; k < 6; k++) { Append(repo, "o1_to_laptop.md", "## [x] 공지 - slow " + k, Iso(t0.AddMinutes(300 + k))); }
				Func<string, int, string> slow = (a, ms) =>
				{
					if (ms < 400) { System.Threading.Thread.Sleep(ms); throw new GitException("git x : timed out after " + ms + " ms"); }
					System.Threading.Thread.Sleep(400);
					return Git(repo, a);
				};
				RelayIdle rs = new RelayIdle(state);
				System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
				try { rs.CheckRelay(slow, "relay", "HEAD", map, 10, t0.AddMinutes(310), send, 1500); } catch (GitException) { }
				long first = w.ElapsedMilliseconds;
				int leftAfter = rs.State.ScanQueue.Count;
				long worst = first;
				for (int k = 0; k < 20 && rs.State.ScanQueue.Count > 0; k++)
				{
					w.Restart();
					try { rs.CheckRelay(slow, "relay", "HEAD", map, 10, t0.AddMinutes(311 + k), send, 1500); } catch (GitException) { }
					worst = Math.Max(worst, w.ElapsedMilliseconds);
				}
				Check("J-T10 slow git : every check returns within the budget (+ one command) and the rest continues later", worst <= 1500 + 450 && leftAfter > 0 && rs.State.ScanQueue.Count == 0, "first " + first + " ms, worst " + worst + " ms, left " + leftAfter);

				//// 코드 리뷰 R1 : 첫 커밋의 조상 판정도 마감에서 멈춘다 - 열린 항목 여러 개 + 받는 쪽 회신 한 커밋, 상한 1 s
				RelayIdle ra = new RelayIdle(state);
				string baseHead = Git(repo, "rev-parse HEAD").Trim();
				ra.State.LastCommit = baseHead;
				ra.State.ScanQueue.Clear();
				ra.State.ScanTarget = null;
				for (int k = 0; k < 6; k++) { Append(repo, "o1_to_laptop.md", "## [x] 요청 - many " + k, Iso(t0.AddMinutes(400 + k))); }
				ra.CheckRelay(run, "relay", "HEAD", map, 10, t0.AddMinutes(406), send);
				int open = ra.State.Items.Count;
				Append(repo, "laptop_to_o1.md", "## [x] 회신 - many", Iso(t0.AddMinutes(407)));
				Func<string, int, string> slowAnc = (a, ms) =>
				{
					if (a.StartsWith("merge-base", StringComparison.Ordinal)) { System.Threading.Thread.Sleep(300); }
					return Git(repo, a);
				};
				w.Restart();
				ra.CheckRelay(slowAnc, "relay", "HEAD", map, 10, t0.AddMinutes(407), send, 1000);
				long t1 = w.ElapsedMilliseconds;
				int afterFirst = ra.State.Items.Count;
				for (int k = 0; k < 10 && ra.State.Items.Count > 0; k++) { ra.CheckRelay(slowAnc, "relay", "HEAD", map, 10, t0.AddMinutes(408 + k), send, 1000); }
				Check("R1 first-commit ancestor checks stop at the budget, the commit stays queued and later closes everything", open == 6 && t1 <= 1000 + 700 && afterFirst > 0 && ra.State.Items.Count == 0 && ra.State.ScanQueue.Count == 0, "open " + open + ", " + t1 + " ms, after first " + afterFirst + ", end " + ra.State.Items.Count);

				//// 코드 리뷰 R4 : 조상이 아닌 열린 항목(다른 분기) 판정이 마감으로 나뉘어도 앞으로 간다 - 판정한 키를 남긴다
				Git(repo, "checkout -q -b other");
				Append(repo, "o1_to_laptop.md", "## [x] 요청 - other", Iso(t0.AddMinutes(500)));
				string otherHash = Git(repo, "rev-parse HEAD").Trim();
				Git(repo, "checkout -q main");
				RelayIdle rn = new RelayIdle(state);
				for (int k = 0; k < 8; k++)
				{
					rn.State.Items.Add(new IdleItem { Key = otherHash + "|fake" + k + ".md", Hash = otherHash, File = "o1_to_laptop.md", Relay = "laptop", FirstSeen = t0.AddMinutes(500).ToString("o", CultureInfo.InvariantCulture) });
				}
				Append(repo, "laptop_to_o1.md", "## [x] 회신 - main only", Iso(t0.AddMinutes(501)));
				int cycles = 0;
				do
				{
					rn.CheckRelay(slowAnc, "relay", "HEAD", map, 600, t0.AddMinutes(502 + cycles), send, 1000);
					cycles++;
				}
				while (rn.State.ScanQueue.Count > 0 && cycles < 10);
				Check("R4 non-ancestor checks split by the budget still finish (queue empties, the 8 items stay open)", rn.State.ScanQueue.Count == 0 && rn.State.Items.Count(i => i.Hash == otherHash) == 8 && cycles > 1 && cycles <= 5, "cycles " + cycles + ", queue " + rn.State.ScanQueue.Count + ", open " + rn.State.Items.Count);
			}
			catch (Exception ex)
			{
				Check("idle : git repository checks ran", false, ex.GetType().Name + " " + ex.Message);
			}
		}
	}
}
