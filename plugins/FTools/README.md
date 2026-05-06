# FTools

SDLS 팀 개발용 Claude Code 플러그인.

## 포함

### 스킬

- **`sdls-manual`** — SDLS WinForms UI 캡처 자동화 + 매뉴얼 작성 규약
  - PowerShell 라이브러리 (`_capture_lib.ps1`): 창 검색, 그림자 없는 캡처, 모달 안전 BM_CLICK, UIA 통합
  - 글쓰기 규칙 (사용자 반복 교정 사항): 평범한 말 / 예시 라벨 / em-dash 회피 등
  - 검증 원칙: `.Designer.cs::.Text` 우선, Click 핸들러 logic 확인

## 사용

매뉴얼 작성 / UI 캡처 작업 시작 시:
```
Skill(sdls-manual)
```

또는 자연 언어로 "SDVision UI 캡처 / 매뉴얼 작성" 의도를 전달하면 자동 활성화.

## 요구사항

- Windows + PowerShell 5.1+
- WinForms 기반 SDVision (또는 유사 앱)
- PowerShell `System.Drawing` / `UIAutomationClient` / `UIAutomationTypes` (.NET 기본 포함)

## 변경 이력

- **0.1.0** — 초기 릴리스 (sdls-manual 스킬 단독)
