# WordRoom 방 생성 이식 2단계 — 생성 엔진·지도 상태·디버그 조작

Python 1.1.0 `kw_semantic_engine.Simulation`의 방 단어 생성과 이동을 Unity C#으로 옮겼습니다. 확인 수단은 Console 출력과 방향키뿐이며, 자동 플레이어·3D 방·지도 UI·Photon은 포함하지 않습니다.

> 상태: **코드 작성만 완료, 실행·컴파일·테스트·Python 비교는 하지 않았습니다.** 아래 6절의 절차로 사용자가 확인해야 합니다.

## 1. 파일

| 위치 | 어셈블리 | 역할 |
|---|---|---|
| `Scripts/Generation/GridPos.cs` | `WordRoom.Generation` | 좌표, 상·하·좌·우, 생성 순회 순서(Python `DIRECTIONS` = 상·우·하·좌) |
| `Scripts/Generation/GenerationConfig.cs` | 〃 | 생성 설정(값 출처 표시), 검증, 관계 기준 단계 |
| `Scripts/Generation/PythonRandom.cs` | 〃 | BLAKE2b, NumPy `SeedSequence`·`PCG64`, `Generator.choice` 이식 |
| `Scripts/Generation/TargetCloseness.cs` | 〃 | 목표 근접도, 엔진 전용 float32 내적 |
| `Scripts/Generation/RoomMap.cs` | 〃 | 지도 상태: 좌표별 단어, 생성 순서, 방문, 경로, 최근 단어, 미완성 기록, 진행 상태 |
| `Scripts/Generation/RoomGenerator.cs` | 〃 | 생성 엔진: 후보·순위·배치 계획·fastText 보충·채우기·이동 |
| `Scripts/Generation/GenerationResults.cs` | 〃 | 요청 결과 데이터(추가된 방, 연결 근거, 단계 기록, 빈 위치와 원인) |
| `Scripts/RoomDebug/RoomGenerationDebug.cs` | `WordRoom.RoomDebug` | Inspector 설정, 방향키, Debug.Log (MonoBehaviour) |
| `Editor/RoomDebugSceneTools.cs` | `WordRoom.Editor` | 메뉴 `WordRoom/Room Debug/Create Room Generation Debug Scene` |
| `Tests/EditMode/RoomGenerationTests.cs` | `WordRoom.Data.Tests` | 난수 이식 기준값, 합성 자료로 재현성·고정·중복·근거 검사 |

`WordRoom.Generation`은 MonoBehaviour·GameObject·Photon을 쓰지 않고, 데이터는 기존 `WordRoomDataset`(같은 로드 결과)을 조회합니다. 변경된 기존 파일: `WordRoom.Editor.asmdef`·`WordRoom.Data.Tests.asmdef`에 참조 추가. 입력 시스템 설정과 패키지는 바꾸지 않았습니다(프로젝트는 Input System 전용, `Keyboard.current` 사용).

## 2. Python → C# 대응

| Python 1.1.0 (`kw_semantic_engine.py`, `kw_engine.py`) | C# | 비고 |
|---|---|---|
| `Simulation.__init__`, `Simulation`(base)`.__init__` | `RoomGenerator.Create` | 설정 검증, 시작≠목표, 두 단어가 어휘 범위 안, 제한 표기, 목표 근접도 |
| `Config`, `load_config` 결과 | `GenerationConfig.FromSettings` | 데이터 패키지 `settings.json` |
| `threshold_ladder` | `GenerationConfig.ThresholdLadder` | 기본 설정이면 패키지에 저장된 float64 비트 그대로 |
| `restricted_words` | 패키지 `restricted.json` → `IsRestricted` | |
| `target_closeness`, `percentile` | `TargetCloseness.Compute` | |
| `guidance`, `closeness` | `RoomGenerator.Guidance`, `Closeness` | 진행 = 고유 방문 방 수(시작 방 포함) 또는 이동 수 |
| `coordinate_rng(pos).choice(..., p=)` | `PythonRandom.CoordinateUniform` + `Choice` | |
| `neighbors_of`, `candidates` | `NeighborsOf`, `Candidates` | 확정 방과 임시 배치 방 모두에 중복 거리 적용 |
| `evidence` | `Evidence` | YES 점수 → E5(float32) → 뜻 행 순 |
| `rank` | `Rank` | |
| `plan` (+`search`, `BudgetExhausted`) | `Plan` | 단계별 `no_candidate`·`placed`·`budget_exhausted`·`placement_conflict` |
| `report`, `explain` | `Report`, `Explain` | 같은 한국어 문구 |
| `db_room`, `commit` | `DbRoom`, `Commit` | |
| `ft_sims`, `ft_candidates`, `ft_links`, `plan_fasttext` | `FtSims`, `FtCandidates`, `FtLinks`, `PlanFasttext` | |
| `fill` | `Fill` | 같은 지도 상태(방 수)에서 실패한 방은 다시 시도하지 않음 |
| `reserve` | `RoomGenerator.Start` | 주변이 하나도 없으면 `no_exit` |
| `step(destination)` | `RoomGenerator.Move(direction)` | 도착은 진입 즉시 확정, 목표 방 주변도 채움, `max_moves` 도달 시 종료 |
| `choose`, `choose_v2`, 플레이어 `random.Random` | 이식 안 함 | 자동 플레이어는 범위 밖 |
| `check_target`, `result()`, 결과 JSON·통계 | 이식 안 함 | 기록 전용. 생성 결과에는 영향 없음 |

## 3. 흐름

1. Play → `WordRoomDataService`가 패키지를 작업 스레드에서 로드(완료 전 입력 무시).
2. 로드 완료 → `RoomGenerationDebug`가 Inspector 값을 `GenerationConfig`로 복사·고정하고 단어를 확인(정규화 후 없으면 오류 출력, 다른 단어로 바꾸지 않음).
3. 작업 스레드: `RoomGenerator.Create`(목표 근접도 계산) → `Start` = (0, 0)의 빈 주변을 함께 계획: 관계 DB 0.8→0.1(단계마다 백트래킹, 한도 2000) → 실패 시 fastText 보충(빈 위치 수 → 약한 연결 수 → fastText 연결 수 → 가장 약한 값(+유도) → 평균 순) → 확정.
4. 메인 스레드: 결과(`GenerationUpdate`)를 받아 출력.
5. 방향키: 한 프레임에 한 방향(↑↓←→ 순서로 첫 입력)만. 생성 중·로딩 중 입력은 버리고 쌓지 않음. 미생성 좌표면 이유를 출력하고 이동하지 않음.
6. 이동: 방문·이동 수 갱신 → 목표면 즉시 도착 확정 → 이동한 방의 빈 주변만 같은 절차로 생성(기존 방은 유지). 생성 실패는 이동·도착을 취소하지 않고 빈 위치와 원인을 기록.
7. 목표 도착 후 `목표 도착` 출력, 이후 방향키는 무시.

결과 데이터: `GenerationUpdate`(이동 전후 좌표, 첫 방문 여부, 상태, 유도 강도, `FillReport` — 추가된 방과 각 연결의 출처·뜻 쌍·YES 점수·적용 기준·fastText 유사도·DB 상태·약한 연결 사유, 기준별 단계, 빈 위치 원인). 호스트가 엔진을 돌리고 이 데이터만 다른 참가자에게 보내는 구조로 쓸 수 있습니다(네트워크는 구현하지 않음).

## 4. 난수·정렬·정밀도 방침

- 생성 엔진 전용 난수만 씁니다(`UnityEngine.Random` 미사용). 좌표 (x, y)마다 `blake2b("generator:{seed}:{x}:{y}", 16바이트)`를 little-endian 정수로 바꿔 NumPy `default_rng`를 새로 만들고 첫 난수 하나만 씁니다 — 같은 좌표의 재시도는 같은 난수를 씁니다(원본과 같음). SeedSequence·PCG64·`choice`(누적합 → `searchsorted(side='right')`)는 NumPy 알고리즘을 그대로 옮겼습니다.
- 후보 정렬은 Python `np.lexsort` 키를 그대로 씁니다. 관계 DB: 점수 내림차순 → 단어 순위. fastText: 약한 연결 수 → fastText 연결 수 → 조정값 내림차순 → 평균 내림차순 → DB YES(float32) 내림차순 → 단어 순위. 상위 `selection_top`(4)에서 온도 0.08 가중 추첨, 나머지는 점수 순.
- 자료형: Python에서 float32로 남는 값(E5·fastText 내적, novelty, `.1*diversity*novelty`, fastText 합계, DB YES 최솟값)은 엔진 안에서 float32로, float64 값(관계 점수·평균·근접도·유도 보너스)은 float64로 계산합니다(NumPy 2.5 NEP 50 승격 규칙 기준). 데이터 조회 계층(`WordRoomDataset.E5Similarity` 등, float64)은 바꾸지 않았습니다.
- **남은 차이**: NumPy는 float32 내적을 BLAS(SIMD 누적)로 계산하고, C#은 float64로 합한 뒤 float32로 한 번 반올림합니다. 둘은 마지막 비트(약 1e-7)가 다를 수 있어, 두 후보의 값이 그 정도로 붙어 있으면 정렬·동점·추첨 경계에서 결과가 갈릴 수 있습니다. `exp`도 구현이 달라 1ulp 차이가 날 수 있습니다. 따라서 **같은 시드라도 Python과 동일한 지도라고 보장하지 않습니다.** Unity 안에서는 같은 입력·설정·시드·이동 순서면 같은 결과가 나오도록 순서 의존 자료구조(사전 순회 등)를 쓰지 않았습니다.

## 5. 설정과 값 출처

Inspector(실험 시작 시 복사): 시작 단어, 목표 단어, 시드(42), 어휘 범위(15000, 1~8글자), 목표 유도(켬, 초기 0, 시작 10, 폭 40, 최대 0.03, 고유 방문 방 수), 상세 로그, 데이터 준비되면 자동 시작. 시작·목표 기본값 `커피`·`우유`는 README 예시 단어일 뿐입니다.

나머지(`GenerationConfig.FromSettings`, 출처: 데이터 패키지 `settings.json` = Python 1.1.0 `load_config` 적용값): 중복 금지(켬, 주변 3칸, 맨해튼 거리), `selection_top` 4, 온도 0.08, `recent_window` 64, 다양성 0.6, `neighbor_weight` 0.5, 관계 기준 0.8/폭 0.1/최저 0.1, 배치 탐색 한도 2000, fastText 보충 켬, 선호 하한 0.2, 최대 이동 300. 제한 표기는 패키지 `restricted.json`(Python `configs/fasttext_restricted.json`, `ft-restrict-v2`).

## 6. 사용자 확인 절차

준비: 에디터에서 메뉴 **WordRoom → Room Debug → Create Room Generation Debug Scene** 실행 → `Assets/WordRoom/Scenes/RoomGenerationDebug.unity`가 생기고 `RoomGenerationDebug` 오브젝트가 선택됩니다(기존 `DataCheck` 장면은 그대로). 수동으로 하려면 빈 장면에 빈 GameObject를 만들고 `RoomGenerationDebug` 컴포넌트를 추가하면 `WordRoomDataService`가 함께 붙습니다. Inspector에서 값을 정하고 Play, Game 창을 클릭해 포커스를 준 뒤 방향키를 누릅니다. 상세 로그는 Inspector의 `Verbose Log`.

| 확인 | 방법 | 기대 |
|---|---|---|
| 첫 방·주변 | Play | `[방 생성] 새 실험 준비…` 뒤 `현재: 커피 (0, 0) \| 목표: 우유` + 상·하·좌·우 4줄. 없는 방향은 `미생성` |
| 이동·좌표 | ↑/↓/←/→ | 좌표가 각각 y+1 / y-1 / x-1 / x+1, 새 위치 기준으로 다시 출력 |
| 재방문 단어 유지 | 갔다가 되돌아오기 | 같은 좌표는 항상 같은 단어. 되돌아온 방에서 새 생성 없음(상세: "주변 4방향이 이미 모두 생성") |
| 새 주변 생성 | 처음 가는 방으로 이동 | 지나온 방을 뺀 최대 3방향만 새로 생김(상세 `+` 줄) |
| 중복 제한 | 여러 방 생성 후 출력 비교 | 맨해튼 거리 3칸 안에 같은 표기 없음(임시 배치 포함) |
| 연결 근거 | 상세 로그 | 새 방마다 이웃별 `관계 DB [뜻ID]–[뜻ID] YES 점수 (기준 0.x)` 또는 `fastText 유사도 · DB 상태` |
| 기준 완화·보충 | 상세 로그 | `기준 0.8: 후보 없는 위치 있음` … 다음 기준으로 내려가거나, 0.1까지 실패하면 `관계 DB 실패 사유:`와 `fastText 보충`, 약한 연결이면 사유 |
| 미생성 이동 | 미생성 방향 키 | `미생성이라 이동할 수 없습니다 — 사유`, 좌표 그대로 |
| 재현성 | 같은 설정·시드로 Play 두 번, 같은 키 순서 | 출력이 같음. 시드를 바꾸면 달라질 수 있음 |
| 목표 도착 | 목표 단어가 보이는 방향으로 이동 | 출력 끝에 `목표 도착`, 이후 방향키 무시(`실험이 끝나 이동하지 않습니다`) |
| 입력 차단 | 로딩 중·생성 중(특히 목표 근접도 계산 중) 키 연타 | 이동하지 않고, 끝난 뒤에도 쌓인 입력이 실행되지 않음(상세 로그면 "입력을 무시") |
| 잘못된 입력 | 어휘에 없는 단어·같은 시작/목표 | 오류 출력, 다른 단어로 대체하지 않음 |
| 테스트 | Test Runner → EditMode → `PythonRandomTests`, `RoomGeneratorFixtureTests` | 통과(난수 이식은 NumPy·RFC 공개값과 비교) |

### Python과 직접 비교(선택)

Python 쪽은 평소 실행 경로(`SemanticLexicon`, 관계 DB를 일반 앱처럼 엶)를 씁니다.

```python
# D:\Pro_KW\WordRoom 에서: semantic_runtime\Scripts\python.exe
import hashlib, numpy as np
def u(seed, x, y):
    d = hashlib.blake2b(f'generator:{seed}:{x}:{y}'.encode(), digest_size=16).digest()
    return np.random.default_rng(int.from_bytes(d, 'little')).random()
print(repr(u(42, 0, 1)))          # PythonRandom.CoordinateUniform(42, (0,1))와 비교

from kw_semantic import SemanticLexicon
from kw_semantic_engine import Config, Simulation
lex = SemanticLexicon(); v = lex.view
sim = Simulation(v, Config(seed=42), v.resolve('커피'), v.resolve('우유'))
def show():
    x, y = sim.pos
    print(v.words[sim.rooms[sim.pos]], sim.pos, {k: v.words[sim.rooms[(x+dx, y+dy)]] if (x+dx, y+dy) in sim.rooms else '미생성'
          for k, (dx, dy) in {'상': (0, 1), '하': (0, -1), '좌': (-1, 0), '우': (1, 0)}.items()})
show(); sim.step((1, 0)); show()   # Unity에서 → 키를 누른 결과와 비교
```

차이가 나면 4절의 정밀도 차이인지, 이식 오류인지 첫 차이 좌표의 상세 로그와 Python `sim.plans[-1]`을 비교해 확인합니다.

## 7. 요구사항과의 관계·남은 일

- 원본 동작을 유지한 부분: 최대 이동 300회에 도달하면 `이동 한도 도달`로 끝납니다. 목표 방에 들어가도 그 방의 빈 주변은 채웁니다. 생성 순회 순서는 상·우·하·좌(출력은 상·하·좌·우). fastText 보충의 임시 배치 간 중복 검사는 `avoid_adjacent_repeat` 설정과 무관하게 거리 3을 씁니다(원본과 같음).
- 이식하지 않은 것: 자동 플레이어(`choose`), 목표 후보 검사 기록(`check_target`), 결과 JSON·통계.
- fastText 유사도 캐시는 최근 256개 표기로 제한(원본은 무제한) — 결과에는 영향 없음.
- 확인 필요: 컴파일, 테스트, 실제 생성 시간(특히 1글자·다의어 목표의 근접도 계산), Python과의 지도 비교, Android(범위 밖).
