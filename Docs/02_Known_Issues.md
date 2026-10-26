# WordRoom — Known Issues

기록·갱신일: **2026-10-23 (한국 시간)**  
현재 확인 기준: **`79a6ba2` — feat(game): 메인 메뉴·난이도·사용자 설정 추가**  
KI-001 최초 기준: **`60d4d3d`**

미해결 사항을 정리한다. **KI-001만 사용자가 기록 유지·수정 보류를 결정했다.** KI-002~005는 최신 메뉴 검토, KI-006~008은 이전 모델 도구·HUD 검토 사항이다. 모두 미수정·수정 범위 미합의 상태다. 문서 기록을 수정 승인이나 보류 결정으로 해석하지 않는다. 코드 읽기로 확인한 경로와 실제 사용자 검증 결과를 구분한다. 번호는 문서 관리용이며 발견 순서를 뜻하지 않는다.

| 번호 | 항목 | 우선순위·상태 |
|---|---|---|
| KI-001 | Prepare·Enter 일부 취소 확인 누락 | 낮음 / 사용자 결정으로 수정 보류 |
| KI-002 | 첫 미확정 화면 변경 후 재실행 복구 기준 없음 | P2 / 코드 검토 위험, 미수정·수정 범위 미합의 |
| KI-003 | 표시 거리 갱신 중 걷기와 입장 판정 정지의 불일치 | P3 / 조건부 위험, 미수정·수정 범위 미합의 |
| KI-004 | UI 크기 트랙 최초 누름에서 즉시 적용 | P3 / 입력 처리 순서 문제, 미수정·수정 범위 미합의 |
| KI-005 | 현재 해상도 추가로 창 크기 제한 우회 | P3 / 목록·검증 경로 문제, 미수정·수정 범위 미합의 |
| KI-006 | 모델 교체 도구 부분 저장과 실패 안내 불일치 | P2 / Editor 도구 오류 경로, 미수정 |
| KI-007 | 모델 교체 도구 재실행 시 재질·조명 조정 덮어쓰기 | P3 / 사용자 설정 보존, 미수정 |
| KI-008 | HUD 컴포넌트 비활성화 후 UI 재생성 가능 | P3 / 비활성 처리, 미수정 |

최신 항목들은 에이전트가 실행·자동 재현하지 않았다. 우선순위는 검토 당시 영향 판단이며 모든 사용자 환경에서 발생한다고 단정하지 않는다.

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

**우선순위 P3 / 미수정 / 수정 범위 미합의 / 코드·설치된 UGUI 소스 읽기.**

SettingsPanel은 Slider를 만든 다음 SliderRelease를 추가한다. 핸들 바깥 트랙을 누르면 Slider.OnPointerDown이 먼저 UpdateDrag로 값을 변경할 수 있다. onValueChanged에서 Held를 확인할 때 SliderRelease.OnPointerDown이 아직 실행되지 않았다면 false여서 UI 크기를 즉시 바꾼다.

보고서의 마우스 끌기 시 놓을 때 적용 의도와 다르게, 최초 누름에서 메뉴·트랙 위치가 움직일 수 있다. 핸들 자체를 잡아 끄는 경우와 구분한다. 실행 중 시각적 튐의 크기는 확인하지 않았다.

- [SettingsPanel.cs](../Assets/WordRoom/Scripts/Game/SettingsPanel.cs): Build의 uiScale 생성·변경 핸들러.
- [ArchiveUi.cs](../Assets/WordRoom/Scripts/Game/ArchiveUi.cs): AddSlider.
- [SliderRelease.cs](../Assets/WordRoom/Scripts/Game/SliderRelease.cs): OnPointerDown·OnPointerUp.
- 설치된 UGUI의 Slider.OnPointerDown과 ExecuteEvents 처리 순서를 읽었다. 패키지는 수정하지 않았다.
- 보완 후보: Slider의 값 변경 전에 누름 상태를 확정하도록 포인터 처리를 통합한다. 키보드 조절 즉시 적용은 유지한다.

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

**우선순위 P3 / 미수정 / 수정 범위 미합의 / 컴포넌트 비활성화 조건.**

WordRoomHud.OnDisable은 Clear로 UI를 제거한다. 하지만 RoomGenerationDebug.RefreshHud는 참조가 null이 아니면 활성 여부와 관계없이 매 프레임 Show를 호출한다. Show에도 활성 여부 검사가 없어 UI를 다시 만들 수 있다. 비활성 MonoBehaviour의 일반 메서드는 외부에서 호출할 수 있다.

정상 활성 HUD의 표시 전체가 잘못됐다는 뜻은 아니다. 컴포넌트 체크를 끄는 디버그·수명 관리 상황의 문제이며, 게임 설정의 UI 크기와도 별개다.

- [WordRoomHud.cs](../Assets/WordRoom/Scripts/World/WordRoomHud.cs): Show·Clear·OnDisable.
- [RoomGenerationDebug.cs](../Assets/WordRoom/Scripts/RoomDebug/RoomGenerationDebug.cs): RefreshHud.
- 보완 후보: 호출 측이나 Show에서 활성 여부를 확인하고, 재활성화 후 표시 정책을 일관되게 정한다.
