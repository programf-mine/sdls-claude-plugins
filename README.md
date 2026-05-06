# sdls-claude-plugins

Claude Code 플러그인 마켓플레이스 — SDLS 팀 내부 도구 모음.

## 설치

상세 안내: **[DOC/INSTALL.md](DOC/INSTALL.md)** (사전 요구 / 설치 / 사용 / 업데이트 / 문제 해결)

빠른 시작:
```
/plugin marketplace add programf-mine/sdls-claude-plugins
/plugin install FTools@sdls-claude-plugins
```

## 포함된 플러그인

| 플러그인 | 설명 | 주요 자산 |
|---|---|---|
| [FTools](plugins/FTools/) | SDLS 매뉴얼 작성 + 캡처 자동화 도구 | `sdls-manual` 스킬 |

### FTools

- **sdls-manual 스킬** — WinForms 기반 SDLS UI 캡처 (그림자 margin 제거, 모달 안전 클릭, BM_CLICK + DWM bounds 등) + 매뉴얼 작성 글쓰기 규약
- 사용 시점: SDVision UI 매뉴얼 캡처, `DOC/Manual/*.html` / `DOC/Tech/*.html` 작성
- 설치 후 `Skill(sdls-manual)` 또는 자연 언어로 호출

## 마켓플레이스 구조

```
sdls-claude-plugins/
├── .claude-plugin/
│   └── marketplace.json          # 마켓플레이스 매니페스트
├── plugins/
│   └── FTools/
│       ├── .claude-plugin/
│       │   └── plugin.json       # 플러그인 매니페스트
│       └── skills/
│           └── sdls-manual/
│               ├── SKILL.md
│               └── _capture_lib.ps1
└── README.md
```

## 로컬 개발

원본 스킬은 SDLS repo (`d:\001. DEVELOPMENT\020. VS\SDLS\.claude\skills\sdls-manual\`) 에 있고, 이 마켓플레이스는 같은 내용을 패키징해 배포 채널 역할만 합니다. 로컬 개발 시 SDLS repo 안에서 수정한 후, 검증되면 이 repo 로 동기화 → commit → push.

수동 동기화 명령:
```bash
cp "d:/001. DEVELOPMENT/020. VS/SDLS/.claude/skills/sdls-manual/SKILL.md" \
   "d:/001. DEVELOPMENT/020. VS/sdls-claude-plugins/plugins/FTools/skills/sdls-manual/SKILL.md"
cp "d:/001. DEVELOPMENT/020. VS/SDLS/.claude/skills/sdls-manual/_capture_lib.ps1" \
   "d:/001. DEVELOPMENT/020. VS/sdls-claude-plugins/plugins/FTools/skills/sdls-manual/_capture_lib.ps1"
```

향후 자동 동기화는 SessionStop 훅 또는 cron 으로 추가 가능.

## 라이선스

내부 도구 — 별도 라이선스 명시 없음.
