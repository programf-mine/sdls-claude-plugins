using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace SdoTelegramHub
{
	//// Windows 시작 시 자동 실행(H10) - 작업 스케줄러 \SDOPTICS\SdoTelegramHub. 관리자 권한 없이 현재 사용자 작업으로 등록.
	//// 트리거 : 로그온 · 절전 해제(Power-Troubleshooter 1). 실패 시 1 분 뒤 최대 3 회. 로그온한 경우에만 실행.
	internal static class Autostart
	{
		public const string TaskName = @"\SDOPTICS\SdoTelegramHub";

		public static bool IsEnabled()
		{
			return Schtasks("/Query /TN \"" + TaskName + "\"", out _) == 0;
		}

		public static bool Enable(string exePath, out string error)
		{
			string user = WindowsIdentity.GetCurrent().Name;
			string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
"<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
"  <RegistrationInfo><Description>SDLS 텔레그램 허브 - 로그온 · 절전 해제 때 트레이로 실행</Description></RegistrationInfo>\n" +
"  <Triggers>\n" +
"    <LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId></LogonTrigger>\n" +
"    <EventTrigger><Enabled>true</Enabled><Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"System\"&gt;&lt;Select Path=\"System\"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription></EventTrigger>\n" +
"  </Triggers>\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
"  <Settings>\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
"    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\n" +
"  </Settings>\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exePath) + "</Command></Exec></Actions>\n" +
"</Task>\n";
			string tmp = Path.Combine(HubConfig.DataFolder, "autostart_task.xml");
			Directory.CreateDirectory(HubConfig.DataFolder);
			File.WriteAllText(tmp, xml, Encoding.Unicode);
			int rc = Schtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"", out error);
			try { File.Delete(tmp); } catch (IOException) { }
			return rc == 0;
		}

		public static bool Disable(out string error)
		{
			return Schtasks("/Delete /F /TN \"" + TaskName + "\"", out error) == 0;
		}

		private static int Schtasks(string args, out string output)
		{
			ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
			using (Process p = Process.Start(psi))
			{
				string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
				p.WaitForExit(15000);
				output = o.Trim();
				return p.HasExited ? p.ExitCode : -1;
			}
		}
	}
}
