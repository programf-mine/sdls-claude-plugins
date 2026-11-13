using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace SdoTelegramHub
{
	//// --selftest [출력 폴더] : 보고 파서 · 메시지 형식 · 거르기 · 지시 항목 · 장부(손상 줄) · 아이콘 그리기.
	//// 결과는 <출력 폴더>\selftest.txt(종료 코드 = 실패 수), 아이콘 PNG 도 같은 폴더에.
	internal static class SelfTest
	{
		private static int _pass, _fail;
		private static StringBuilder _out;

		private static void Check(string name, bool ok, string detail = "")
		{
			if (ok) { _pass++; } else { _fail++; }
			_out.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail.Length > 0 ? " - " + detail : string.Empty));
		}

		public static int Run(string folder)
		{
			folder = folder ?? Path.Combine(Path.GetTempPath(), "SdoTelegramHub_selftest");
			Directory.CreateDirectory(folder);
			_out = new StringBuilder();

			string md = "# D1 → 사용자\n\n## [2026-11-13 14:05] 읽음 tg-812345 r-D1-20261113140501-a7f2\n\n## [2026-11-13 14:20] 완료 tg-812345 r-D1-20261113142011-b001 - 스캔 시험 끝, 모두 통과\n- 공개 : 다음은 전송 시험\n- 세부 : D:\\Data\\x.bmp 98/0\n\n## [2026-11-13 14:20] 보고 r-D252-TM-20261113142011-c9z1 - 같은 시각 같은 제목\n## [2026-11-13 14:20] 보고 r-D252-TM-20261113142012-d0z1 - 같은 시각 같은 제목\n## [2026-11-13 14:30] 회신 tg-1 - 형식 틀린 머리줄(보고 ID 없음)\n";
			List<ReportEntry> es = RelayEntries.ParseReports(md);
			Check("parse : 4 entries with report IDs (malformed header skipped)", es.Count == 4, es.Count.ToString());
			Check("parse : kind / tg / rid / node", es.Count > 1 && es[1].Kind == "완료" && es[1].Tg == "tg-812345" && es[1].Rid == "r-D1-20261113142011-b001" && es[1].Node == "D1");
			Check("parse : node with hyphen", es.Count > 2 && es[2].Node == "D252-TM", es.Count > 2 ? es[2].Node : "");
			Check("parse : same time + title -> different IDs", es.Count > 3 && es[2].Rid != es[3].Rid);
			Check("parse : only '- 공개 :' lines kept", es.Count > 1 && es[1].PublicLines.Count == 1 && es[1].PublicLines[0] == "다음은 전송 시험");
			string msg = es.Count > 1 ? RelayEntries.FormatMessage(es[1], "D1") : "";
			Check("format : icon + [node] kind tg - title + public line", msg == "✅ [D1] 완료 tg-812345 - 스캔 시험 끝, 모두 통과\n다음은 전송 시험", msg.Replace("\n", "\\n"));
			Check("format : body detail (path) not sent", msg.Contains("D:\\Data") == false);
			ReportEntry unknown = new ReportEntry { Kind = "이상한종류", Rid = "r-X-20261113000000-aaaa", Title = "t" };
			Check("format : unknown kind -> 보고", RelayEntries.FormatMessage(unknown, "X").StartsWith("📝 [X] 보고"));
			ReportEntry cancelNo = new ReportEntry { Kind = "취소불가", Tg = "tg-5", Rid = "r-X-20261113000000-aaab", Title = "이미 시작" };
			Check("format : 취소불가 shown as '취소 불가'", RelayEntries.FormatMessage(cancelNo, "X") == "⛔ [X] 취소 불가 tg-5 - 이미 시작");

			Check("filter : drive path", ReportFilter.Clean("저장 C:\\SDOPTICS\\a.bmp 끝") == "저장 [가림] 끝", ReportFilter.Clean("저장 C:\\SDOPTICS\\a.bmp 끝"));
			Check("filter : UNC", ReportFilter.Clean(@"\\nas\share\x 확인").Contains("nas") == false);
			Check("filter : long hex", ReportFilter.Clean("해시 C3CF4E90A1B2C3D4E5F6A7B8C9D0E1F2A3B4") == "해시 [가림]");
			Check("filter : .lic", ReportFilter.Clean("SDLS.SDK.lic 를 넣음").Contains(".lic") == false);
			Check("filter : password", ReportFilter.Clean("password=abc123 설정").Contains("abc123") == false);
			Check("filter : bot token", ReportFilter.Clean("123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0 x").Contains("AAH") == false);
			Check("filter : Cal numbers", ReportFilter.Clean("Cal 배율 1.0812 적용") == "Cal 배율 [가림] 적용", ReportFilter.Clean("Cal 배율 1.0812 적용"));
			Check("filter : ordinary numbers kept", ReportFilter.Clean("시험 98 통과") == "시험 98 통과");

			LedgerRecord r = new LedgerRecord { id = "tg-812345", kind = "request", node = "D1", text = "스캔 시험 돌려줘\n결과 알려줘", ts = "2026-11-13 14:05", exp = "2026-11-14 14:05" };
			string entry = RelayEntries.RequestEntry(r);
			Check("request entry : header starts with 요청 tg-N", entry.Contains("## [2026-11-13 14:05] 요청 tg-812345 - (텔레그램) 스캔 시험 돌려줘"));
			Check("request entry : marker + expiry + indented body", entry.Contains("<!-- id:tg-812345 -->") && entry.Contains("- 만료 : 2026-11-14 14:05") && entry.Contains("  결과 알려줘"));
			string cancel = RelayEntries.CancelEntry(new LedgerRecord { id = "tg-812345-cancel", target = "tg-812345", ts = "2026-11-13 14:10" });
			Check("cancel entry", cancel.Contains("요청 취소 tg-812345") && cancel.Contains("<!-- id:tg-812345-cancel -->"));

			//// 장부 : 마지막 줄 손상은 버리고 계속, 중간 줄 손상은 Corrupt
			string lf = Path.Combine(folder, "ledger_t1");
			if (Directory.Exists(lf)) { Directory.Delete(lf, true); }
			Ledger l1 = new Ledger(lf);
			l1.Append(new LedgerRecord { ev = "received", id = "tg-1", kind = "request", node = "D1", msg = 11 }, new LedgerRecord { ev = "held", id = "tg-2", kind = "request", node = "D1", msg = 12 });
			File.AppendAllText(Path.Combine(lf, "ledger.jsonl"), "{\"ev\":\"deliv", new UTF8Encoding(false));
			Ledger l2 = new Ledger(lf);
			Check("ledger : broken last line dropped, not corrupt", l2.Corrupt == false && l2.Pending().Count == 1 && l2.Held().Count == 1);
			Check("ledger : message -> id", l2.IdOfMessage(11) == "tg-1");
			//// 코드 리뷰 ① : 잘라 낸 뒤 이어서 기록해도 다음 로드가 정상이어야 한다
			l2.Append(new LedgerRecord { ev = "delivered", id = "tg-1", commit = "c1" });
			Ledger l2b = new Ledger(lf);
			Check("ledger : append after truncated last line reloads cleanly", l2b.Corrupt == false && l2b.StateOf("tg-1") == "delivered" && l2b.Held().Count == 1);
			string lines = File.ReadAllText(Path.Combine(lf, "ledger.jsonl"));
			File.WriteAllText(Path.Combine(lf, "ledger.jsonl"), "garbage\n" + lines, new UTF8Encoding(false));
			Ledger l3 = new Ledger(lf);
			Check("ledger : broken middle line -> Corrupt", l3.Corrupt);
			string lf2 = Path.Combine(folder, "ledger_t2");
			if (Directory.Exists(lf2)) { Directory.Delete(lf2, true); }
			Ledger l4 = new Ledger(lf2);
			l4.Append(new LedgerRecord { ev = "received", id = "tg-9", kind = "request", node = "O1" });
			l4.Append(new LedgerRecord { ev = "delivered", id = "tg-9", commit = "abc" });
			l4.MarkSent(new[] { "r-O1-20261113000000-aaaa" });
			Ledger l5 = new Ledger(lf2);
			Check("ledger : delivered not pending after reload", l5.Pending().Count == 0 && l5.StateOf("tg-9") == "delivered");
			Check("ledger : sent ids persist", l5.WasSent("r-O1-20261113000000-aaaa") && l5.WasSent("r-O1-20261113000000-aaab") == false);

			Check("node name rule", HubConfig.IsValidNode("D252-TM") && HubConfig.IsValidNode("d1") == false && HubConfig.IsValidNode("TOO-LONG-NODE-NAME") == false);

			//// 아이콘 : 상태 5 가지 × 밝음 · 어두움 × 16 · 24 · 32 PNG
			foreach (HubState st in Enum.GetValues(typeof(HubState)))
			{
				foreach (bool light in new[] { true, false })
				{
					foreach (int size in new[] { 16, 24, 32 })
					{
						using (Bitmap b = TrayIconRenderer.Draw(size, st, light))
						{
							b.Save(Path.Combine(folder, "icon_" + st + "_" + (light ? "light" : "dark") + "_" + size + ".png"), ImageFormat.Png);
							int opaque = 0;
							for (int y = 0; y < size; y++) { for (int x = 0; x < size; x++) { if (b.GetPixel(x, y).A > 0) { opaque++; } } }
							if (size == 24) { Check("icon " + st + " " + (light ? "light" : "dark") + " 24 px drawn, transparent background", opaque > 20 && b.GetPixel(0, 0).A == 0, opaque + " px"); }
						}
					}
				}
			}

			_out.AppendLine("== RESULT selftest pass " + _pass + " / fail " + _fail + " ==");
			File.WriteAllText(Path.Combine(folder, "selftest.txt"), _out.ToString(), new UTF8Encoding(true));
			return _fail;
		}
	}
}
