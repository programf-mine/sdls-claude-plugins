using System.Text.RegularExpressions;

namespace SdoTelegramHub
{
	//// 보내기 직전 거르기(H5) - 보조 방어. 알려진 형식만 가린다. 공개 여부의 책임은 쓰는 세션(S2).
	internal static class ReportFilter
	{
		private const string Mask = "[가림]";
		private static readonly Regex Hex = new Regex(@"\b[0-9A-Fa-f]{32,}\b", RegexOptions.Compiled);
		private static readonly Regex DrivePath = new Regex(@"(?i)\b[A-Z]:[\\/][^\s,;)""']*", RegexOptions.Compiled);
		private static readonly Regex Unc = new Regex(@"\\\\[^\s\\]+\\[^\s,;)""']*", RegexOptions.Compiled);
		private static readonly Regex Lic = new Regex(@"(?i)\S+\.(lic|sdlskey|pfx|snk)\b", RegexOptions.Compiled);
		private static readonly Regex Secret = new Regex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|비밀번호|암호|토큰)\s*[:=]\s*\S+", RegexOptions.Compiled);
		private static readonly Regex BotToken = new Regex(@"\b\d{6,12}:[A-Za-z0-9_-]{30,}\b", RegexOptions.Compiled);
		private static readonly Regex CalLine = new Regex(@"(?i)(cal|ffc|보정|calib)", RegexOptions.Compiled);
		private static readonly Regex Number = new Regex(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

		public static string Clean(string s)
		{
			if (string.IsNullOrEmpty(s)) { return string.Empty; }
			string t = BotToken.Replace(s, Mask);
			t = Secret.Replace(t, Mask);
			t = Hex.Replace(t, Mask);
			t = Unc.Replace(t, Mask);
			t = DrivePath.Replace(t, Mask);
			t = Lic.Replace(t, Mask);
			if (CalLine.IsMatch(t)) { t = Number.Replace(t, Mask); }
			return t.Trim();
		}
	}
}
