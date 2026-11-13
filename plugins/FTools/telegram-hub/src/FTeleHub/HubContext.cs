using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FTeleHub
{
	//// 트레이(H9) - 창 없이 NotifyIcon 과 메뉴. 상태가 바뀌면 아이콘 · 툴팁을 다시 그린다.
	internal sealed class HubContext : ApplicationContext
	{
		private readonly NotifyIcon _tray;
		private readonly HubEngine _engine;
		private readonly ToolStripMenuItem _miHead, _miPause, _miAuto;
		private readonly Control _ui = new Control();
		private readonly Timer _timer = new Timer { Interval = 3000 };
		private HubState _shownState = (HubState)(-1);
		private bool _shownLight;
		private Icon _icon;
		private DateTime _offlineSince = DateTime.MinValue;
		private bool _offlineBalloonShown;

		public HubContext()
		{
			_ui.CreateControl();
			_engine = new HubEngine(HubConfig.Load());

			ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Malgun Gothic", 9f) };
			_miHead = new ToolStripMenuItem("SDLS 텔레그램 허브") { Enabled = false };
			_miPause = new ToolStripMenuItem("전달 일시정지", null, (s, e) => TogglePause());
			_miAuto = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (s, e) => ToggleAutostart());
			menu.Items.Add(_miHead);
			menu.Items.Add(new ToolStripSeparator());
			menu.Items.Add(new ToolStripMenuItem("상태 보기…", null, (s, e) => ShowStatus()));
			menu.Items.Add(_miPause);
			menu.Items.Add(new ToolStripMenuItem("로그 폴더 열기", null, (s, e) => OpenFolder(HubLog.Folder)));
			menu.Items.Add(new ToolStripMenuItem("설정 파일 열기", null, (s, e) => OpenConfig()));
			menu.Items.Add(new ToolStripMenuItem("토큰 설정…", null, (s, e) => SetToken()));
			menu.Items.Add(new ToolStripSeparator());
			menu.Items.Add(_miAuto);
			menu.Items.Add(new ToolStripSeparator());
			menu.Items.Add(new ToolStripMenuItem("종료", null, (s, e) => Quit()));
			menu.Opening += (s, e) => { _engine.HasNew = false; RefreshUi(); _miAuto.Checked = Autostart.IsEnabled(); };

			_tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "SDLS 텔레그램 허브" };
			_tray.DoubleClick += (s, e) => ShowStatus();

			_engine.Changed += () => { try { if (_ui.IsHandleCreated) { _ui.BeginInvoke((Action)RefreshUi); } } catch (InvalidOperationException) { } };
			SystemEvents.UserPreferenceChanged += OnPrefChanged;
			SystemEvents.PowerModeChanged += OnPowerChanged;
			_timer.Tick += (s, e) => RefreshUi();
			_timer.Start();

			//// 처음 설치 때 자동 실행 기본 켬(H10). 등록이 확인됐을 때만 표식을 남긴다 - 실패는 로그 · 다음 시작 때 다시(2026-11-13 실측).
			//// 등록 직후 표식 전에 꺼졌다면 다음 시작에서 기존 작업을 확인하고 표식만 남긴다. 사용자가 메뉴로 바꾸면 그때 표식을 남긴다.
			if (File.Exists(AutostartMarker) == false)
			{
				string err = string.Empty;
				if (Autostart.IsEnabled() || Autostart.Enable(Application.ExecutablePath, out err))
				{
					WriteAutostartMarker("default-on");
				}
				else
				{
					HubLog.Warn("autostart : default enable failed - " + err);
				}
			}

			RefreshUi();
			_engine.Start();
			HubLog.Info("hub started " + Application.ProductVersion);
		}

		//// 절전 복귀(H8) - 살아 있는 프로세스도 재개 알림 · 보고 재조회
		private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
		{
			if (e.Mode == PowerModes.Resume) { _engine.OnResume(); }
		}

		private void OnPrefChanged(object sender, UserPreferenceChangedEventArgs e)
		{
			_shownState = (HubState)(-1);		//// 작업 표시줄 테마가 바뀌었을 수 있다 - 다시 그림
			try { _ui.BeginInvoke((Action)RefreshUi); } catch (InvalidOperationException) { }
		}

		private void RefreshUi()
		{
			HubState st = _engine.State;
			bool light = TrayIconRenderer.TaskbarIsLight();
			if (st != _shownState || light != _shownLight)
			{
				int size = SystemInformation.SmallIconSize.Width;
				using (Bitmap b = TrayIconRenderer.Draw(size, st, light))
				{
					Icon old = _icon;
					_icon = TrayIconRenderer.ToIcon(b);
					_tray.Icon = _icon;
					if (old != null) { old.Dispose(); }
				}
				_shownState = st; _shownLight = light;
			}
			string line = _engine.StatusLine;
			string tip = "SDLS 텔레그램 허브 - " + line;
			_tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
			_miHead.Text = "SDLS 텔레그램 허브 · " + line;
			_miPause.Text = _engine.Config.Paused ? "전달 재개" : "전달 일시정지";

			//// 풍선 알림 : 끊김 10 분 넘음 · 장부 오류만(H9)
			if (st == HubState.Offline)
			{
				if (_offlineSince == DateTime.MinValue) { _offlineSince = DateTime.Now; }
				if (_offlineBalloonShown == false && (DateTime.Now - _offlineSince).TotalMinutes >= 10)
				{
					_tray.ShowBalloonTip(8000, "SDLS 텔레그램 허브", "텔레그램 또는 GitHub 에 10 분 넘게 닿지 않습니다.", ToolTipIcon.Warning);
					_offlineBalloonShown = true;
				}
			}
			else { _offlineSince = DateTime.MinValue; _offlineBalloonShown = false; }
			if (_engine.Ledger.Corrupt && _corruptBalloonShown == false)
			{
				_tray.ShowBalloonTip(8000, "SDLS 텔레그램 허브", "장부가 손상되어 지시 받기를 멈췄습니다. 로그 폴더를 확인하세요.", ToolTipIcon.Error);
				_corruptBalloonShown = true;
			}
		}

		private bool _corruptBalloonShown;

		private void TogglePause()
		{
			lock (_engine.Config.Sync)
			{
				_engine.Config.Paused = !_engine.Config.Paused;
				_engine.SaveConfig();
			}
			RefreshUi();
		}

		private static string AutostartMarker
		{
			get { return Path.Combine(HubConfig.DataFolder, "autostart_default_done"); }
		}

		private static bool WriteAutostartMarker(string why)
		{
			try
			{
				Directory.CreateDirectory(HubConfig.DataFolder);
				File.WriteAllText(AutostartMarker, DateTime.Now.ToString("s") + " " + why);
				HubLog.Info("autostart : " + why);
				return true;
			}
			catch (Exception ex)
			{
				HubLog.Warn("autostart : marker write failed - " + ex.Message);
				return false;
			}
		}

		private void ToggleAutostart()
		{
			string err;
			bool wasOn = Autostart.IsEnabled();
			bool ok = wasOn ? Autostart.Disable(out err) : Autostart.Enable(Application.ExecutablePath, out err);
			if (ok)
			{
				//// 사용자가 정한 뒤로는 기본 켬을 다시 하지 않는다. 그 선택을 저장하지 못하면 알린다(코드 리뷰 - 끈 것이 다음 시작에 다시 켜질 수 있음).
				if (WriteAutostartMarker(wasOn ? "user-off" : "user-on") == false && wasOn)
				{
					MessageBox.Show("자동 실행은 껐지만 이 선택을 저장하지 못했습니다(데이터 폴더 쓰기 실패).\n다음에 허브를 시작하면 자동 실행이 다시 켜질 수 있습니다. 로그를 확인해 주세요.", "SDLS 텔레그램 허브", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				}
			}
			else
			{
				HubLog.Warn("autostart : toggle failed - " + err);
				MessageBox.Show("자동 실행 설정을 바꾸지 못했습니다.\n" + err, "SDLS 텔레그램 허브", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			_miAuto.Checked = Autostart.IsEnabled();
		}

		private void ShowStatus()
		{
			using (StatusForm f = new StatusForm(_engine)) { f.ShowDialog(); }
		}

		private void SetToken()
		{
			using (TokenForm f = new TokenForm())
			{
				if (f.ShowDialog() == DialogResult.OK && f.Token.Length > 0)
				{
					_engine.Config.SetToken(f.Token);
					_engine.SaveConfig();
					_engine.ResetClient();
					HubLog.Info("token updated");
					RefreshUi();
				}
			}
		}

		private void OpenConfig()
		{
			if (File.Exists(HubConfig.ConfigPath) == false) { _engine.SaveConfig(); }
			Process.Start("notepad.exe", "\"" + HubConfig.ConfigPath + "\"");
		}

		private static void OpenFolder(string path)
		{
			Directory.CreateDirectory(path);
			Process.Start("explorer.exe", "\"" + path + "\"");
		}

		private void Quit()
		{
			_engine.Stop();
			SystemEvents.UserPreferenceChanged -= OnPrefChanged;
			SystemEvents.PowerModeChanged -= OnPowerChanged;
			_timer.Stop();
			_tray.Visible = false;
			_tray.Dispose();
			if (_icon != null) { _icon.Dispose(); }
			HubLog.Info("hub stopped by user");
			ExitThread();
		}
	}
}
