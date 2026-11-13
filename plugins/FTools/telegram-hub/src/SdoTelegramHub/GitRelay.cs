using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SdoTelegramHub
{
	//// 허브 전용 클론(H1 · H3) - relay 폴더만 sparse. 무인 실행(입력 대기 없음), 명령당 120 s.
	internal sealed class GitRelay
	{
		private readonly HubConfig _c;
		public string RepoPath { get; private set; }

		public GitRelay(HubConfig c)
		{
			_c = c;
			RepoPath = Path.Combine(HubConfig.DataFolder, "repo");
		}

		public string RelayFolder
		{
			get { return Path.Combine(RepoPath, _c.RelayDir.Replace('/', Path.DirectorySeparatorChar)); }
		}

		public void EnsureClone()
		{
			if (Directory.Exists(Path.Combine(RepoPath, ".git"))) { return; }
			Directory.CreateDirectory(HubConfig.DataFolder);
			if (Directory.Exists(RepoPath)) { Directory.Delete(RepoPath, true); }
			Run(HubConfig.DataFolder, "clone --filter=blob:none --no-checkout --branch " + _c.Branch + " \"" + _c.RepoUrl + "\" repo");
			Run(RepoPath, "sparse-checkout init --cone");
			Run(RepoPath, "sparse-checkout set \"" + _c.RelayDir + "\"");
			Run(RepoPath, "checkout " + _c.Branch);
		}

		public string RemoteHead()
		{
			string o = Run(RepoPath, "ls-remote origin refs/heads/" + _c.Branch).Trim();
			int t = o.IndexOf('\t');
			return t > 0 ? o.Substring(0, t) : o;
		}

		//// 전용 클론은 허브만 쓴다 - push 되지 않은 로컬 상태는 버리고 원격과 같게(H3 6)
		public void SyncHard()
		{
			Run(RepoPath, "fetch --quiet origin " + _c.Branch);
			Run(RepoPath, "reset --hard --quiet origin/" + _c.Branch);
			//// 추적 안 된 파일(최초 생성 뒤 add 전에 꺼진 경우)도 지운다 - 표식을 로컬 파일에서 잘못 찾지 않게(코드 리뷰 ③)
			Run(RepoPath, "clean -fdq");
		}

		//// 원격(origin/<branch>) 커밋의 파일 내용. 파일이 없으면 빈 문자열.
		public string RemoteFile(string relPath)
		{
			try { return Run(RepoPath, "show \"origin/" + _c.Branch + ":" + relPath + "\""); }
			catch (GitException ex) when (ex.Message.Contains("does not exist") || ex.Message.Contains("exists on disk, but not in")) { return string.Empty; }
		}

		public string HeadCommit() { return Run(RepoPath, "rev-parse HEAD").Trim(); }

		public void CommitAndPush(string message, params string[] relPaths)
		{
			foreach (string p in relPaths) { Run(RepoPath, "add -- \"" + p + "\""); }
			string msgFile = Path.Combine(HubConfig.DataFolder, "commitmsg.txt");
			File.WriteAllText(msgFile, message, new UTF8Encoding(false));
			Run(RepoPath, "-c user.name=SdoTelegramHub -c user.email=telegram-hub@sdoptics.local commit --quiet -F \"" + msgFile + "\"");
			Run(RepoPath, "push --quiet origin HEAD:" + _c.Branch);
		}

		public string Run(string cwd, string args)
		{
			ProcessStartInfo psi = new ProcessStartInfo("git", args)
			{
				WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
			};
			psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
			psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
			psi.EnvironmentVariables["GIT_CONFIG_PARAMETERS"] = "'safe.directory=*' 'core.quotepath=false'";
			using (Process p = Process.Start(psi))
			{
				//// 두 출력 모두 비동기로 읽고 프로세스 종료만 기다린다 - stdout 을 동기로 읽으면 120 s 제한이 걸리지 않는다(코드 리뷰 ④)
				System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
				System.Threading.Tasks.Task<string> outp = p.StandardOutput.ReadToEndAsync();
				if (p.WaitForExit(120000) == false)
				{
					try { p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
					throw new GitException("git " + FirstWord(args) + " : timed out after 120 s");
				}
				p.WaitForExit();
				string outText = outp.Wait(5000) ? outp.Result : string.Empty;
				string errText = err.Wait(5000) ? err.Result : string.Empty;
				if (p.ExitCode != 0) { throw new GitException("git " + FirstWord(args) + " exit " + p.ExitCode + " : " + errText.Trim()); }
				return outText;
			}
		}

		private static string FirstWord(string a) { int i = a.IndexOf(' '); return i > 0 ? a.Substring(0, i) : a; }
	}

	internal sealed class GitException : Exception
	{
		public GitException(string m) : base(m) { }
	}
}
