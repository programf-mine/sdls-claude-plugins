using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;

namespace SdoTelegramHub
{
	//// 텔레그램 Bot API 최소 구현 - getUpdates(롱폴링) · sendMessage. 토큰은 URL 에만 들어가며 예외 문구는 HubLog 가 가린다.
	internal sealed class TelegramClient : IDisposable
	{
		private readonly HttpClient _http;
		private readonly string _token;
		private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

		public TelegramClient(string token)
		{
			_token = token;
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
		}

		public void Dispose() { _http.Dispose(); }

		private string Url(string method) { return "https://api.telegram.org/bot" + _token + "/" + method; }

		//// 반환 : update 목록(Dictionary). 실패면 예외(429 는 RetryAfterException).
		public List<Dictionary<string, object>> GetUpdates(long offset, int timeoutSec)
		{
			Dictionary<string, object> body = new Dictionary<string, object>
			{
				{ "offset", offset }, { "timeout", timeoutSec },
				{ "allowed_updates", new[] { "message", "edited_message" } }
			};
			Dictionary<string, object> r = Call("getUpdates", body);
			List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
			object[] arr = r["result"] as object[];
			if (arr == null && r["result"] is System.Collections.ArrayList al) { arr = al.ToArray(); }
			if (arr != null)
			{
				foreach (object o in arr) { if (o is Dictionary<string, object> d) { list.Add(d); } }
			}
			return list;
		}

		//// 반환 : 보낸 메시지 ID
		public long SendMessage(long chatId, long threadId, string text, long replyTo)
		{
			Dictionary<string, object> body = new Dictionary<string, object> { { "chat_id", chatId }, { "text", text }, { "disable_web_page_preview", true } };
			if (threadId > 0) { body["message_thread_id"] = threadId; }
			if (replyTo > 0) { body["reply_parameters"] = new Dictionary<string, object> { { "message_id", replyTo }, { "allow_sending_without_reply", true } }; }
			Dictionary<string, object> r = Call("sendMessage", body);
			Dictionary<string, object> m = r["result"] as Dictionary<string, object>;
			return m != null && m.ContainsKey("message_id") ? Convert.ToInt64(m["message_id"]) : 0;
		}

		private Dictionary<string, object> Call(string method, Dictionary<string, object> body)
		{
			using (StringContent c = new StringContent(_json.Serialize(body), Encoding.UTF8, "application/json"))
			using (HttpResponseMessage resp = _http.PostAsync(Url(method), c).GetAwaiter().GetResult())
			{
				string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				Dictionary<string, object> r = _json.Deserialize<Dictionary<string, object>>(text);
				if (r == null) { throw new TelegramException(method + " : empty reply (HTTP " + (int)resp.StatusCode + ")"); }
				object ok;
				if (r.TryGetValue("ok", out ok) && ok is bool && (bool)ok) { return r; }
				int retry = 0;
				object prm;
				if (r.TryGetValue("parameters", out prm) && prm is Dictionary<string, object> p && p.ContainsKey("retry_after")) { retry = Convert.ToInt32(p["retry_after"]); }
				string desc = r.ContainsKey("description") ? Convert.ToString(r["description"]) : "error";
				if (retry > 0) { throw new RetryAfterException(retry, method + " : " + desc); }
				throw new TelegramException(method + " : " + desc + " (HTTP " + (int)resp.StatusCode + ")");
			}
		}
	}

	internal class TelegramException : Exception
	{
		public TelegramException(string m) : base(m) { }
	}

	internal sealed class RetryAfterException : TelegramException
	{
		public int Seconds { get; private set; }
		public RetryAfterException(int s, string m) : base(m) { Seconds = s; }
	}

	//// update JSON 을 다루는 작은 도우미
	internal static class J
	{
		public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
		{
			object o; return d != null && d.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
		}
		public static long Long(Dictionary<string, object> d, string key)
		{
			object o; return d != null && d.TryGetValue(key, out o) && o != null ? Convert.ToInt64(o) : 0;
		}
		public static string Str(Dictionary<string, object> d, string key)
		{
			object o; return d != null && d.TryGetValue(key, out o) ? o as string : null;
		}
		public static bool Bool(Dictionary<string, object> d, string key)
		{
			object o; return d != null && d.TryGetValue(key, out o) && o is bool && (bool)o;
		}
	}
}
