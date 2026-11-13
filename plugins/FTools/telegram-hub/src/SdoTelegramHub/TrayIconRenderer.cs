using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SdoTelegramHub
{
	internal enum HubState { Normal, NewItem, Paused, Warn, Offline }

	//// 트레이 아이콘(H9) - 모양 B "주고받기", 바탕 없는 선, SDLS ToolsUi 색. 상태는 오른쪽 아래 배지.
	//// 24 단위 격자 좌표를 그대로 크기에 맞춰 그린다(그림 파일 없음).
	internal static class TrayIconRenderer
	{
		private static readonly Color LineLight = Color.FromArgb(0x5E, 0x4B, 0x75);
		private static readonly Color LineDark = Color.FromArgb(0xCB, 0xC1, 0xDA);
		private static readonly Color Grey = Color.FromArgb(0x9A, 0x93, 0xA6);
		private static readonly Color NewLight = Color.FromArgb(0x7A, 0x52, 0xB3);
		private static readonly Color NewDark = Color.FromArgb(0xB9, 0xA0, 0xE0);
		private static readonly Color WarnColor = Color.FromArgb(0xE6, 0x96, 0x1E);
		private static readonly Color OffColor = Color.FromArgb(0xD2, 0x3C, 0x3C);

		//// 작업 표시줄이 밝은 테마인가(SystemUsesLightTheme = 1)
		public static bool TaskbarIsLight()
		{
			try
			{
				using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
				{
					object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
					return v is int && (int)v == 1;
				}
			}
			catch (Exception) { return false; }
		}

		public static Bitmap Draw(int size, HubState state, bool lightTaskbar)
		{
			Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage(bmp))
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				g.Clear(Color.Transparent);
				float k = size / 24f;
				Color line = state == HubState.Paused ? Grey : (lightTaskbar ? LineLight : LineDark);
				float sw = Math.Max(1.6f * k, 1.15f);
				Color? badge = null;
				if (state == HubState.NewItem) { badge = lightTaskbar ? NewLight : NewDark; }
				else if (state == HubState.Warn) { badge = WarnColor; }
				else if (state == HubState.Offline) { badge = OffColor; }
				else if (state == HubState.Paused) { badge = Grey; }

				using (Pen pen = new Pen(line, sw))
				{
					pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
					if (badge.HasValue)
					{
						//// 배지 둘레를 비운다(지름 7.2 + 둘레 1.6 → 반지름 5.2)
						using (GraphicsPath hole = new GraphicsPath())
						{
							hole.AddEllipse((19.4f - 5.2f) * k, (19.4f - 5.2f) * k, 10.4f * k, 10.4f * k);
							using (Region r = new Region(new RectangleF(0, 0, size, size)))
							{
								r.Exclude(hole);
								g.Clip = r;
								DrawShape(g, pen, k);
								g.ResetClip();
							}
						}
						using (SolidBrush b = new SolidBrush(badge.Value))
						{
							g.FillEllipse(b, (19.4f - 3.6f) * k, (19.4f - 3.6f) * k, 7.2f * k, 7.2f * k);
						}
					}
					else
					{
						DrawShape(g, pen, k);
					}
				}
			}
			return bmp;
		}

		//// 위 : 오른쪽으로 나가는 화살표(보고), 아래 : 왼쪽으로 들어오는 화살표(지시)
		private static void DrawShape(Graphics g, Pen pen, float k)
		{
			g.DrawLine(pen, 4f * k, 8.5f * k, 17.5f * k, 8.5f * k);
			g.DrawLines(pen, new[] { new PointF(14f * k, 5f * k), new PointF(17.5f * k, 8.5f * k), new PointF(14f * k, 12f * k) });
			g.DrawLine(pen, 19f * k, 15.5f * k, 5.5f * k, 15.5f * k);
			g.DrawLines(pen, new[] { new PointF(9f * k, 12f * k), new PointF(5.5f * k, 15.5f * k), new PointF(9f * k, 19f * k) });
		}

		public static Icon ToIcon(Bitmap bmp)
		{
			IntPtr h = bmp.GetHicon();
			try
			{
				using (Icon tmp = Icon.FromHandle(h))
				{
					return (Icon)tmp.Clone();
				}
			}
			finally
			{
				DestroyIcon(h);
			}
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool DestroyIcon(IntPtr hIcon);
	}
}
