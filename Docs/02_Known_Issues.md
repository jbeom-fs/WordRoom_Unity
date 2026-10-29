# WordRoom — Known Issues

기록·갱신일: **2026-10-29 (한국 시간), 하루 작업 종료**

오늘 갱신: **KI-012의 진단·새로고침은 `8928210`에 구현·커밋됐으나 누락 원인은 미확정이다. 원격 포즈의 보간 시각(KI-013)·보정 직후 전송 지연(KI-014)은 사용자 결정으로 수정 보류다. 부분 지도 마커 참조 누락 경고(KI-015)는 미수정·수정 범위 미합의다.**

현재 확인 기준: **HEAD `964effb`**. 기존 KI-001~011의 근거·확인 범위는 각 항목의 당시 기록을 유지한다. 로비 새로고침·협동 탐색·이동 개선·로봇은 사용자 확인 완료 통보, 지도는 사용자 씬 적용 통보와 정적 검토이며 전체 실행 결과는 별도 미확인이다. 진행 중 이탈·실패 주입 전체 통과를 추정하지 않는다.

KI-001 최초 기준: **`60d4d3d`**

미해결 사항과 수정 이력을 정리한다. **KI-001·009·011은 사용자가 기록 유지·수정 보류를 결정했다.** KI-004·008은 고정 UI 전환과 함께 **`0c5d217`에서 수정·커밋**됐다. 사용자 UI 구성 완료 통보는 있으나 개별 재현 조건의 확인 기록은 별도 없다. KI-002·003·005·006·007·010은 미수정·수정 범위 미합의다. 기존 단독 지도 기능은 사용자 최종 확인 완료 통보가 있으나, KI-010의 작은 풀 구성까지 확인됐다는 뜻은 아니다. 문서 기록을 수정 승인이나 보류 결정으로 해석하지 않는다. 코드 읽기와 실제 사용자 확인을 구분하며 번호는 문서 관리용이다.

| 번호 | 항목 | 우선순위·상태 |
|---|---|---|
| KI-001 | Prepare·Enter 일부 취소 확인 누락 | 낮음 / 사용자 결정으로 수정 보류 |
| KI-002 | 첫 미확정 화면 변경 후 재실행 복구 기준 없음 | P2 / 코드 검토 위험, 미수정·수정 범위 미합의 |
| KI-003 | 표시 거리 갱신 중 걷기와 입장 판정 정지의 불일치 | P3 / 조건부 위험, 미수정·수정 범위 미합의 |
| KI-004 | UI 크기 트랙 최초 누름에서 즉시 적용 | P3 / `0c5d217` 수정·커밋 / 개별 조건 확인 기록 없음 |
| KI-005 | 현재 해상도 추가로 창 크기 제한 우회 | P3 / 목록·검증 경로 문제, 미수정·수정 범위 미합의 |
| KI-006 | 모델 교체 도구 부분 저장과 실패 안내 불일치 | P2 / Editor 도구 오류 경로, 미수정 |
| KI-007 | 모델 교체 도구 재실행 시 재질·조명 조정 덮어쓰기 | P3 / 사용자 설정 보존, 미수정 |
| KI-008 | HUD 컴포넌트 비활성화 후 UI 재생성 가능 | P3 / `0c5d217` 수정·커밋 / 개별 조건 확인 기록 없음 |
| KI-009 | 목표 보정으로 앞으로만 이동해도 목표 단어가 나오는 현상 | 게임 밸런스 / 사용자 보고 / 기록만 유지·수정 보류 |
| KI-010 | 작은 지도 단어 풀에서 용량 계산이 끝나지 않음 | P2 / 코드 검토 위험 / 미수정·수정 범위 미합의 |
| KI-011 | 협동 중단 후 준비 초기화 확인 전 재시작 가능 | P2 / 코드 검토 위험 / 사용자 결정으로 후속 수정 보류 |
| KI-012 | 로비 방 목록 일부 누락 의심 | 원인 미확정 / 진단·새로고침 `8928210` 구현·사용자 확인·커밋 |
| KI-013 | 수신 포즈 묶음의 같은 시각 기록으로 보간 불연속 | P2 / 코드 검토 / 사용자 결정으로 수정 보류 |
| KI-014 | 위치 보정 직후에도 일반 전송 간격을 기다림 | P3 / 보고서와 코드 차이 / 사용자 결정으로 수정 보류 |
| KI-015 | 일부 원격 지도 마커 참조 누락을 경고하지 않음 | P3 / 조건부 구성 문제 / 미수정·수정 범위 미합의 |

최신 항목들은 에이전트가 실행·자동 재현하지 않았다. 우선순위는 검토 당시 영향 판단이며 모든 사용자 환경에서 발생한다고 단정하지 않는다.

## KI-011. 협동 중단 후 준비 초기화 확인 전에 재시작할 수 있음

**2026-10-28 추가 / P2 / 코드·설치된 PUN 소스 읽기 기준 / 실행 미재현 / 사용자 결정으로 후속 수정 보류.** 이번 요청에서는 기록만 남기고 코드·씬·프리팹을 수정하지 않는다.

`CoopSession.ReopenRoomIfMaster`는 방을 다시 열고 설정 순번(`ws`) 증가를 요청하지만, 반환값이나 서버의 확정 속성 수신을 기다리는 상태가 없다. PUN 기본 옵션에서는 이 사용자 속성의 로컬 사본도 서버 갱신 후 바뀐다. 한편 대기실 복귀 시 `CoopLobby.AdoptSession`은 `ResumeWaiting`으로 세션을 바로 Waiting으로 바꾸며, 새 로비의 `SettingsChangeGate`는 Settled에서 시작한다.

따라서 중단 후 설정 순번 증가의 서버 확인보다 대기실 복귀가 먼저 끝나면, 이전 순번과 이전 준비가 여전히 유효하게 계산될 수 있다. 이때 호스트가 시작을 누르면 `TryStartAsHost`도 통과할 수 있어, 전원 다시 준비한 뒤 재시작한다는 규칙을 보장하지 못한다. 씬 설정으로 해결되는 문제가 아니다.

후속 수정 방향은 준비 초기화 확인 상태를 씬 전환 이후에도 유지하고, 목표 설정 순번의 서버 확인 전에는 시작·준비·난이도 변경을 차단하는 것이다. 전송 실패·시간 초과도 미확정으로 처리해야 한다. 현재 `CoopProtocolTests`의 `Device.Reopen`은 공유 방의 순번을 즉시 증가시키므로 이 지연 구간을 다루지 않는다. 수정 시 대기실 복귀 후에도 확인이 늦는 동안 시작이 차단되고, 확인 후 이전 준비가 무효가 되는 사례를 추가한다. 실행 확인은 사용자가 한다.

관련 코드: [CoopSession.cs](../Assets/WordRoom/Scripts/Network/CoopSession.cs)의 `ReopenRoomIfMaster`·`ResumeWaiting`·`TryStartAsHost`, [CoopLobby.cs](../Assets/WordRoom/Scripts/Network/CoopLobby.cs)의 `AdoptSession`, [CoopProtocolTests.cs](../Assets/WordRoom/Tests/EditMode/CoopProtocolTests.cs)의 `Device.Reopen`.

## KI-012. 로비 방 목록 일부 누락 의심

**2026-10-28 최초 보고 / 2026-10-29 갱신 / 누락 원인·재현 조건·우선순위 미확정 / 진단·새로고침 `8928210` 구현·사용자 확인·커밋.**

사용자가 공동 시작까지 확인한 뒤, 일부 방 목록을 받아오지 못하는 때가 있는 것 같다고 보고하고 새로고침 필요성을 제안했다. 특정 방이 실제 수신에서 빠진 것인지, 캐시·화면 반영 문제인지, 서로 다른 지역·게임 버전·로비 또는 공개/진행 상태 차이인지는 확인되지 않았다. 일반 공개 로비가 모든 환경의 방을 표시해야 한다고 가정하지 않는다.

수신·캐시·UI 경로 정적 검토로 누락 원인을 확정하지 못했다. 지역·AppVersion·로비 또는 방 공개 상태 차이와 참가 응답 전 전체 목록 도착을 후보로 남겼다. 설정을 임의로 바꾸지 않았고, 기본 꺼짐 진단 로그(List Diagnostics / `-wordroom-list-log`)를 추가했다.

새로고침은 같은 로비 퇴장·재참가 후 GameList 전체 목록 확인까지 기다린다. 변경분 수신 Received와 ListComplete를 구분해 부분 목록으로 선택을 풀지 않으며 15초 제한·생성/입장 중복 차단·늦은 응답·화면 이탈을 처리한다. 빌린 연결의 진행 중 로비 요청은 CoopLobbyCleanup이 정리하고 재진입 시 인수한다. 사용자 확인 뒤 `8928210`에 커밋됐다. 새로고침 구현을 누락 원인의 근본 해결로 표시하지 않으며, KI-011과 진행 중 이탈 확인은 별도 후속이다.

관련 코드: [CoopLobby.cs](../Assets/WordRoom/Scripts/Network/CoopLobby.cs)의 `OnRoomListUpdate`·`OnJoinedLobby`·`OnLeftLobby`·`JoinLobby`, [CoopRules.cs](../Assets/WordRoom/Scripts/Network/CoopRules.cs)의 `RoomListCache`, [CoopLobbyScreen.cs](../Assets/WordRoom/Scripts/Game/CoopLobbyScreen.cs)의 `RefreshLobby`.

## KI-013. 수신 포즈 묶음에 같은 시각을 기록해 원격 로봇 보간이 끊길 수 있음

**2026-10-29 / P2 / `964effb` 코드 검토 / 실행 재현 없음 / 사용자 결정으로 수정 보류.**

CoopGameplayFlow.TakePoses는 한 프레임에 꺼낸 모든 포즈에 같은 now를 전달하고 RemotePlayers.Apply는 이를 보간 시각으로 저장한다. 같은 참가자의 포즈 여러 개가 지연 뒤 한 번에 처리되면 사이 시간 간격이 0이 되어, Lerp를 사용해도 이동 일부가 보간되지 않고 튈 수 있다. 로컬 걷기 잠금 문제와는 별개인 상대 외형 표시 문제다.

사용자는 로봇 검증 완료 뒤 Lerp 가능성을 논의했고, 현재도 Vector3.Lerp/Mathf.LerpAngle을 쓴다는 설명을 확인한 후 이 문제와 KI-014를 보류하고 지도 작업으로 진행했다. 지도는 최신 수신 포즈를 그대로 표시하므로 이 로봇 재생 시간축을 사용하지 않는다.

추후 포즈별 샘플 시간과 기기 간 시간축 차이를 고려한 재생 시각을 설계한다. 반복문 안에서 현재 시각을 다시 읽는 것만으로 원래 이동 간격을 복구했다고 보지 않는다. 코드는 이번에 바꾸지 않는다.

관련 코드: [CoopGameplayFlow.cs](../Assets/WordRoom/Scripts/Game/CoopGameplayFlow.cs)의 `TakePoses`, [RemotePlayers.cs](../Assets/WordRoom/Scripts/World/RemotePlayers.cs)의 `Apply`·`Sample`.

## KI-014. 위치 보정 직후에도 최대 0.1초 전송 간격을 기다림

**2026-10-29 / P3 / `964effb` 보고서·코드 대조 / 사용자 결정으로 수정 보류.**

SendPose는 placement 변경을 moved=true로만 취급한다. 초기 배치·거절 복귀도 이전 전송 뒤 PoseSendSeconds(0.1초)가 지나야 보낼 수 있어 제작 보고서의 "보정 횟수가 바뀌면 즉시 전송"과 다르다. 받는 쪽은 변경된 보정 횟수를 확인하면 이전 포즈와 보간하지 않고 배치하지만, 전송 전 지연까지 없애는 것은 아니다.

추후 보정 변경은 일반 이동 전송 주기와 구분하되 즉시 실패 시 매 프레임 재시도하지 않도록 다룬다. 강제 복귀를 일반 Lerp로 벽을 가로질러 이동시키지 않는다. 사용자 보류 결정을 유지하며 현재 수정하지 않는다.

관련 코드: [CoopGameplayFlow.cs](../Assets/WordRoom/Scripts/Game/CoopGameplayFlow.cs)의 `SendPose`, [CoopRules.cs](../Assets/WordRoom/Scripts/Network/CoopRules.cs)의 `PoseSendSeconds`.

## KI-015. 부분적으로 빠진 원격 지도 마커 참조를 경고하지 않음

**2026-10-29 / P3 / `964effb` 정적 검토 / 미수정·수정 범위 미합의 / 기본 연결 정상.**

MapView.RemoteSlots와 MapScreen.BindSlots는 remoteMarkers 배열 길이를 표시 가능한 인원으로 센다. 배열에 null 참조가 있어도 참가자를 해당 슬롯에 배정하고 DrawRemote는 조용히 건너뛴다. 예를 들어 길이는 3이지만 실제 참조가 하나뿐이면 일부 참가자가 표시되지 않아도 슬롯 부족 경고가 없다.

GameMenuSetup.HasRemoteMarkers도 참조 하나만 있으면 true여서 부분 누락을 경고하거나 보완하지 않는다. 추후 유효하고 중복되지 않은 참조 수와 누락 슬롯을 점검하는 보완이 가능하다. 사용자 참조를 임의로 덮어쓰는 정책으로 확대하지 않는다.

현재 MapCanvas에는 서로 다른 원격 마커 3개와 참조가 모두 있고 Gameplay가 해당 프리팹을 사용한다. 사용자가 직접 씬에 적용했다고 확인했으므로 "보고서와 달리 자산이 적용됨"은 결함이 아니라 작성/검토 시점 차이다. 기본 구성이 깨졌다는 뜻이나 지도 실행 검증 완료라는 뜻은 아니다.

관련 코드: [MapScreen.cs](../Assets/WordRoom/Scripts/Game/MapScreen.cs)의 `BindSlots`, [MapView.cs](../Assets/WordRoom/Scripts/Game/MapView.cs)의 `RemoteSlots`·`DrawRemote`, [GameMenuSetup.cs](../Assets/WordRoom/Editor/GameMenuSetup.cs)의 `HasRemoteMarkers`·`EnsureMapRemoteMarkers`.

## KI-001. 주변 준비·입장 API의 취소 확인 누락

| 항목 | 내용 |
|---|---|
| 상태 | 알려진 문제 / 수정 보류 / 기록만 유지 |
| 우선순위 | 낮음 — 중단 상황의 방어 코드 보완 |
| 확인 방법 | 코드 읽기 검토. 이 취소 경로의 실행 재현은 하지 않음 |
| 사용자 확인 | 문 열기 시 생성·실제 입장 분리 기능은 사용자 검증 완료 통보. 이 취소 경로까지 검증됐다는 뜻은 아님 |

### 내용과 원인

`RoomGenerator.Enter`는 전달받은 취소 토큰을 `cancel` 필드에 저장하지만, 취소 여부를 검사하지 않고 `Map.Enter`로 논리 위치·이동·방문·경로와 종료 상태를 갱신한다. 이미 취소된 토큰으로 엔진 API를 직접 호출해도 이 상태 변경을 막는 검사가 없다.

`RoomGenerator.Prepare`의 경우 실제 생성 루프에는 기존 취소 검사가 있지만, 주변이 이미 채워졌거나 같은 지도 상태라 생성을 건너뛰는 경로에서는 해당 검사에 도달하지 않고 `MarkPrepared`로 준비 완료를 기록할 수 있다.

호출 측의 `Task.Run(..., token)`은 작업 시작 전 취소를 처리한다. 작업이 시작된 뒤에는 엔진 내부의 협조적 취소 확인이 필요하다. `await` 이후의 토큰 검사는 늦은 결과의 화면·제어 반영을 막지만, 엔진이 이미 변경한 지도 상태를 되돌리지는 않는다.

### 발생 조건과 예상 영향

코드상 가능한 순서:

1. 방 입장 작업이 시작된다.
2. 방 생성 제어 컴포넌트 비활성화 등으로 요청이 취소된다.
3. 엔진이 취소를 확인하지 않고 입장을 기록한다.
4. 호출 측은 취소를 확인해 화면 갱신을 생략한다.

이 경우 중단된 세션의 논리 상태와 마지막 표시 상태가 달라질 수 있다. 작업이 시작되기 전에 취소되거나, 취소 전에 이미 입장이 확정된 경우와 구분해야 한다. 모든 취소에서 발생한다는 뜻은 아니다.

### 현재 일반 플레이에 미치는 영향

- **Escape·Alt+Tab·포커스 상실은 현재 생성 요청을 취소하는 동작이 아니다.** 이것만으로 발생하는 일반 탐색 버그로 분류하지 않는다.
- `RoomGenerationDebug.OnDisable`은 요청을 취소하고 제어 중인 플레이어를 해제한다. 다시 활성화해도 이전 플레이어의 걷기를 자동 재개하지 않으며 새 실험에서 다시 배치한다.
- 씬 종료로 기존 세션과 지도가 폐기되는 경우에는 중단된 지도 변경이 다음 세션으로 이어지지 않는다.
- 현재 정상 플레이를 막는 오류로 확인된 것은 아니다. 초기 검토의 P2 지적은 위 보호 동작과 실제 사용자 조작 범위를 고려해 **낮은 우선순위의 보완 과제**로 조정한다.

### 관련 코드

- [RoomGenerator.cs](../Assets/WordRoom/Scripts/Generation/RoomGenerator.cs): `Prepare`, `Enter`, `CheckCancel`, `Fill`, `FillAround`.
- [RoomMap.cs](../Assets/WordRoom/Scripts/Generation/RoomMap.cs): `Enter`, `MarkPrepared`.
- [RoomGenerationDebug.cs](../Assets/WordRoom/Scripts/RoomDebug/RoomGenerationDebug.cs): `OnDisable`, `PrepareRoom`, `EnterRoom`, 비동기 완료 후 토큰 검사.

### 추후 보완 방향

- 엔진 잠금 획득 직후와 입장·준비 기록 확정 전에 취소를 확인한다.
- 취소 전에 이미 확정한 작업을 임의로 롤백하지 않는다. 취소는 모든 상태 변경을 원자적으로 되돌리는 기능이 아니다.
- 플레이 중 요청 취소·중단 후 같은 세션 재개 기능을 추가할 때 이 문제를 함께 재검토한다.
- 수정 시 사용자가 취소된 토큰의 직접 API 호출, 실행 중 취소, 정상 입장·준비 흐름을 구분해 확인한다.

KI-001 최초 기록에서는 **문서만 추가**했고 코드는 수정하지 않았다. 10월 23일 작업 종료 갱신에서도 수정 보류 결정을 유지한다.

## KI-002. 첫 화면 변경이 미확정인 채 종료된 뒤 복원 기준이 없을 수 있음

**우선순위 P2 / 미수정 / 수정 범위 미합의 / 코드 검토 위험.**

### 조건·원인·영향

1. 사용자가 화면 설정에서 아직 한 번도 유지를 누르지 않았다.
2. 화면 적용 후 확인 대기 중 앱이 종료된다.
3. Unity 자체 화면 설정에 미확정 크기·모드가 남을 가능성이 있다.
4. 다음 실행에서 ScreenOptions.ApplySavedOnStartup은 GameSettings.TryGetScreen이 false면 즉시 반환한다. 자체 저장된 복구 기준이 없어 이전 화면으로 덮어쓰지 못한다.

패널 닫기·시간 초과·씬 전환의 실행 중 복원은 별도 구현돼 있다. 이 항목은 **첫 변경과 앱 종료·재실행이 겹친 경우**이며 모든 화면 변경이 저장된다는 뜻이 아니다. Unity의 종료 시 저장·복원 결과는 실행 확인하지 않았다. 최초 구현 보고서에도 같은 제약이 공개돼 있었다.

### 관련 코드·보완 방향

- [ScreenOptions.cs](../Assets/WordRoom/Scripts/Game/ScreenOptions.cs): ApplySavedOnStartup.
- [SettingsPanel.cs](../Assets/WordRoom/Scripts/Game/SettingsPanel.cs): ApplyScreen·KeepScreen·RevertScreen·OnDisable.
- [GameSettings.cs](../Assets/WordRoom/Scripts/Settings/GameSettings.cs): TryGetScreen·CommitScreen.
- 첫 미리보기 전에 복구할 기준을 영속 보관하고, 다음 실행에서 미확정 시도를 복구할 수 있도록 한다. 단순 PlayerPrefs 미확정 값 저장과 기준값 보관을 구분한다.

## KI-003. 표시 반경 갱신 중 입장 판정은 멈추지만 걷기는 잠그지 않음

**우선순위 P3 / 미수정 / 수정 범위 미합의 / 작업 지연과 경로 변경이 겹칠 때의 조건부 위험.**

RefreshView는 busy를 켜고 작업 스레드에서 스냅샷을 캡처하지만 SetMovementLocked를 호출하지 않는다. 사용자가 설정을 바꾼 뒤 메뉴를 닫으면 입력 차단은 해제될 수 있다. 이때 LateUpdate는 busy를 이유로 입장 판정을 생략하고 lastPosition도 갱신하지 않는다.

갱신이 지연되는 동안 열린 문을 통과하고 방향까지 바꾸면, 완료 뒤 사용되는 이전 위치→최종 위치 직선이 실제 이동 경로와 달라질 수 있다. 출입구 교차 지점이 폭 바깥으로 계산되면 입장을 놓칠 여지가 있다.

짧은 갱신이나 직선 이동은 누적 구간으로 정상 판정될 수 있다. **메뉴를 닫을 때마다 발생하는 일반 이동 오류로 단정하지 않는다.** 실행 재현·지연 측정은 하지 않았다.

- [RoomGenerationDebug.cs](../Assets/WordRoom/Scripts/RoomDebug/RoomGenerationDebug.cs): RefreshView·LateUpdate·EnterRoom.
- [FirstPersonPlayer.cs](../Assets/WordRoom/Scripts/World/FirstPersonPlayer.cs): SetInputBlocked·SetMovementLocked.
- 보완 후보: 표시 갱신 중 이동을 잠그고, 현재 요청·플레이어·종료 상태를 확인한 뒤 잠금을 해제한다. 메뉴 차단이나 다른 생성 잠금을 임의로 풀지 않도록 한다.

## KI-004. UI 크기 슬라이더의 최초 트랙 누름은 즉시 적용될 수 있음

**우선순위 P3 / `0c5d217`에서 수정·커밋 / 개별 재현 조건의 사용자 확인 기록 없음.** 아래 원인 설명은 **수정 전(`79a6ba2`) 상태**이며 기록으로 보존한다. 근거: 코드·설치된 UGUI 소스 읽기.

SettingsPanel은 Slider를 만든 다음 SliderRelease를 추가한다. 핸들 바깥 트랙을 누르면 Slider.OnPointerDown이 먼저 UpdateDrag로 값을 변경할 수 있다. onValueChanged에서 Held를 확인할 때 SliderRelease.OnPointerDown이 아직 실행되지 않았다면 false여서 UI 크기를 즉시 바꾼다.

보고서의 마우스 끌기 시 놓을 때 적용 의도와 다르게, 최초 누름에서 메뉴·트랙 위치가 움직일 수 있다. 핸들 자체를 잡아 끄는 경우와 구분한다. 실행 중 시각적 튐의 크기는 확인하지 않았다.

- [SettingsPanel.cs](../Assets/WordRoom/Scripts/Game/SettingsPanel.cs): Build의 uiScale 생성·변경 핸들러.
- [ArchiveUi.cs](../Assets/WordRoom/Scripts/Game/ArchiveUi.cs): AddSlider.
- [SliderRelease.cs](../Assets/WordRoom/Scripts/Game/SliderRelease.cs): OnPointerDown·OnPointerUp.
- 설치된 UGUI의 Slider.OnPointerDown과 ExecuteEvents 처리 순서를 읽었다. 패키지는 수정하지 않았다.
- 보완 후보: Slider의 값 변경 전에 누름 상태를 확정하도록 포인터 처리를 통합한다. 키보드 조절 즉시 적용은 유지한다.

### 10월 26일 변경 (`0c5d217`, 개별 조건 확인 기록 없음)

- **방식**: 처리 순서에 의존하지 않도록 판단 시점을 옮겼다. onValueChanged는 값 표시만 즉시 갱신하고 `uiScaleDirty`만 표시한다. 실제 적용은 SettingsPanel.LateUpdate(모든 Update·EventSystem 처리 후)에서 `Held`가 false일 때만 한다. 트랙 최초 누름은 같은 프레임 안에서 SliderRelease.OnPointerDown까지 끝나므로 LateUpdate 시점에는 Held가 true이고, 놓을 때 Released로 적용한다.
- **근거**: ExecuteEvents는 같은 오브젝트의 핸들러를 컴포넌트 순서대로 호출하므로, 컴포넌트 순서를 맞추는 방식은 사용자가 Inspector에서 순서를 바꾸면 다시 깨진다. Slider 하위 클래스로 바꾸는 방식은 SliderRelease 컴포넌트·프리팹 구조를 바꾸는 범위가 커서 택하지 않았다.
- **유지**: 키보드 조절은 같은 프레임 LateUpdate에서 적용(즉시 적용 의도 유지). 끌기 중 다른 설정 변경으로 RefreshAll이 슬라이더를 저장값으로 되돌리지 않는다. 패널이 닫히는 프레임의 미적용 키보드 값은 OnDisable에서 적용한다.
- **확인 대기**: 트랙 바깥 클릭·핸들 끌기·키보드 좌우 조절에서 적용 시점. 실행·재현하지 않았다.

## KI-005. 현재 해상도 재삽입으로 창 모드 목록 제한이 우회될 수 있음

**우선순위 P3 / 미수정 / 수정 범위 미합의 / 코드 검토.**

ScreenOptions.Sizes(false)는 디스플레이보다 큰 해상도를 먼저 제외하지만 이후 현재 Screen.width·height를 크기 제한 없이 다시 넣는다. 모니터 변경 등으로 현재 크기가 디스플레이보다 큰 경우 그 값이 다시 선택지에 들어간다. Validate도 같은 목록을 사용하므로 해당 크기를 그대로 허용할 수 있다.

현재 해상도를 UI에 표시하려는 목적과 적용 가능한 크기를 제한하는 목적이 섞인 경로다. 현재 크기가 디스플레이 안에 있으면 이 문제에 해당하지 않는다. 다중 모니터·고해상도 상황은 실행 확인하지 않았다.

- [ScreenOptions.cs](../Assets/WordRoom/Scripts/Game/ScreenOptions.cs): Sizes·Validate.
- 보완 후보: 현재 상태 표시와 적용 가능한 목록을 분리하거나, 현재 크기를 추가할 때도 창 모드 제한을 적용한다.

## KI-006. 모델 교체 도구가 부분 저장 후에도 저장하지 않았다고 안내함

**우선순위 P2 / 미수정 / 수정 범위 미합의 / Editor 메뉴 오류 경로.**

ArchiveModelSetup은 문 프리팹을 먼저 SaveAsPrefabAsset한 뒤 방 프리팹을 저장한다. 문 저장 성공 후 방 저장이 실패하면 문만 교체된 상태가 남는다. 내부 사유에는 문이 이미 저장됐다고 적지만 공통 catch는 프리팹은 저장하지 않았습니다라고 출력한다. 재질 자산도 프리팹 저장보다 먼저 생성·수정한다.

따라서 최초 보고의 검증 실패 시 두 프리팹 모두 저장하지 않음을 전체 저장 실패까지 보장하는 것으로 해석하면 안 된다. 성공 경로가 항상 잘못 저장된다는 뜻은 아니다. 실제 저장 실패를 발생시키지 않았다.

- [ArchiveModelSetup.cs](../Assets/WordRoom/Editor/ArchiveModelSetup.cs): EnsureMaterials 이후 두 번의 SaveAsPrefabAsset, catch.
- 보완 후보: 부분 저장 여부를 정확히 안내하고, 두 자산·재질의 복구 범위 또는 저장 전 준비 절차를 명확히 한다.

## KI-007. 모델 교체 메뉴 재실행이 사용자 재질·조명 값을 다시 설정함

**우선순위 P3 / 미수정 / 수정 범위 미합의 / 도구 재실행 조건.**

EnsureMaterials는 이미 있는 재질도 기본 색·금속성·매끄러움·발광을 다시 설정한다. PlaceLight도 기존 조명의 색·세기·범위·그림자 등을 다시 설정한다. 사용자가 교체 후 조정한 값을 메뉴 재실행으로 잃을 수 있다.

인스턴스·조명이 중복 생성되지 않는다는 멱등성과 사용자 속성 보존은 별개다. 사용자가 명시적으로 기본값 재적용을 원할 때의 동작과 구분해야 한다.

- [ArchiveModelSetup.cs](../Assets/WordRoom/Editor/ArchiveModelSetup.cs): EnsureMaterials·PlaceLight.
- 보완 후보: 최초 생성 시 기본값 적용과 기존 자산 보존을 구분한다. 그전에는 단순 확인 목적으로 교체 메뉴를 다시 실행하지 않는다.

## KI-008. HUD 컴포넌트 비활성화만으로 화면이 계속 숨겨지지 않을 수 있음

**우선순위 P3 / `0c5d217`에서 수정·커밋 / 개별 재현 조건의 사용자 확인 기록 없음.** 아래 원인 설명은 **수정 전(`79a6ba2`) 상태**이며 기록으로 보존한다. 조건: 컴포넌트 비활성화.

WordRoomHud.OnDisable은 Clear로 UI를 제거한다. 하지만 RoomGenerationDebug.RefreshHud는 참조가 null이 아니면 활성 여부와 관계없이 매 프레임 Show를 호출한다. Show에도 활성 여부 검사가 없어 UI를 다시 만들 수 있다. 비활성 MonoBehaviour의 일반 메서드는 외부에서 호출할 수 있다.

정상 활성 HUD의 표시 전체가 잘못됐다는 뜻은 아니다. 컴포넌트 체크를 끄는 디버그·수명 관리 상황의 문제이며, 게임 설정의 UI 크기와도 별개다.

- [WordRoomHud.cs](../Assets/WordRoom/Scripts/World/WordRoomHud.cs): Show·Clear·OnDisable.
- [RoomGenerationDebug.cs](../Assets/WordRoom/Scripts/RoomDebug/RoomGenerationDebug.cs): RefreshHud.
- 보완 후보: 호출 측이나 Show에서 활성 여부를 확인하고, 재활성화 후 표시 정책을 일관되게 정한다.

### 10월 26일 변경 (`0c5d217`, 개별 조건 확인 기록 없음)

고정 UI 사전 배치 전환과 같은 수명 관리 문제라 함께 처리했다.

- **구조**: HUD UI는 씬에 미리 배치한 `HudCanvas` 프리팹 인스턴스다. WordRoomHud는 RoomGenerationDebug 오브젝트에 남아 그 Canvas·텍스트를 참조만 한다. 표시 영역(Canvas 컴포넌트)과 제어 컴포넌트가 분리되어, 숨김이 제어 컴포넌트를 끄지 않는다. 런타임 생성·Destroy 경로는 없다.
- **Show**: 컴포넌트가 `isActiveAndEnabled`가 아니면 호출을 무시해 화면이 다시 켜지지 않는다. 바뀐 단어만 다시 쓴다.
- **Hide / Clear / OnDisable**: Hide는 Canvas만 끈다. Clear는 단어·캐시를 비우고 숨긴다(삭제 없음). OnDisable은 Clear를 호출한다.
- **재활성화**: OnDisable에서 캐시를 비우므로 다시 켠 뒤 같은 단어로 Show가 와도 표시 갱신이 생략되지 않는다. 호출 측 RefreshHud는 매 프레임 상태대로 Show/Hide를 호출하므로 다시 켜면 다음 프레임에 현재 단어가 다시 보인다.
- **호출 측**: 단어를 버릴 때(새 실험·플레이어 해제)는 Clear, Halt·디버그 모드는 Hide. 현재 단어는 기존대로 실제 입장 표시·바닥 확인 후에만 바뀐다. PlayHint도 같은 정책(비활성 시 Set 무시, Hide는 Canvas만, Clear·OnDisable은 문구 비우기)으로 맞췄다.
- **확인 대기**: 실행 중 WordRoomHud·PlayHint 체크 해제·재설정, RoomGenerationDebug 오브젝트 비활성화·재활성화. 실행·재현하지 않았다.

## KI-009. 목표 보정으로 앞으로만 이동해도 목표 단어가 나오는 현상

| 항목 | 내용 |
|---|---|
| 기록일·구현 기준 | 2026-10-26 / `0c5d217` |
| 상태 | 사용자 보고 / 게임 밸런스 검토 과제 / 기록만 유지·수정 보류 |
| 사용자 결정 | 추후 고려할 사항으로 남기고 다음 작업으로 진행 |
| 확인 범위 | 사용자 플레이 관찰 보고. 에이전트는 실행·재현하지 않음 |

사용자는 목표 단어 보정 때문에 단어의 연관성을 비교하며 방향을 선택하지 않고 **앞으로만 이동해도 목표 단어가 나오는 현상**을 보고했다. 단순 전진만으로 목표가 나타나면 방향 선택과 탐색의 의미가 약해질 수 있어, 이후 목표 유도와 난이도를 조정할 때 검토한다.

현재 목표 유도는 이동 방향 자체보다 진행도와 후보의 목표 근접도를 반영한다. 새로운 방을 방문하며 전진하는 경우에도 진행도가 증가하므로 목표 유도가 작동할 수 있다. 다만 **이번 관찰의 정확한 원인과 보정 강도의 영향은 아직 분석·확정하지 않았다.** 특정 방향을 우대하는 코드 오류나 모든 시드에서의 목표 출현 보장으로 단정하지 않는다.

시드, 난이도, 시작·목표 단어, 이동 수, 재현 빈도는 이번 보고에 포함되지 않았다. 목표 단어가 나타나는 것과 실제 목표 방 입장으로 성공하는 판정은 구분한다.

추후에는 목표 유도 켬·끔과 강도·시작 시점에 따른 단순 전진 및 단어를 비교하는 탐색의 차이를 검토할 수 있다. 비교 기준과 조정값은 미정이며, **이번에는 생성 코드·보정 설정·데이터를 변경하지 않는다.**

관련 코드: `Scripts/Generation/RoomGenerator.cs`의 `Guidance`·후보 순위 보정, `GenerationConfig.cs`, `TargetCloseness.cs` (`Assets/WordRoom/` 기준).

## KI-010. 작은 지도 단어 풀에서 용량 계산이 끝나지 않을 수 있음

| 항목 | 내용 |
|---|---|
| 기록일·구현 기준 | 2026-10-26 / `430dfbb` |
| 상태 | 코드 검토 위험 / 미수정 / 수정 범위 미합의 |
| 우선순위 | P2 — 잘못된 프리팹 구성에서 메인 스레드 정지 가능 |
| 확인 범위 | 이전 지도 검토 지적과 현재 코드 읽기. 실행·자동 재현하지 않음 |

`MapView.MissingParts`는 단어 풀에 요소가 하나 이상이고 모든 참조가 있으면 통과시킨다. 이후 `UpdateLimits`는 `CellFor`에 보이는 칸 수가 단어 풀 이하가 되는 칸 크기를 찾도록 요청한다. `CellFor`의 `while (!fits(hi)) hi *= 2f`에는 반복 상한이나 해가 없는 경우의 처리가 없다.

`MapGraphic.MaxCellsInRange(size, cellSize, 0)`은 축마다 최소 3을 더하므로 정상적인 비음수 영역 크기에서는 칸 수가 적어도 9다. 따라서 **풀을 1~8개로 줄인 구성**은 조건을 만족할 수 없으며, 지도를 열어 용량을 계산할 때 메인 스레드가 무한 반복할 수 있다. 이는 확인된 위험 조건이며 가능한 모든 부족한 풀 크기를 나열한 것은 아니다.

현재 저장된 기본 프리팹의 풀은 364개다. 이 작은 풀 조건이 기본 구성에서 발생했다는 사용자 보고는 없으며, 최종 지도 기능 확인 완료 통보와도 구분한다.

관련 코드: [MapView.cs](../Assets/WordRoom/Scripts/Game/MapView.cs)의 `MissingParts`·`UpdateLimits`·`CellFor`, [MapGraphic.cs](../Assets/WordRoom/Scripts/Game/MapGraphic.cs)의 `MaxCellsInRange`.

추후 보완 후보는 용량 계산에 반복·유한값 상한과 실패 결과를 두고, 해가 없는 경우 경고 후 단어 표시를 생략하는 정책이다. 단어 풀의 최소 구성 검사를 함께 고려할 수 있다. **이번에는 문제를 기록하며 코드는 수정하지 않는다.**
