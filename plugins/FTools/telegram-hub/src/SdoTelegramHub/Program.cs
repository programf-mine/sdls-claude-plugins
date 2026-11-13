using System;
using System.Threading;
using System.Windows.Forms;

namespace SdoTelegramHub
{
	//// SDLS 텔레그램 허브 - 트레이 상주. 설계 : SDLS 00B. DOC/003. Claude/Automation/relay/ResidentAgent_Telegram_Design.html
	//// 사용 : SdoTelegramHub.exe            트레이 실행(PC 당 하나 - 뮤텍스)
	////       SdoTelegramHub.exe --selftest 파서 · 거르기 · 장부 자체 시험(콘솔 출력 없이 결과 파일)
	internal static class Program
	{
		[STAThread]
		private static int Main(string[] args)
		{
			if (args.Length > 0 && args[0] == "--selftest")
			{
				return SelfTest.Run(args.Length > 1 ? args[1] : null);
			}

			bool created;
			using (Mutex m = new Mutex(true, @"Local\SdoTelegramHub", out created))
			{
				if (created == false)
				{
					return 3;		//// 이미 떠 있다(자동 실행 · 절전 해제 트리거가 겹쳐도 하나만)
				}
				Application.EnableVisualStyles();
				Application.SetCompatibleTextRenderingDefault(false);
				System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
				Application.Run(new HubContext());
				GC.KeepAlive(m);
			}
			return 0;
		}
	}
}
