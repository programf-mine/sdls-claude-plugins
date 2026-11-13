using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SdoTelegramHub
{
	//// 세션 보고 항목(§4-0 · S2) : "## [시각] <종류> [tg-N] r-<노드>-<yyyyMMddHHmmss>-<4 자> - <공개용 한 줄>" + 선택 "- 공개 : …" 줄
	internal sealed class ReportEntry
	{
		public string Kind;			//// 완료 · 질문 · 오류 · 보고 · 읽음 · 취소됨 · 취소불가
		public string Tg;			//// tg-N 또는 null
		public string Rid;			//// 보고 ID
		public string Node;			//// 보고 ID 의 노드
		public string Title;
		public List<string> PublicLines = new List<string>();
	}

	internal static class RelayEntries
	{
		private static readonly Regex Header = new Regex(
			@"^## \[(?<ts>[^\]]+)\] (?<kind>\S+)(?: (?<tg>tg-\d+))? (?<rid>r-(?<node>[A-Z0-9][A-Z0-9-]*)-\d{14}-[a-z0-9]{4})(?: - (?<title>.*))?\s*$",
			RegexOptions.Compiled);

		public static readonly string[] Kinds = { "완료", "질문", "오류", "보고", "읽음", "취소됨", "취소불가" };

		public static List<ReportEntry> ParseReports(string text)
		{
			List<ReportEntry> list = new List<ReportEntry>();
			ReportEntry cur = null;
			foreach (string raw in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
			{
				if (raw.StartsWith("## ", StringComparison.Ordinal))
				{
					Match m = Header.Match(raw);
					cur = null;
					if (m.Success)
					{
						cur = new ReportEntry { Kind = m.Groups["kind"].Value, Tg = m.Groups["tg"].Success ? m.Groups["tg"].Value : null, Rid = m.Groups["rid"].Value, Node = m.Groups["node"].Value, Title = m.Groups["title"].Success ? m.Groups["title"].Value.Trim() : string.Empty };
						list.Add(cur);
					}
					continue;
				}
				if (cur != null && raw.StartsWith("- 공개 :", StringComparison.Ordinal))
				{
					cur.PublicLines.Add(raw.Substring("- 공개 :".Length).Trim());
				}
			}
			return list;
		}

		public static string IconOf(string kind)
		{
			switch (kind)
			{
				case "완료": return "✅";
				case "질문": return "❓";
				case "오류": return "⚠️";
				case "읽음": return "👀";
				case "취소됨": return "⛔";
				case "취소불가": return "⛔";
				default: return "📝";		//// 보고 · 모르는 종류(§4-0 : 모르는 종류는 보고로)
			}
		}

		//// 텔레그램 첫 줄 형식 : <아이콘> [<노드>] <종류> [tg-N] - <공개용 한 줄>
		public static string FormatMessage(ReportEntry e, string node)
		{
			string kind = Array.IndexOf(Kinds, e.Kind) >= 0 ? e.Kind : "보고";
			string shown = kind == "취소불가" ? "취소 불가" : kind;
			StringBuilder sb = new StringBuilder();
			sb.Append(IconOf(kind)).Append(" [").Append(node).Append("] ").Append(shown);
			if (e.Tg != null) { sb.Append(' ').Append(e.Tg); }
			string title = ReportFilter.Clean(e.Title);
			if (title.Length > 0) { sb.Append(" - ").Append(title); }
			foreach (string p in e.PublicLines)
			{
				string c = ReportFilter.Clean(p);
				if (c.Length > 0) { sb.Append('\n').Append(c); }
			}
			string s = sb.ToString();
			return s.Length > 3500 ? s.Substring(0, 3500) + " …" : s;
		}

		public static string Marker(string id) { return "<!-- id:" + id + " -->"; }

		//// 지시 항목(H4)
		public static string RequestEntry(LedgerRecord r)
		{
			string firstLine = (r.text ?? string.Empty).Replace("\r\n", "\n").Split('\n')[0].Trim();
			if (firstLine.Length > 60) { firstLine = firstLine.Substring(0, 60) + " …"; }
			StringBuilder sb = new StringBuilder();
			sb.Append("\n## [").Append(r.ts).Append("] 요청 ").Append(r.id).Append(" - (텔레그램) ").Append(firstLine).Append('\n');
			sb.Append(Marker(r.id)).Append('\n');
			sb.Append("- 만료 : ").Append(r.exp).Append('\n');
			sb.Append("- 원문 :\n");
			foreach (string line in (r.text ?? string.Empty).Replace("\r\n", "\n").Split('\n')) { sb.Append("  ").Append(line).Append('\n'); }
			return sb.ToString();
		}

		public static string CancelEntry(LedgerRecord r)
		{
			return "\n## [" + r.ts + "] 요청 취소 " + r.target + "\n" + Marker(r.id) + "\n- 사용자가 텔레그램에서 /cancel - 시작 전이면 취소됨, 시작했으면 취소불가로 회신(S4)\n";
		}
	}
}
