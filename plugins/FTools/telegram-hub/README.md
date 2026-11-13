# SDLS 텔레그램 허브 (FTeleHub)

노트북에 상주하는 트레이 프로그램. 텔레그램 봇 하나로 사용자 지시를 받아 깃 릴레이(`user_to_<노드>.md`)로 넘기고, 각 PC 세션의 보고(`<노드>_to_user.md`)를 텔레그램 그룹 토픽으로 올린다.

- 설계 : SDLS 저장소 `00B. DOC/003. Claude/Automation/relay/ResidentAgent_Telegram_Design.html`
- PC 쪽 도구 · 감시기 · 훅 : SDLS 저장소 `relay/tg/`, `relay/Watch-Relay.ps1`, `.claude/hooks/`
- 세션 규칙 : 스킬 `sdls-telegram`

## 빌드

```
powershell -ExecutionPolicy Bypass -File build.ps1          # Release 빌드 + 자체 시험
```
결과 : `src\FTeleHub\bin\Release\FTeleHub.exe` (.NET Framework 4.8, 외부 패키지 없음)

## 처음 설정 (허브 PC 한 번)

1. 텔레그램에서 BotFather 로 봇을 만들고 `/setprivacy` → Disable(그룹 일반 글을 받기 위해).
2. 사용자 전용 그룹을 만들고 포럼 토픽을 켠다. 봇을 그룹에 넣고 관리자로 지정하되 권한은 **주제 관리 하나만** 켠다(익명으로 남기 끔).
3. `FTeleHub.exe` 실행 → 트레이 메뉴 "토큰 설정…" 에 봇 토큰을 붙여 넣는다(DPAPI 로 이 사용자 계정에만 저장).
4. 그룹에서 `/whoami` → 답으로 온 사용자 ID · 대화 ID 를 트레이 "설정 파일 열기" 의 `hub.json` 에 넣는다(`AllowedUserIds`, `ChatId`). 허브를 다시 시작.
5. 그룹에서 `/addnode D1 D252-TM O1` 처럼 보내면 허브가 노드마다 토픽을 만들고 연결한다(한 번에 8 개까지, 이미 있는 노드는 건너뜀). 손으로 만든 토픽은 그 토픽에서 `/bind <노드>`. 그 PC 의 `%LOCALAPPDATA%\SDOPTICS\ClaudeWork\tg_node.txt` 에 같은 이름을 적는다.
   - "생성 여부 불명" 이 오면 다시 `/addnode` 하지 말고 그룹에 그 토픽이 생겼는지 먼저 본다. 있으면 그 토픽에서 `/bind <노드>`.
6. "Windows 시작 시 자동 실행" 은 처음 실행 때 켜진다(실패하면 로그에 남기고 다음 시작 때 다시). 트레이 메뉴에서 끄고 켜며, 사용자가 끈 뒤로는 다시 켜지 않는다. 로그온해야 돈다.

## 명령 (허용된 사용자 · 그룹에서만)

`/status` · `/pause` · `/resume` · `/deliver tg-N` · `/drop tg-N` · `/cancel`(원래 지시에 답글로) · `/bind <노드>` · `/addnode <노드…>` · `/whoami`

## 파일

`%LOCALAPPDATA%\SDOPTICS\FTeleHub\` : `hub.json`(설정 · 토큰 암호문) · `ledger.jsonl`(지시 장부) · `sent_ids.txt`(보낸 보고 ID) · `repo\`(릴레이 전용 클론) · `log\`
