# ============================================================================
# _capture_lib.ps1  —  SDLS 매뉴얼 캡처용 공용 유틸
#
# 사용법:
#   . "$PSScriptRoot\_capture_lib.ps1"        # dot-source 로 로드
#   $hwnd = Find-ChildWindow -ProcessName SDVision -TitleLike '*Edit Profile*'
#   Bring-ToForeground -Handle $hwnd
#   Capture-Window    -Handle $hwnd -OutPath "...png"
#
# 핵심 동작:
#   * Bring-ToForeground — 창 상태별 분기
#       - Minimized  → SW_RESTORE (이전 상태 복귀)
#       - Maximized  → ShowWindow 생략, SetForegroundWindow 만 호출
#       - Normal     → ShowWindow 생략, SetForegroundWindow 만 호출
#     → 사용자가 설정한 창 크기/위치를 절대 건드리지 않음
#
#   * Capture-Window — 그림자 margin 제거
#       - DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) 로 실제 bounds 계산
#       - GetWindowRect (그림자 margin 포함) 와의 차이만큼 crop
#       - 결과: 검은 테두리 없는 정확한 창 이미지
# ============================================================================

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class CapLib {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT pvAttribute, int cbAttribute);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    public const int SW_HIDE            = 0;
    public const int SW_SHOWNORMAL      = 1;
    public const int SW_SHOWMINIMIZED   = 2;
    public const int SW_SHOWMAXIMIZED   = 3;
    public const int SW_SHOW            = 5;
    public const int SW_RESTORE         = 9;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
}
"@ -ReferencedAssemblies System.Drawing

# ---------------------------------------------------------------------------
# Find-ChildWindow : 프로세스의 자식 top-level 창 검색
# ---------------------------------------------------------------------------
function Find-ChildWindow {
    param(
        [Parameter(Mandatory)] [string] $ProcessName,
        [string] $TitleLike,
        [string] $TitleExact
    )
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { return $null }
    $targetPid = $proc.Id
    $found = New-Object System.Collections.ArrayList
    $cb = [CapLib+EnumWindowsProc]{
        param($hWnd, $lParam)
        $wpid = 0
        [CapLib]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $targetPid -and [CapLib]::IsWindowVisible($hWnd)) {
            $sb = New-Object System.Text.StringBuilder 256
            [CapLib]::GetWindowText($hWnd, $sb, 256) | Out-Null
            $null = $found.Add([PSCustomObject]@{ Handle = $hWnd; Title = $sb.ToString() })
        }
        return $true
    }
    [CapLib]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

    if ($TitleExact) { return ($found | Where-Object { $_.Title -eq $TitleExact } | Select-Object -First 1) }
    if ($TitleLike)  { return ($found | Where-Object { $_.Title -like $TitleLike } | Select-Object -First 1) }
    return $found
}

# ---------------------------------------------------------------------------
# Get-WindowState : 현재 창 상태 반환 ('Normal' / 'Minimized' / 'Maximized')
# ---------------------------------------------------------------------------
function Get-WindowState {
    param([Parameter(Mandatory)] [IntPtr] $Handle)
    $wp = New-Object CapLib+WINDOWPLACEMENT
    $wp.length = [System.Runtime.InteropServices.Marshal]::SizeOf($wp)
    [CapLib]::GetWindowPlacement($Handle, [ref]$wp) | Out-Null
    switch ($wp.showCmd) {
        ([CapLib]::SW_SHOWMINIMIZED) { return 'Minimized' }
        ([CapLib]::SW_SHOWMAXIMIZED) { return 'Maximized' }
        default                      { return 'Normal' }
    }
}

# ---------------------------------------------------------------------------
# Bring-ToForeground : 사용자의 창 크기/위치를 절대 변경하지 않고 전면으로
# ---------------------------------------------------------------------------
function Bring-ToForeground {
    param([Parameter(Mandatory)] [IntPtr] $Handle)
    $state = Get-WindowState -Handle $Handle
    if ($state -eq 'Minimized') {
        # 최소화 상태에서만 SW_RESTORE 허용 — 이전 상태 (normal/max) 로 복귀
        [CapLib]::ShowWindow($Handle, [CapLib]::SW_RESTORE) | Out-Null
        Start-Sleep -Milliseconds 200
    }
    # Normal / Maximized → ShowWindow 건드리지 않음 (사용자 크기 유지)
    [CapLib]::SetForegroundWindow($Handle) | Out-Null
    Start-Sleep -Milliseconds 250
    return $state
}

# ---------------------------------------------------------------------------
# Capture-Window : 그림자 margin 제거한 정확한 창 캡처
# ---------------------------------------------------------------------------
function Capture-Window {
    param(
        [Parameter(Mandatory)] [IntPtr] $Handle,
        [Parameter(Mandatory)] [string] $OutPath
    )

    # 1) GetWindowRect : 그림자 margin 포함
    $rFull = New-Object CapLib+RECT
    [CapLib]::GetWindowRect($Handle, [ref]$rFull) | Out-Null
    $fullW = $rFull.Right - $rFull.Left
    $fullH = $rFull.Bottom - $rFull.Top
    if ($fullW -le 0 -or $fullH -le 0) { Write-Host "Invalid window size"; return $false }

    # 2) DwmGetWindowAttribute : 실제 visible bounds (그림자 제외)
    $rDwm = New-Object CapLib+RECT
    $ok = [CapLib]::DwmGetWindowAttribute($Handle, [CapLib]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$rDwm, [System.Runtime.InteropServices.Marshal]::SizeOf($rDwm))
    if ($ok -ne 0) {
        # DWM 실패 시 fallback : 전체 영역 사용
        $rDwm = $rFull
    }

    # 3) 그림자 margin 계산 (GetWindowRect 기준으로 DWM rect 가 얼마나 안쪽인가)
    $offX = $rDwm.Left - $rFull.Left
    $offY = $rDwm.Top  - $rFull.Top
    $dwmW = $rDwm.Right - $rDwm.Left
    $dwmH = $rDwm.Bottom - $rDwm.Top

    # 4) 전체 영역으로 PrintWindow → visible 영역만 crop
    $bmpFull = New-Object System.Drawing.Bitmap $fullW, $fullH
    $g = [System.Drawing.Graphics]::FromImage($bmpFull)
    $hdc = $g.GetHdc()
    $ok = [CapLib]::PrintWindow($Handle, $hdc, [CapLib]::PW_RENDERFULLCONTENT)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if (-not $ok) { $bmpFull.Dispose(); Write-Host "PrintWindow failed"; return $false }

    # 5) Crop
    $cropRect = New-Object System.Drawing.Rectangle $offX, $offY, $dwmW, $dwmH
    $bmpCrop = $bmpFull.Clone($cropRect, $bmpFull.PixelFormat)
    $bmpFull.Dispose()

    $bmpCrop.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmpCrop.Dispose()
    Write-Host "Saved: $OutPath  ($dwmW x $dwmH, cropped from $fullW x $fullH)"
    return $true
}

# ---------------------------------------------------------------------------
# Close-Window : WM_CLOSE 전송
# ---------------------------------------------------------------------------
function Close-Window {
    param([Parameter(Mandatory)] [IntPtr] $Handle)
    [CapLib]::PostMessage($Handle, [CapLib]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}

# ---------------------------------------------------------------------------
# Click-ButtonByHandle : 대상 버튼 HWND 에 BM_CLICK 전송
#   - BM_CLICK = 0x00F5 : 버튼의 클릭 이벤트 핸들러를 직접 실행
#   - mouse_event 와 달리 UIPI / 포커스에 영향 받지 않음
# ---------------------------------------------------------------------------
function Click-ButtonByHandle {
    param(
        [Parameter(Mandatory)] [IntPtr] $Handle,
        [switch] $Synchronous   # 모달 다이얼로그를 띄우는 버튼엔 사용 금지 (블록됨)
    )
    # BM_CLICK = 0x00F5
    # 기본: PostMessage (비동기) — 모달 다이얼로그 대응
    # Synchronous: SendMessage — 즉시 처리 필요 시 (radio button 등)
    if ($Synchronous) {
        [CapLib]::SendMessage($Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    } else {
        [CapLib]::PostMessage($Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
}

# ---------------------------------------------------------------------------
# Click-ButtonByName : UIA 로 버튼 검색 후 BM_CLICK
# ---------------------------------------------------------------------------
function Click-ButtonByName {
    param(
        [Parameter(Mandatory)] [IntPtr] $ParentHandle,
        [Parameter(Mandatory)] [string] $Name,
        [scriptblock] $Filter
    )
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($ParentHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $candidates = @()
    foreach ($el in $all) {
        if ($el.Current.Name -eq $Name -and $el.Current.ClassName -like '*BUTTON*') {
            $candidates += $el
        }
    }
    if ($Filter) { $candidates = @($candidates | Where-Object $Filter) }
    $el = $candidates | Select-Object -First 1
    if (-not $el) { Write-Host "Click-ButtonByName: '$Name' not found"; return $false }
    $hwnd = [IntPtr]($el.Current.NativeWindowHandle)
    Click-ButtonByHandle -Handle $hwnd
    return $true
}
