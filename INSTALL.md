# FTools 설치 가이드

SDLS 팀 전용 Claude Code 플러그인 `FTools` 설치 + 사용 안내.

---

## 1. 사전 요구

- **Claude Code** 설치 + 로그인 완료
- **Windows + PowerShell 5.1+** (스킬 내부 캡처 라이브러리가 PowerShell 사용)
- **인터넷 접속** (GitHub 에서 marketplace clone)

확인 방법:
```bash
claude --version
powershell -Command "$PSVersionTable.PSVersion"
```

---

## 2. 설치 (한 번만)

Claude Code 세션을 열고 입력:

```
/plugin marketplace add programf-mine/sdls-claude-plugins
```

성공 메시지가 뜨면 이어서:

```
/plugin install FTools@sdls-claude-plugins
```

---

## 3. 설치 확인

```
/plugin
```

목록에 `FTools@sdls-claude-plugins` 가 enabled 상태로 보이면 OK.

또는:

```
/sdls-manual
```

`sdls-manual` 스킬이 실행되면 정상 설치됨.

---

## 4. 사용

### 4-1. 자연어 호출 (권장)

매뉴얼 작성 / SDVision UI 캡처 / 글쓰기 규약 검토 등의 의도를 자연어로 전달:

```
SDVision 의 Edit Profile 팝업 캡처해서 매뉴얼에 넣어줘
```

```
이 매뉴얼 페이지의 글쓰기 스타일이 SDLS 규약에 맞는지 검토해줘
```

Claude 가 `sdls-manual` 스킬을 자동 활성화합니다.

### 4-2. 명시적 호출

```
/sdls-manual
```

스킬을 직접 로드해서 가이드를 볼 수 있습니다.

### 4-3. 스킬이 제공하는 것

- **PowerShell 캡처 라이브러리** (`_capture_lib.ps1`)
  - `Find-ChildWindow` — 프로세스 창 검색
  - `Bring-ToForeground` — 창 상태 보존하며 전면화
  - `Capture-Window` — 그림자 margin 제거된 PNG 저장
  - `Click-ButtonByHandle` / `Click-ButtonByName` — BM_CLICK (모달 안전)
  - `Close-Window` — WM_CLOSE 전송
- **함정 6가지 회피 가이드** — SW_RESTORE 축소, 검은 테두리, SendMessage 모달 블록 등
- **검증 원칙** — `.Designer.cs::.Text` 우선
- **글쓰기 규약** — 평범한 말 / 예시 라벨 / em-dash 회피 등

---

## 5. 업데이트

플러그인 새 버전이 나오면:

```
/plugin marketplace update sdls-claude-plugins
/plugin update FTools
```

---

## 6. 비활성화 / 제거

비활성화 (재활성 가능):
```
/plugin disable FTools
```

완전 제거:
```
/plugin uninstall FTools
```

마켓플레이스도 제거:
```
/plugin marketplace remove sdls-claude-plugins
```

---

## 7. 문제 해결

### 7-1. `marketplace add` 실패

| 증상 | 원인 | 해결 |
|---|---|---|
| `not found` | repo URL 오타 | `programf-mine/sdls-claude-plugins` 정확히 입력 |
| `permission denied` | private repo + 인증 미완 | GitHub 로그인 (`gh auth login` 또는 OS 자격 증명) |
| `network error` | 회사 방화벽 | github.com 접근 가능한 환경에서 재시도 |

### 7-2. `install` 후 `/sdls-manual` 안 잡힘

```
/plugin
```
로 활성 상태 확인. `disabled` 면 `/plugin enable FTools`. 그래도 안 되면 Claude Code 재시작.

### 7-3. PowerShell 스크립트 실행 거부

PowerShell 실행 정책 확인:
```powershell
Get-ExecutionPolicy
```
`Restricted` 이면 다음 중 하나 적용 (관리자 권한):
```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```
또는 스크립트 호출 시 `-ExecutionPolicy Bypass` 명시 (스킬이 권장하는 방식).

### 7-4. 캡처 결과에 검은 테두리

`_capture_lib.ps1` 의 `Capture-Window` 가 자동 처리. 직접 캡처 함수를 호출하는 경우 SKILL.md § 3-2 참조.

### 7-5. 모달 다이얼로그 클릭 시 멈춤

`SendMessage(BM_CLICK)` 대신 `Click-ButtonByHandle` (기본 PostMessage) 사용. SKILL.md § 3-3 참조.

---

## 8. 피드백 / 기여

- 버그 / 기능 요청: https://github.com/programf-mine/sdls-claude-plugins/issues
- 직접 수정 제안: PR
- 사내 채팅 (텔레그램 `programf_bot` 등) 도 가능

---

## 부록 — 마켓플레이스 / 플러그인 위치

| 항목 | 경로 |
|---|---|
| 마켓플레이스 repo | https://github.com/programf-mine/sdls-claude-plugins |
| 플러그인 매니페스트 | `plugins/FTools/.claude-plugin/plugin.json` |
| 스킬 본문 | `plugins/FTools/skills/sdls-manual/SKILL.md` |
| 캡처 라이브러리 | `plugins/FTools/skills/sdls-manual/_capture_lib.ps1` |

설치 시 Claude Code 가 위 repo 를 로컬 캐시 (`~/.claude/plugins/marketplaces/sdls-claude-plugins/`) 에 clone 합니다.
