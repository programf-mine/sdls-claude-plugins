using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FTeleHub
{
	//// SDLS 팔레트 - 바탕 #FAF4FF, 글자 #2A2433, 머리 #3C1464, 강조 #7A52B3
	internal static class Palette
	{
		public static readonly Color Bg = Color.FromArgb(0xFA, 0xF4, 0xFF);
		public static readonly Color Ink = Color.FromArgb(0x2A, 0x24, 0x33);
		public static readonly Color Ink2 = Color.FromArgb(0x5E, 0x4B, 0x75);
		public static readonly Color Head = Color.FromArgb(0x3C, 0x14, 0x64);
		public static readonly Color Line = Color.FromArgb(0xE5, 0xDC, 0xEF);
		public static readonly Font Body = new Font("Malgun Gothic", 9.5f);
	}

	//// 상태 창(H9) - 노드별 토픽 · 미완료 · 보류 목록
	internal sealed class StatusForm : Form
	{
		public StatusForm(HubEngine eng)
		{
			Text = "SDLS 텔레그램 허브 - 상태";
			StartPosition = FormStartPosition.CenterScreen;
			Size = new Size(640, 420);
			BackColor = Palette.Bg; ForeColor = Palette.Ink; Font = Palette.Body;
			FormBorderStyle = FormBorderStyle.SizableToolWindow;
			ShowInTaskbar = true;

			Label head = new Label { Dock = DockStyle.Top, Height = 34, BackColor = Palette.Head, ForeColor = Palette.Bg, Padding = new Padding(10, 8, 10, 0), Text = eng.StatusLine };
			ListView lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, BackColor = Palette.Bg, ForeColor = Palette.Ink, HeaderStyle = ColumnHeaderStyle.Nonclickable };
			lv.Columns.Add("노드", 110); lv.Columns.Add("토픽", 80); lv.Columns.Add("미전달", 70); lv.Columns.Add("전달 · 회신 대기", 120); lv.Columns.Add("보류", 60);
			var topics = eng.Config.TopicsSnapshot();
			foreach (var kv in topics.OrderBy(k => k.Value))
			{
				string node = kv.Value;
				int pending = eng.Ledger.Pending().Count(r => r.node == node);
				int awaiting = eng.AwaitingCount(node);
				int held = eng.Ledger.Held().Count(r => r.node == node);
				lv.Items.Add(new ListViewItem(new[] { node, kv.Key, pending.ToString(), awaiting.ToString(), held.ToString() }));
			}
			if (topics.Count == 0) { lv.Items.Add(new ListViewItem(new[] { "(없음)", "-", "-", "-", "-" })); }
			Label foot = new Label { Dock = DockStyle.Bottom, Height = 52, ForeColor = Palette.Ink2, Padding = new Padding(10, 6, 10, 6),
				Text = "보류(held) : " + string.Join(", ", eng.Ledger.Held().Select(h => h.id)) + "\n설정 " + HubConfig.ConfigPath + " · 노드 등록은 텔레그램 토픽에서 /bind <노드>" };
			Controls.Add(lv); Controls.Add(foot); Controls.Add(head);
		}
	}

	//// 토큰 입력 - 화면에 보이지 않게(H6)
	internal sealed class TokenForm : Form
	{
		private readonly TextBox _box;
		public string Token { get { return _box.Text.Trim(); } }

		public TokenForm()
		{
			Text = "SDLS 텔레그램 허브 - 토큰 설정";
			StartPosition = FormStartPosition.CenterScreen;
			FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
			ClientSize = new Size(440, 130);
			BackColor = Palette.Bg; ForeColor = Palette.Ink; Font = Palette.Body;
			Label l = new Label { Left = 14, Top = 12, Width = 410, Height = 36, Text = "BotFather 에서 받은 봇 토큰을 붙여 넣으세요. 이 PC 사용자 계정에만 암호화해 저장합니다(DPAPI)." };
			_box = new TextBox { Left = 14, Top = 54, Width = 410, UseSystemPasswordChar = true };
			Button ok = new Button { Text = "저장", Left = 268, Top = 90, Width = 75, DialogResult = DialogResult.OK };
			Button cancel = new Button { Text = "취소", Left = 349, Top = 90, Width = 75, DialogResult = DialogResult.Cancel };
			Controls.AddRange(new Control[] { l, _box, ok, cancel });
			AcceptButton = ok; CancelButton = cancel;
		}
	}
}
