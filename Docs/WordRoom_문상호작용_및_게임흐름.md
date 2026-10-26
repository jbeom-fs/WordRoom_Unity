# WordRoom — 문 상호작용·주변 준비·아카이브 모델·HUD·게임 흐름

기록일: **2026-10-23 (한국 시간)**, 9절 고정 UI 사전 배치 전환 추가 **2026-10-26** (작업 트리·미커밋)  
현재 구현 기준: **`79a6ba2`**

10월 21일의 방 안 이동 단계 이후 변경을 정리한다. 이 문서는 구현 보고·사용자 결정·실제 코드·프리팹·씬·Git 읽기를 근거로 작성했다. Unity 실행·컴파일·빌드·테스트·자동 재현·Python 실행은 하지 않았다. 커밋 여부와 사용자 검증 여부는 별개다.

## 1. 구현 이력과 확인 범위

| 커밋 | 변경 | 상태 |
|---|---|---|
| `285701f` | E 문 열기, 상승, 실제 입장, 표시 실패·전환 보완 | 구현·커밋 |
| `60d4d3d` | 문 열기 Prepare와 실제 입장 Enter 분리 | 구현·커밋, 사용자 검증 완료 통보 |
| `c4109b4` | 취소 확인 누락 KI-001 | 문서만 기록, 사용자 결정으로 수정 보류 |
| `51186c3` | 아카이브 방·문 모델, 교체 도구, 적용된 프리팹·재질 | 구현·커밋, 항목별 외관·충돌 확인 기록은 별도 없음 |
| `373de03` | 현재·목표 HUD | 구현·커밋, 항목별 사용자 검증 기록은 별도 없음 |
| `79a6ba2` | 메인·게임 씬, 난이도, 설정, 표시 거리 | 씬·빌드 등록 포함 구현·커밋, 검토 문제 4건 미수정 |

`60d4d3d`의 검증 완료 통보를 취소·장시간 탐색·후속 모델·HUD·메뉴의 모든 항목 통과로 확대하지 않는다. 이후 기능도 커밋만으로 검증 완료를 추정하지 않는다.

## 2. 문 열기와 실제 입장

### 입력과 문 상태

FirstPersonPlayer는 화면 중앙에서 2m Ray를 쏜다. 자기 Collider·Trigger·Ignore Raycast를 제외하고 처음 맞은 물체가 유효한 문이어야 한다. 활성 문이며 현재 방과 표시된 이웃 방을 잇는 닫힌 연결일 때만 [E] 문 열기를 표시한다. E를 누르고 있거나 요청 중 연타해도 요청을 쌓지 않는다.

RoomWorldView.OpenDoor가 상승 시작을 수락한 뒤 연결 상태를 Open으로 기록하고 방향을 반환한다. DoorView는 본체만 0.5초 동안 2.2m 올린다. 문 열기 자체는 이동·방문·성공을 바꾸지 않는다.

문 본체 Collider와 PassageBlock은 별개다. 본체 Collider는 완전 상승 후 끄고, Ignore Raycast인 PassageBlock은 다음 조건을 모두 만족할 때 해제한다.

- 문이 활성이고 완전히 열림.
- 현재 방과 실제 사용 가능한 표시 방 사이의 연결.
- 최신 스냅샷에서 목적지 주변 준비가 처리됨.

표시 범위 밖으로 연결된 열린 문은 저장 상태를 Open으로 유지하면서 통과만 막는다. 풀의 Bind·Unbind·Apply는 이전 애니메이션과 차단 상태가 새 연결에 남지 않게 한다. 열린 연결이 다시 표시되면 상승을 재생하지 않고 열린 위치로 나온다.

### Prepare: 문을 열 때 단어 생성

E 수락과 같은 프레임 RoomGenerationDebug.PrepareRoom이 이동 잠금·busy 설정·안내 지우기를 먼저 수행한다. 작업 스레드에서 RoomGenerator.Prepare를 실행하고 같은 스레드에서 스냅샷을 캡처한다.

Prepare는 기존 이웃 방의 빈 주변에 Fill을 적용한다. 현재 위치·이동·방문·경로·성공 상태는 유지한다. 새 단어 확정에 따른 Recent 갱신은 기존 규칙을 유지한다. 메인 스레드에서 현재 방을 중심으로 Show하고, 표시가 준비되면 이동을 재개한다. 문 상승과 준비가 모두 끝나야 실제 통과가 가능하다.

RoomMap의 IsPrepared·MarkPrepared는 요청 처리 기록이다. 네 방향 완성과 다르며 일부 미생성·같은 상태 생략도 처리됨으로 본다. 미생성 방향은 여전히 열거나 입장할 수 없다. 새 세션에서 준비 기록을 초기화한다.

이미 준비된 방으로 다른 닫힌 문을 새로 열면 Prepare 요청은 가능하며 실제 재생성 여부는 기존 실패 재시도 정책이 판단한다. 이미 열린 같은 문에 E를 눌러 반복 생성하는 구조는 아니다.

### Enter: 걸어서 들어갈 때 방문 처리

LateUpdate는 직전 위치→현재 위치 구간이 현재 방 경계 바깥 0.15m를 출입구 폭 안에서 넘었는지 확인한다. 현재 방 중심에서 경계까지 3.1m이므로 각 방향 입장 기준은 3.25m다. 반대로 돌아오려면 반대쪽 0.15m까지 넘어야 한다.

감지 직후 이동을 잠그고 busy를 설정한다. 작업 스레드의 Enter는 준비된 기존 이웃에 대해 Map.Enter와 목표·테스트 이동 제한 판정만 실행하고 Fill은 하지 않는다. 이후 목적지 중심 Show, 도착 바닥 소속 확인, HUD 현재 단어 갱신, 이동 재개 순이다. 위치·시점은 유지하고 PlaceAt을 반복하지 않는다.

목표 방의 문을 열기만 해서는 성공하지 않는다. 실제 목표 방에 입장해야 성공한다. 방향키 디버그 Move는 기존 입장+Fill을 그대로 유지한다.

### 비동기·실패·자막

요청 결과는 취소 토큰·세션 생성기·제어 플레이어 참조를 확인해 적용한다. 요청 중 다른 생성 요청은 받지 않는다. 정상 취소는 오류로 표시하지 않는다. 취소 토큰 확인이 엔진 일부 경로에서 빠진 문제는 KI-001이며, 화면의 늦은 결과 방어와 구분한다.

Show는 Dictionary 키만 보지 않고 방·문·PassageBlock의 실제 바인딩·좌표·활성·준비 상태를 검사해 bool과 실패 사유를 반환한다. 시작 실패는 FailStart로 플레이어·커서를 해제한다. 진행 중 준비 거절·표시 실패·예외는 Halt로 정지한다. 이미 확정한 지도 상태를 되돌리거나 자동으로 재시도하지 않는다.

문 여는 중·주변 방 준비 중 자막은 삭제했다. E 안내는 열기 수락 즉시 지운다. 목표·오류·재배치 필요 안내는 PlayHint에 유지한다.

## 3. 플레이어 전환과 시점 보존

ActivePlayer 기준은 참조가 있고 isActiveAndEnabled인 상태다. 해제·교체·비활성·파괴를 TrackPlayer가 확인하면 이전 플레이어를 정지하고 커서를 해제한다. 새 플레이어는 새 실험에서만 배치한다. OnDisable은 배치 상태를 해제하고, 비활성 PlaceAt은 거부한다.

카메라 원래 로컬 위치는 직렬화하거나 RememberViewHome에서 최초 한 번 저장한다. 씬 기준은 (0, 1.6, 0)이다. 디버그 카메라가 이동한 위치를 새 기준으로 덮어쓰지 않는다. PlaceAt에서 원래 높이·수평 시점으로 복원한다.

메뉴 입력 차단 SetInputBlocked와 생성 중 걷기 잠금 SetMovementLocked는 별도다. 메뉴는 걷기·시점·E·클릭 커서 잠금을 막되 중력은 유지한다. 메뉴를 닫으면 다음 프레임부터 조작을 재개하고 배치된 플레이어만 커서를 다시 잠근다. 생성이 끝나지 않았다면 걷기 잠금은 남는다.

## 4. 아카이브 모델과 공간

| 항목 | 기준 |
|---|---|
| 방·문 모델 | Assets/Models/ArchiveRoom_v1.fbx·ArchiveDoor_v1.fbx |
| 기존 경로 유지 | Assets/Prefabs/TestRoomPrefab.prefab·TestDoor.prefab |
| 방 간격 / 실내 | 6.2m / 6×6m |
| 벽 두께 / 바닥 범위 | 0.1m / X·Z ±3.1, Y -0.2~0 |
| 천장 아랫면 / 수납부 최고점 | 3.0m / 4.6m |
| 출입구 / 문 상승 | 폭 1.3m·높이 2.2m / 2.2m |
| 문 모델 본체 Bounds | X ±0.05, Y 0~2.2, Z -1.3~0 |
| 명판 / 글자 영역 | 0.9×0.3m / 0.82×0.22m |

ArchiveModelSetup은 사용자 실행 메뉴 WordRoom/새 아카이브 모델로 프리팹 교체를 제공한다. 임포트된 모델 위치에서 축을 판별·보정하고, 바닥·천장·개구 양옆과 상단 Collider를 구성한다. 문 Collider는 움직이는 본체에 붙는다. 기존 TMP·Pretendard를 재사용하고 6종 URP Lit 재질과 실내 조명을 구성한다.

`51186c3`에는 실제 교체된 프리팹·모델·재질이 포함돼 있다. 모델 교체 도구만 있고 아직 프리팹이 구형이라는 최초 보고는 현재 Git 상태를 설명하지 않는다. 조명·재질·문 수납·8글자 가독성·충돌은 설계와 파일만으로 실제 품질을 단정하지 않는다.

## 5. HUD

WordRoomHud는 1920×1080 기준 좌상단 (32, -32), 320×96 패널에 현재·목표 두 줄을 둔다. 배경 #49352A, 테두리 #A8874B, 아이보리 계열 글자를 사용한다. 라벨 18px, 단어 24px이며 폭이 부족하면 최소 20px까지 줄인다. Canvas 정렬 50, PlayHint 100, 게임 메뉴 200이다.

HUD는 RoomMap을 직접 읽지 않는다. 스냅샷의 CurrentWord·TargetWord를 사용하고, 실제 입장 표시와 바닥 확인을 통과한 뒤 현재 단어를 변경한다. 목표는 세션 시작 값으로 고정한다. 문 열기·생성 중에도 기존 단어를 유지한다. 목표 도달 시 HUD는 유지하고, Halt·새 실험·플레이어 해제·디버그 모드에서는 숨긴다.

HUD·PlayHint·메뉴는 UiScaleBinding을 공유한다. CanvasScaler의 기준 해상도를 사용자 UI 배율로 나누며 월드 표지판 크기는 바꾸지 않는다. HUD와 PlayHint는 메뉴 입력을 받지 않는다.

## 6. 메인·게임 씬과 자동 시작

- MainMenu: 정적 아카이브 배경·메뉴 카메라·AudioListener·MainMenuController·UiSound. 생성 엔진·데이터 로드 없음.
- Gameplay: 개발용 씬 복사 기반. Inspector 자동 시작을 끄고 GameplayBootstrap·PauseMenu·UiSound와 카메라 AudioListener를 연결.
- 빌드 목록: MainMenu → Gameplay → 기존 SampleScene. 씬 전환은 이름으로 처리.
- 개발용 RoomGenerationDebug 씬은 유지하며 Inspector 입력·자동 시작으로 확인할 수 있음.

설치 메뉴 WordRoom/메인 메뉴와 게임 설정 구성은 두 씬을 처음 만들고 이후 누락 구성을 채운다. 현재 `79a6ba2`에 두 씬과 빌드 등록이 이미 포함돼 있다. 설치 대기였던 보고 시점과 구분한다.

GameLaunch는 선택한 난이도만 다음 씬에 한 번 전달한다. GameplayBootstrap.Awake가 Inspector 자동 시작을 끄고 요청을 소비한다. 데이터 준비 후 CreateScope(limit, 1, 8).Ranks에서 서로 다른 두 단어와 새 시드를 한 번 결정한다. 후보 부족·실패를 다른 단어로 숨기지 않는다. 메뉴 없는 직접 Gameplay 시작은 기본 보통이다.

일반 사용자는 단어를 입력하지 않는다. 쉬움·보통·어려움은 각각 빈도 상위 5,000·10,000·15,000개 단어다. 테스트 이동 제한은 끄고 기존 생성 품질·중복·목표 유도 규칙은 유지한다.

WordRoomDataService는 성공적으로 로드한 데이터셋을 패키지 경로·해시 옵션 기준으로 실행 중 재사용한다. 매 Play 시작 시 캐시를 초기화한다. 캐시는 세션 지도 저장이 아니다.

## 7. 설정 저장·화면 확인·음량

| 항목 | 기본 / 범위 |
|---|---|
| 표시 반경 | 1 / 1~3칸, 최대 5·13·25개 기존 방 |
| 감도 배율 / Y축 반전 | 1 / 0.2~3, 꺼짐 |
| 시야각 | 60 / 수직 60~90도 |
| UI 크기 | 100% / 80~125% |
| 전체·효과음 | 각각 100% / 0~100% |
| 화면 | 전체 화면·디스플레이 크기 / 플랫폼 해상도 |

GameSettings는 WordRoom.Settings. 접두사만 사용한다. 잘못된 수치를 보정하고 변경 이벤트를 발행한다. 기본값 복원으로 다른 PlayerPrefs·지도·난이도를 지우지 않는다. 화면 외 설정은 즉시 저장값을 갱신하고, UI 크기 끌기는 놓을 때 적용하려는 구조다. 최초 클릭 예외는 KI-004를 따른다.

화면 적용은 15초 확인을 시작한다. 유지하면 실제 적용된 크기·모드를 저장하며, 되돌리기·시간 초과·닫기·씬 전환은 이전 화면으로 복원한다. 빌드 시작 시 저장값을 적용하고 Editor Game 뷰는 시작 시 자동 변경하지 않는다. 첫 미확정 변경 후 앱 종료 복원(KI-002), 현재 해상도 재삽입에 따른 목록 제한 우회(KI-005)가 남아 있다.

표시 반경 변경은 별도 busy 요청으로 기존 지도 스냅샷만 재캡처한다. 생성 중 변경한 반경은 종료 후 최신 값을 반영한다. 입장 판정이 생략되는 동안 이동을 막지 않는 위험은 KI-003을 따른다.

UiSound는 외부 음원 없이 짧은 클릭음을 만든다. 효과음은 기본 음량×효과음 배율, 전체 음량은 AudioListener.volume을 한 번씩 적용한다. 모든 메뉴 버튼과 효과음 슬라이더 놓기에서 재생한다.

## 8. 미해결 사항과 다음 경계

[Known Issues](02_Known_Issues.md)의 KI-001은 사용자 결정으로 기록만 유지한다. KI-002~005는 최신 메뉴 검토에서 보고했고 수정 범위는 아직 합의하지 않았다. 이전 모델 도구의 부분 저장·재실행 덮어쓰기와 HUD 비활성화 처리도 KI-006~008에 기록했다. 모두 실행 재현 결과와 구분한다.

후속 과제는 메뉴·설정 검토 사항의 처리 범위, 결과 화면·시간/방문 집계·복귀 흐름, 시야·장시간 성능, 공유 월드와 플레이어별 상태 분리, Photon 협동, 재접속·인계, 공유 지도·그림이다. 결과 집계·시간 측정 기준과 협동 세부 정책은 아직 확정하지 않았다.

## 9. 고정 화면 UI 사전 배치 전환 (2026-10-26, 작업 트리·미커밋)

**상태: 코드·Editor 도구 작성 완료 / 프리팹·씬 적용 대기 / 실행 확인 전.** 에이전트는 Unity·Editor 메뉴를 실행하지 않았다. 아래 구조는 사용자가 `WordRoom/메인 메뉴와 게임 설정 구성`을 실행해야 씬·프리팹에 저장된다. 그 전까지 세 씬에는 UI 계층이 없고, 런타임은 UI를 만들지 않으므로 각 컴포넌트가 참조 누락 오류를 한 번 출력하고 해당 기능만 멈춘다(메인 메뉴 미동작, Esc는 플레이어의 커서 해제로 남음, HUD·안내 미표시).

### 9.1 제거한 런타임 생성 경로

| 이전 | 이후 |
|---|---|
| MainMenuController.Awake: EnsureEventSystem·CreateCanvas·BuildMain·BuildDifficulty·SettingsPanel.Create | SerializeField 참조로 패널 전환·클릭 처리만 |
| PauseMenu.Awake: EnsureEventSystem·CreateCanvas·Overlay·Menu·SettingsPanel.Create | menuRoot·list·버튼·settings 참조 |
| SettingsPanel.Create/Build: 패널·스크롤·행·슬라이더·SliderRelease AddComponent | Stepper·SliderField 구조체 등 직렬화 참조, Initialize 1회 |
| ArchiveUi: Canvas·Panel·Button·Slider·Scroll 조립, Font 정적 상태 | 색상·Select·QuitGame·Need·Bind/Unbind·SetNormalColor만. 조립은 Editor `ArchiveUiBuilder` |
| WordRoomHud.Build / Clear·OnDisable의 Destroy | canvas·currentText·targetText 참조, Canvas.enabled 전환 |
| PlayHint.Build / OnDestroy의 Destroy | canvas·label 참조, Canvas.enabled 전환 |
| UiScaleBinding.Configure: CanvasScaler·바인딩 AddComponent | 저장된 CanvasScaler 사용, 없으면 오류 1회 |

### 9.2 프리팹과 씬 배치(도구 실행 후)

| 자산 | 내용 |
|---|---|
| `Assets/Prefabs/UI/SettingsPanel.prefab` | 설정 패널. 내부 참조 연결, ScrollFollowSelection·SliderRelease(UI 크기·효과음) 포함 |
| `MainMenuCanvas.prefab` | Canvas 100 + Raycaster, Main·Difficulty(비활성)·SettingsPanel 중첩 인스턴스(비활성) |
| `GameMenuCanvas.prefab` | Canvas 200 + Raycaster, Overlay(클릭 차단)·Menu·SettingsPanel 중첩 인스턴스(비활성) |
| `HudCanvas.prefab` | Canvas 50, Raycaster 없음, raycastTarget 모두 꺼짐. Canvas 컴포넌트는 꺼진 채 저장 |
| `PlayHintCanvas.prefab` | Canvas 100, Raycaster 없음. Canvas 꺼진 채 저장 |
| `Assets/Font/Pretendard-Black SDF - Outline.mat` | PlayHint 외곽선(0.2, 검정) 머티리얼 프리셋 |

모든 Canvas는 Screen Space Overlay, CanvasScaler Scale With Screen Size 1920×1080·match 0.5, UiScaleBinding. 색상·크기·문구·배치는 기존 런타임 조립 코드 값을 그대로 옮겼다.

- MainMenu: MainMenuCanvas 인스턴스, EventSystem + InputSystemUIInputModule, 기존 MainMenu 오브젝트의 MainMenuController 참조 연결.
- Gameplay: GameMenuCanvas(인스턴스 비활성), HudCanvas, PlayHintCanvas, EventSystem. RoomGenerationDebug 오브젝트의 PauseMenu·WordRoomHud·PlayHint 참조 연결.
- RoomGenerationDebug(개발용): HudCanvas·PlayHintCanvas와 기존 WordRoomHud·PlayHint 연결만. 입력 UI가 없어 EventSystem은 추가하지 않는다. 다른 개발 구성은 바꾸지 않는다.

### 9.3 구성 도구 규칙

- 프리팹은 자산이 없을 때만 임시 빈 씬에서 만든다. 있으면 그대로 쓰며 다시 만들지 않는다(사용자 수정·override 보존).
- 컨트롤러 참조가 이미 있으면 유지하고 빈 참조만 채운다. 연결 대상은 다른 참조가 가리키는 인스턴스 → 씬의 유일한 인스턴스 → 새 배치 순이다.
- 인스턴스 2개 이상, EventSystem 2개 이상, 다른 입력 모듈, 프리팹 밖 UI를 가리키는 부분 연결은 `주의`로 보고하고 삭제·교체하지 않는다.
- 변경이 있는 씬만 저장한다. 완료·중단 로그 모두 실제 저장한 자산 목록을 출력한다.
- 클릭 리스너는 프리팹에 Inspector 이벤트로 저장하지 않는다. 런타임 컴포넌트가 Awake(설정 패널은 Initialize) 1회 코드로 추가하고, 해제는 자기 delegate만 RemoveListener한다.

### 9.4 런타임 동작 기준

- 초기 비활성 설정 패널: Open이 Initialize를 먼저 호출해 참조 확인·리스너 등록을 마친 뒤 활성화한다. 참조가 불완전하면 Open이 false를 반환하고 메뉴는 기존 화면을 유지한다.
- 설정값 표시는 SetValueWithoutNotify, 문구는 표시 값이 바뀔 때만 다시 만든다. GameSettings.Changed 구독은 패널이 열린 동안만이며 delegate는 1회 생성해 재사용한다.
- UI 크기: 끌기는 놓을 때, 키보드는 같은 프레임 LateUpdate 적용(KI-004 변경). UiScaleBinding은 저장된 CanvasScaler 기준 해상도를 1회 읽고 같은 배율은 다시 적용하지 않는다.
- 난이도 선택 표시는 Normal 색만 바꾸고, 선택 해제 시 Inspector에 저장된 Normal 색으로 돌린다(기존: ColorBlock 전체를 기본값으로 덮어씀).
- HUD·PlayHint: 9.1 표와 [Known Issues KI-008](02_Known_Issues.md) 변경 기록 참조. 진행 정지·실험 종료·미배치 안내 문자열은 원인·상태가 바뀔 때만 만든다(이전: 매 프레임 조립).
- Esc 메뉴가 불완전하면 플레이어의 Esc 처리를 빼앗지 않는다.

남은 제약: 도구 실행 전 씬은 UI 없음. 난이도 버튼 문구는 프리팹 생성 시 GameLaunch 값으로 고정되며 어휘 수를 바꾸면 프리팹 문구도 직접 고쳐야 한다. 슬라이더 범위도 프리팹 생성 시 GameSettings 범위로 저장된다. 프로파일링은 하지 않았으며 GC·성능 수치를 주장하지 않는다.

### 9.5 검토 반영 (2026-10-26 2차, 작업 트리·미커밋 / 적용 대기 / 실행 확인 전)

코드 검토 지적 3건을 구성 도구와 호환 필드로 보완했다. 아래는 코드 작성·읽기 결과이며 실행 결과가 아니다.

**중복·참조 혼합 (GameMenuSetup.Connect)**
- 빈 참조 유무와 관계없이 먼저 검사한다: 같은 UI 프리팹 인스턴스 2개 이상(중첩·비활성 포함), 연결된 참조가 서로 다른 인스턴스에 속함, 인스턴스와 프리팹 밖 UI가 섞임, 사용자 UI에 연결됐는데 연결 안 된 인스턴스가 따로 있음, 일부만 사용자 UI에 연결되고 나머지가 빔.
- 하나라도 해당하면 `주의`(씬·제어 컴포넌트 경로·인스턴스 경로·필드명)를 남기고 그 UI의 자동 배치·참조 채우기를 중단한다. 기존 참조·UI는 삭제·교체하지 않는다.
- 전부 사용자 UI에 연결된 완성 구성은 그대로 인정한다. 정상 단일 인스턴스는 빈 참조만 채운다.
- 인스턴스 판정은 참조 오브젝트에서 위로 올라가며 해당 프리팹의 인스턴스 루트를 찾는다(다른 프리팹 안에 중첩돼도 인식).
- 경고만 있는 씬은 저장하지 않는다. 기존 인스턴스 재사용 기록은 실제로 참조를 채웠을 때만 남긴다.

**EventSystem·입력 (GameMenuSetup.EnsureEventSystem)**
- EventSystem 2개 이상, 계층 비활성, EventSystem 컴포넌트 꺼짐, 다른 입력 모듈 동시 존재, InputSystemUIInputModule 중복·꺼짐을 모두 검사·보고한다. 켜거나 바꾸지 않는다.
- 메뉴에 필요한 포인터·클릭·스크롤·이동·제출·취소 액션과 Actions Asset의 참조 유무, 저장 가능 여부(임시 런타임 참조 아님), 실제 action 존재를 검사한다. 실행 시 기본 액션 자동 생성은 모든 액션이 비었을 때만 동작하고 저장되지 않으므로 정상 구성으로 보지 않는다.
- 새로 만드는 EventSystem·모듈(입력 모듈이 전혀 없는 EventSystem 포함)에는 설치된 Input System 패키지의 `Packages/com.unity.inputsystem/InputSystem/Plugins/PlayerInput/DefaultInputActions.inputactions`와 그 임포터가 저장하는 InputActionReference 하위 자산(UI 맵)을 직렬화 필드로 지정한다. 패키지의 `PlayerInputEditor.kDefaultInputActionsAssetPath`와 `InputSystemUIInputModuleEditor.ReassignActions`는 internal이라 같은 조회를 도구에 옮겼다. 이 자산을 찾지 못하면 EventSystem을 만들지 않고 보고한다.
- 정상 구성은 검사만 하므로 재실행해도 변경이 없다.

**전환 이전 UI 설정 이전 (LegacyUiMigration)**
- 과거 직렬화 값을 숨김 호환 필드로 다시 읽는다(`FormerlySerializedAs`): MainMenuController·PauseMenu `font`, WordRoomHud `font`·`sortingOrder`, PlayHint `font`·`fontSize`·`offset`. 런타임은 이 필드를 사용하지 않는다. 값이 저장돼 있지 않으면 표식값(int.MinValue, -1, NaN, null)으로 남아 "읽을 수 없음"으로 보고한다.
- 10월 26일 확인 시점의 세 씬 YAML에는 과거 값이 남아 있다(모두 Pretendard-Black SDF, HUD 50, 안내 30·(0, -90)). **이 버전 코드가 컴파일되기 전에 해당 씬을 열어 저장하면 과거 값이 사라질 수 있다.**
- 이전은 해당 씬의 프리팹 인스턴스에 instance override로 기록한다. 공통 프리팹은 바꾸지 않는다. 메뉴 폰트는 Canvas 아래 모든 TMP(중첩 설정 패널 포함), HUD 폰트는 HUD 텍스트 전체와 sortingOrder는 HUD Canvas, 안내는 텍스트의 폰트·크기와 anchoredPosition에 적용한다.
- 이미 같은 값이면 변경하지 않는다. 씬에서 이미 override된 속성은 사용자 수정값으로 보고 유지한다. 사용자 UI에 연결된 경우 값은 적용하지 않고 보고만 한다.
- 폰트를 바꿀 때 텍스트가 외곽선 등 머티리얼 프리셋을 쓰고 새 폰트 아틀라스와 맞지 않으면 `<폰트 폴더>/<폰트 이름> - <프리셋 접미사>.mat`을 한 번 만들어 외곽선 값을 복사한다. 같은 이름의 자산이 아틀라스와 맞지 않으면 덮어쓰지 않고 그 텍스트를 제외한다.
- 완료 표시 `legacyUiMigrated`는 참조가 완성되고(Connect 중단 없음) 모든 항목이 성공한 경우에만 켜며, 같은 씬 저장에 포함된다. 참조 미완성·일부 실패·저장 실패면 완료 표시가 저장되지 않아 다음 실행에서 남은 항목만 다시 시도한다. 완료 후에는 다시 이전하지 않아 사용자 수정값을 덮어쓰지 않는다.

### 9.6 프레임 두께 4 통일 (2026-10-26 3차, 작업 트리·미커밋 / 실행 확인 전)

- 원본 프리팹의 FrameTop·Bottom 높이와 FrameLeft·Right 너비를 1 → 4로 직접 수정했다(sizeDelta의 두께 축만 수정. 길이·앵커·피벗·위치·색·Raycast·LayoutElement는 그대로). SettingsPanel 56개, MainMenuCanvas 본문 40개, GameMenuCanvas 본문 20개, HudCanvas 4개.
- 중첩 설정 패널은 SettingsPanel 원본을 따른다. 바깥 프리팹·씬에 두께 override는 없었다.
- MainMenu 씬에서 사용자가 Main·Difficulty 패널과 시작·설정·종료 버튼의 프레임 20개를 override로 이미 4로 지정해 두었다. 이 씬 값은 그대로 두었다(이제 원본과 같은 값).
- `ArchiveUiBuilder.FrameThickness = 4`로 새로 만드는 프레임도 맞췄다. 프레임이 없는 PlayHint는 대상이 아니다.
