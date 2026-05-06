---
name: sdls-manual
description: SDLS 매뉴얼 작성 시 공통으로 쓰이는 스크린 캡처 자동화 라이브러리, 소스 기반 검증 원칙, 사용자가 반복 교정한 글쓰기 규칙 (평범한 말 쓰기, 예시 표시, td 목록 간격 등 공용 common.css 규약 포함). HTML/CSS 전체 레이아웃 (h1/h2 구조, 카드 디자인 등) 은 팀원별로 자유도 있음.
---

# SDLS Manual — 스크린 캡처 & 검증 가이드

SDLS 매뉴얼 (`DOC/Manual/*.html`, `DOC/Tech/*.html`, `DOC/External/**/*.html`) 작성 시 반복 사용되는 **캡처 자동화 + 검증 원칙** 을 모아둔 스킬입니다. HTML/CSS 구조·레이아웃 컨벤션은 작성자마다 다를 수 있으므로 제외했고, **"이 부분만큼은 일관되게 가야 한다"** 는 부분만 담았습니다.

---

## 1. 언제 이 스킬을 쓰는가

- SDVision.exe (또는 다른 WinForms 기반 앱) 의 UI 를 매뉴얼용으로 캡처해야 할 때
- 캡처한 이미지에 검은 테두리 (그림자 margin) 가 보이거나, 캡처 때마다 창 크기가 달라지는 현상을 피하고 싶을 때
- 버튼 클릭 → 팝업 등장 → 캡처 → 팝업 자동 닫기 플로우가 필요할 때
- 매뉴얼에 기재하는 버튼 레이블·동작이 실제 소스와 일치하는지 검증하고 싶을 때

---

## 2. 캡처 자동화 라이브러리

`.claude/skills/sdls-manual/_capture_lib.ps1` 에 재사용 가능한 PowerShell 유틸이 들어 있습니다. **SDLS repo 안에서 쓸 때는 `DOC/Manual/img/capture/_capture_lib.ps1` 를 dot-source** 하는 것을 기본으로 합니다 (해당 위치는 이미 repo 에 반영된 master copy).

### 핵심 함수

| 함수 | 용도 | 주의 |
|---|---|---|
| `Find-ChildWindow -ProcessName <name> [-TitleLike <pat>]` | 프로세스의 top-level 창 검색 | 여러 개 반환 시 필터링 |
| `Get-WindowState -Handle <hwnd>` | `Normal` / `Minimized` / `Maximized` 반환 | `GetWindowPlacement` 기반 |
| `Bring-ToForeground -Handle <hwnd>` | 창 상태 보존하며 전면으로 | **Normal·Maximized 상태에선 `ShowWindow` 절대 호출 안 함** (사용자가 잡아둔 크기·위치 유지) |
| `Capture-Window -Handle <hwnd> -OutPath <png>` | DWM extended frame bounds 사용해 **그림자 margin 제거** 후 PNG 저장 | `PrintWindow` + crop |
| `Click-ButtonByHandle -Handle <hwnd>` | 버튼 HWND 에 `BM_CLICK` 전송 | **기본 `PostMessage` (비동기)** — 모달 다이얼로그 띄우는 버튼에 `-Synchronous` 쓰면 **영구 블록** |
| `Click-ButtonByName -ParentHandle <hwnd> -Name <text>` | UIA 로 버튼 검색 후 BM_CLICK | 이름 중복 시 첫 매치 |
| `Close-Window -Handle <hwnd>` | WM_CLOSE 전송 | dismiss button 탐색 실패 시 fallback |

### 전형적인 사용 패턴

```powershell
. "$PSScriptRoot\_capture_lib.ps1"    # 또는 $PWD\DOC\Manual\img\capture\_capture_lib.ps1

# 1) 메인 창 앞으로
$main = Find-ChildWindow -ProcessName SDVision -TitleLike 'SDLS*'
Bring-ToForeground -Handle $main.Handle | Out-Null

# 2) 팝업 띄우는 버튼 클릭 (PostMessage, 비동기)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle($main.Handle)
$btn = $root.FindAll([...]::Descendants, [...]::TrueCondition) |
       Where-Object { $_.Current.Name -eq 'Edit' -and $_.Current.ClassName -like '*BUTTON*' } |
       Select-Object -First 1
Click-ButtonByHandle -Handle ([IntPtr]$btn.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 900

# 3) 새로 뜬 팝업 검출
$popup = Find-ChildWindow -ProcessName SDVision |
         Where-Object { $_.Handle -ne $main.Handle } | Select-Object -First 1

# 4) 팝업 캡처
Bring-ToForeground -Handle $popup.Handle | Out-Null
Capture-Window -Handle $popup.Handle -OutPath 'popup.png' | Out-Null

# 5) 팝업 자체의 Cancel/No/OK 버튼으로 닫기 (WM_CLOSE fallback 전에)
$dismiss = ... (Cancel/No/OK 중 존재하는 것 찾기)
Click-ButtonByHandle -Handle ([IntPtr]$dismiss.Current.NativeWindowHandle)
```

---

## 3. 중요한 함정 & 교훈

실제 작업 중 삽질했던 포인트들. **새로 하는 사람도 같은 함정을 안 밟도록** 먼저 읽어주세요.

### 3-1. `ShowWindow(SW_RESTORE)` 는 최대화된 창을 축소시킴
- 증상: 캡처할 때마다 메인 창 크기·위치가 달라져서 매뉴얼 기준 해상도가 흔들림.
- 원인: `SW_RESTORE` 는 *이전 상태로 복귀* — 현재 최대화면 normal 크기로 "축소" 됨.
- 해결: `GetWindowPlacement` 로 상태 확인 후 **Minimized 일 때만** `SW_RESTORE`. 나머진 `SetForegroundWindow` 만. → `Bring-ToForeground` 가 이 로직을 내장.

### 3-2. 캡처 결과에 검은 테두리
- 증상: 저장된 PNG 각 변에 7~9 px 검은 margin.
- 원인: Windows 10/11 창은 `GetWindowRect` 에 **invisible drop shadow margin** 을 포함해 반환. 해당 영역은 `PrintWindow` 가 렌더링하지 않아 bitmap 기본색 (검정) 으로 남음.
- 해결: `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS, ...)` 로 실제 visible bounds 계산 → crop. → `Capture-Window` 가 자동 처리.

### 3-3. `SendMessage(BM_CLICK)` 은 모달 다이얼로그에서 블록됨
- 증상: `btnRename` 같은 버튼에 `SendMessage` 로 BM_CLICK 했더니 스크립트가 멈춰서 후속 캡처가 모두 실패.
- 원인: `SendMessage` 는 **동기** — 콜백이 반환할 때까지 대기. 버튼이 모달 dialog (`ShowDialog()`) 를 열면 해당 dialog 가 닫힐 때까지 반환하지 않음.
- 해결: **`PostMessage`** (비동기) 사용. `Click-ButtonByHandle` 은 기본 PostMessage. 버튼이 모달을 안 띄우는 것이 확실할 때만 (`-Synchronous`) 사용.

### 3-4. `mouse_event` 는 UIPI/권한 경계에서 조용히 실패
- 증상: `SetCursorPos` 는 동작해서 마우스 커서는 움직이지만 `mouse_event` 클릭이 타겟 창에 안 전달됨.
- 원인: SDVision 이 관리자 권한, PowerShell 이 일반 권한이면 UIPI 가 raw input 차단.
- 해결: 창 메시지 기반 `PostMessage(BM_CLICK)` 로 대체. UIPI 를 우회해서 동작.

### 3-5. 크로스-프로세스 SendMessage 에 로컬 struct 포인터 전달 금지 (프로세스 크래시)
- 증상: `LVM_SETITEMSTATE` 를 로컬 `LVITEM` struct 로 호출했더니 **타겟 프로세스 (SDVision) 가 즉시 크래시**.
- 원인: `SendMessage(hwnd, LVM_SETITEMSTATE, idx, &lvitem)` 에서 lparam 은 포인터 — 타겟 프로세스가 **자신의 주소공간에서 그 포인터를 dereference** 함. 내가 넘긴 주소는 PowerShell 프로세스의 주소라 타겟 입장에서 쓰레기 메모리 → access violation.
- 해결: 포인터를 lparam 으로 받는 API 를 크로스-프로세스에 그대로 쓰지 말 것. 선택지:
  - **키보드 네비게이션** (권장): `SetFocus(lv)` → `PostMessage(WM_KEYDOWN VK_HOME)` → `PostMessage(WM_KEYDOWN VK_DOWN) × N` → 포커스/선택 변경 자연 발생
  - **마우스 클릭**: `PostMessage(lv, WM_LBUTTONDOWN/UP, ..., MAKELPARAM(x,y))` — client 좌표면 struct 불필요
  - **실제 struct 가 필요한 경우**: `VirtualAllocEx + WriteProcessMemory` 로 타겟 프로세스 주소공간에 struct 복사 후 그 주소를 lparam 으로 전달
- `_capture_lib.ps1` 의 `Select-LVRowByKeyboard` 가 안전한 예시 ─ `_capture_each_item.ps1` 에서 사용.

### 3-6. WCT_Button 은 UIA 에서 `ControlType=Pane`
- 증상: `ControlType=Button` 필터로 찾으면 안 잡힘.
- 원인: WCT_Button 은 Windows Forms 커스텀 버튼 — UIA 가 표준 button 으로 인식하지 않음.
- 해결: `Name` + `ClassName -like '*BUTTON*'` 로 필터. `InvokePattern` 대신 `NativeWindowHandle` 얻어서 `BM_CLICK` 수동 전송.

---

## 4. 매뉴얼 기재 전 검증 원칙

> **"UI 캡처에 보이는 텍스트" 와 "실제 소스의 텍스트" 는 다를 수 있다** — 항상 소스를 신뢰.

### 4-1. 버튼 레이블은 `.Designer.cs` 의 `.Text` 로 검증

버튼 폭이 좁으면 GDI 가 **텍스트를 잘라서 표시** 합니다. 실제 사례:

| 캡처에 보이는 것 | 소스의 실제 텍스트 |
|---|---|
| `Renam` | `Rename` (`btnGroupRename.Text = "Rename"`) |
| `Del` | `Delete` |

→ 버튼 언급 전에 **반드시** grep 으로 확인:
```
Grep pattern = btn<Name>\.Text\s*=  in <ProjectName>.Designer.cs
```

### 4-2. 동작 설명은 Click 이벤트 핸들러로 검증

"이 버튼을 누르면 XX 가 일어납니다" 라고 쓰기 전에 해당 버튼의 `.Click += ...` 핸들러를 읽어 실제 로직 확인. 예:

- `btnGroupAdd_Click` → 유효성 실패 시 `FRM_Message_Ok.Show("Group Empty!...")` 경고창이 뜬다는 사실은 소스 읽어야 알 수 있음 (UI 상으론 "Add 누르면 뭐가 생기나?" 만 보임).
- `btnGroupDelete_Click` → `FRM_Message_YesNo.Show("Are you sure want to delete ?")` 확인 창이 먼저 뜸. `No` 하면 취소.

### 4-3. UIA Name 과 ClassName 확인 시 주의

- `AutomationElement.Current.Name` 은 보통 `.Text` 와 같지만, 일부 커스텀 컨트롤은 내부 `AccessibleName` 이 따로 설정되어 다를 수 있음.
- 매뉴얼 기재 시에는 **소스의 `.Text`** 가 우선.

---

## 5. 글쓰기 스타일 규칙 (사용자 반복 교정 사항)

아래는 실제 매뉴얼 작성 중 사용자가 반복해서 지적·교정한 포인트입니다. **처음부터 이 규칙을 따르면 같은 피드백을 받지 않아도 됩니다.**

### 5-1. 엔지니어 은어 대신 평범한 말 쓰기

| 피해야 할 표현 | 대신 쓸 표현 |
|---|---|
| `Profile × Camera` (수학 표기) | `Profile 과 Camera 조합` / `Profile + Camera 조합` |
| `전역 감사 로그` | `언제·무엇을·어떻게 바꿨는지 자동으로 남는 변경 이력` |
| `SingleShot 등` (존재하지 않는 값) | 소스의 **실제 enum 멤버** 만 나열 (`StitchedLayer / RawImage / EDOF / None`) |
| `듀얼 카메라` (2대 한정 뉘앙스) | `멀티 카메라` (2대 이상 포괄) |

핵심 원칙: **사용자가 도메인 지식 없이 읽어도 이해되는지** 를 기준으로.

### 5-2. 예시는 줄바꿈 + 라벨로 구분

규칙/설명 문장 안에 예시가 섞이면 가독성이 떨어집니다.

**나쁨**: `... 이미지 번호가 붙음. 예: Top → Top-0, Top-1.`
**좋음**:
```html
... 이미지 번호가 붙음.<br>
<span class="ex-label">예</span><code>Top</code> → <code>Top-0</code>, <code>Top-1</code>.
```

- `<br>` 로 줄바꿈 후
- `<span class="ex-label">예</span>` 로 작은 pill 라벨 표시
- (해당 CSS 는 `DOC/common.css` 에 정의됨)

### 5-3. 카드/인포 박스의 "예:" 도 동일 규칙

concept-card 등 작은 카드 내에서도 본문과 예시 사이에 `<br>` 을 넣어 한 줄 띄우기.

### 5-4. 표 td 안 목록 (ul.inline) 수직 간격

`td ul.inline li` 를 여러 줄 설명에 쓸 때 기본 `margin: 2px 0` 으로는 너무 빽빽. 현재는 `margin: 8px 0; line-height: 1.6` 이 표준 (common.css 에 반영됨). 페이지별 override 금지.

### 5-5. 내부 변수명/컨트롤 ID 노출 금지

사용자 매뉴얼에 `btnXxx`, `rdoYyy`, `prm_Zzz` 같은 내부 이름 **적지 않기**. Tech 매뉴얼에선 필요 시 가능.

### 5-6. 숨겨진 입력 룰 / 특수 문자열 반드시 기록

필드 설명을 쓸 때 소스에 숨겨진 특수 동작이 있는지 반드시 확인:

- 상수 접두어 (`#FORCE_`, `#ORIGINSTEP`, `INSTANT_` 등)
- 빈 값일 때 자동 생성되는 기본값 (`ImageName` 이 비면 `GrabType` 사용)
- 프로젝트 코드별 override (TGV / EUV / ABF 등 특정 `AppCode` 에서만 동작하는 로직)

이런 숨은 룰을 놓치면 "사용자가 알면 훨씬 편한데 문서에 없음" 상태가 됨.

### 5-7. 버튼 레이블은 시각 캡처 대신 `.Text` 소스로

(이미 § 4-1 에 있음 — 재강조)

- UI 캡처에서 잘려 보이는 "Renam" 은 실제 `"Rename"`
- `.Designer.cs` 의 `.Text = "..."` 이 진실

### 5-8. 목록 항목 구분자는 em-dash 대신 콜론 (`:`)

"값 → 설명" 형태의 목록에서 em-dash (`—`) 는 CJK 폰트에서 전각으로 렌더링되어 짧은 값 뒤에 **과도하게 길게** 보입니다.

**나쁨**:
```
비워둠 — Strobe 비활성화
1    — 포트 1 만 On
1,2  — 두 포트 모두 On
```

**좋음** (콜론 사용):
```
비워둠 : Strobe 비활성화
1    : 포트 1 만 On
1,2  : 두 포트 모두 On
```

적용 규칙:
- **짧은 키 뒤의 구분자**: em-dash 대신 **`:`** (콜론 + 공백)
- 입력값 → 결과 매핑에 특히 적합 (콜론이 "~이면" 의미로 자연스러움)
- 긴 서술문 중간 삽입구에는 em-dash 를 그대로 써도 됨 (예: "... 알고리즘 — 정식 명칭 SDAF — 은 ...")
- 가운뎃점 (`·`) 은 enum 나열에 쓰지 말 것 (§ 5-1, 쉼표 사용)

---

## 6. 작업 순서 체크리스트

매뉴얼 한 페이지를 작성할 때 반복하는 흐름입니다.

1. **소스 분석**
   - `PAGE_*.cs` + `PAGE_*.Designer.cs` 읽기
   - 주요 컨트롤 이름·타입·이벤트 핸들러 파악
   - 관련 Manager (SDL_ProfileManager, LightProfileManager 등) 읽어서 데이터 저장 범위·사이드 이펙트 확인

2. **사용자 관점 설명 작성**
   - 내부 변수명 (btnXxx, rdoYyy) 은 매뉴얼에 노출 금지
   - 기능별 용도·동작 순서로 기술

3. **스크린샷 캡처**
   - 전체 화면 오버뷰: 1920×1040 (원본). `BitBlt` 또는 `PrintWindow` 로.
   - 서브 탭 상세: UIA 로 시작·끝 컨트롤 bounds 찾아 rect 계산 → `BitBlt` → 보통 720px wide 로 리사이즈
   - 팝업: `Capture-Window` 로 DWM crop → 보통 420px wide 로 리사이즈
   - 리사이즈는 **HighQualityBicubic** interpolation

4. **버튼 동작 샘플 캡처**
   - 각 상호작용 버튼 (Add / Rename / Delete 등) 의 대표 팝업을 실제로 띄워서 캡처
   - 클릭: `Click-ButtonByHandle` (PostMessage)
   - dismiss: 팝업의 Cancel/No/OK 버튼을 탐색해서 다시 `Click-ButtonByHandle` → 실패 시 `Close-Window` fallback

5. **소스 대조 검증**
   - 매뉴얼에 기재된 모든 버튼 레이블·경고 메시지 문자열을 grep 으로 소스와 대조
   - 동작 설명은 이벤트 핸들러 로직과 맞춘다

6. **내부·외부 매뉴얼 분리** (해당되는 경우)
   - 내부: `DOC/Manual/*.html` + `DOC/Tech/*.html`
   - 외부 배포용: `DOC/External/Manual/*.html` + `DOC/External/Tech/*.html` — 내부 정보 (구현 참고, 코드 경로 등) 제거

7. **노트북 (클라이언트) 동기화** — 배치로 한 번에, `Y:\020. VS\SDLS` (별도 메모리 참조)

---

## 7. 캡처 스크립트 조직 관례

페이지별 캡처 스크립트는 `DOC/Manual/img/capture/_recap_<target>.ps1` 형태로 둡니다. 한 번 작성해두면 나중에 UI 변경 시 똑같이 재실행하면 되어 유지 보수가 쉽습니다.

예:
- `_recap_sequence_tab.ps1` — Sequence 탭 right-panel crop 캡처
- `_recap_seq_buttons.ps1` — Rename/Delete/Add 팝업 자동 캡처

각 스크립트는 `_capture_lib.ps1` 을 dot-source 하고, **실행 후 resize 포함** 해서 최종 매뉴얼 삽입용 PNG 를 만드는 것이 원칙입니다.

---

## 8. 이 스킬이 다루지 않는 것

다음 항목은 팀원·프로젝트마다 달라질 수 있어 **의도적으로 제외** 했습니다:

- HTML 문서 전체 구조 (h1/h2 레벨, 섹션 네이밍)
- 페이지별 카드·다이어그램·표 디자인 (`.concept-card`, `.feat-table`, `.param-table`, `.folder-tree` 등)
- 퍼플 테마 색상·폰트 선택
- 언어 전환 UI, 테마 토글 버튼
- 문서 index 배치

**예외 — `DOC/common.css` 에 포함된 공용 규약은 준수:**
- `td ul.inline li` 수직 간격 (사용자 반복 교정으로 확립)
- `.ex-label` 예시 pill (사용자 지정)
- `.topbar`, `.footer`, 테마 토글 등 이미 정의된 공통 컴포넌트

필요하다면 별도 스킬 (예: `sdls-html-conventions`) 로 HTML 템플릿 전반을 문서화할 수 있습니다.
