> 2026-10-23 최신 안내: 구현 기준은 `79a6ba2`입니다. 문 열기 시 주변 생성(Prepare)·실제 입장(Enter), 아카이브 모델(간격 6.2m·출입구 폭 1.3m), 현재·목표 HUD, 메인 메뉴·난이도·설정, 표시 반경 1~3칸이 구현·커밋됐습니다. 이전 플레이어 전환 문제 2건도 후속 구현에서 보완됐습니다. 최신 상태는 [인수인계](00_프로젝트_인수인계.md), [전체 계획](01_전체개발계획_및_진행현황.md), [최신 구현 상세](WordRoom_문상호작용_및_게임흐름.md), [미해결 사항](02_Known_Issues.md)을 따릅니다.
>
> **아래 본문은 해당 단계 당시 기록입니다.** 미구현·최대 5개·구형 치수·미수정 문제 등 당시 설명을 현재 상태로 해석하지 않습니다. 과거 측정·사용자 확인을 오늘 재실행한 결과로 확대하지 않습니다.

# WordRoom 데이터 이식 1단계 — 데이터 패키지·내보내기·Unity 로더

Python 1.1.0(`D:\Pro_KW\WordRoom`)이 준비한 어휘·뜻·관계 판정·벡터를 Unity가 Python·AI 모델 없이 읽고 조회하게 하는 단계입니다.
방 생성·배치·이동·Photon은 포함하지 않습니다.

## 1. 구성

| 위치 | 내용 |
|---|---|
| `Tools/DataExport/` | Python 내보내기 도구 (`export_unity_data.py`, `wr_*.py`) |
| `Assets/StreamingAssets/WordRoomData/` | 활성 데이터 패키지 (생성물, Git 제외) |
| `Assets/WordRoom/Scripts/Data/` | `WordRoom.Data` — 패키지 규격·로더·조회 (장면·GameObject·Photon 무관) |
| `Assets/WordRoom/Scripts/Validation/` | `WordRoom.Data.Validation` — Python 기준값 비교, 조회 시간 측정 |
| `Assets/WordRoom/Scripts/Unity/` | `WordRoom.Unity` — StreamingAssets 접근, 로딩 서비스, 개발용 확인 화면, 빌드 자체 검증 |
| `Assets/WordRoom/Editor/` | 확인 장면 생성, Windows 확인 빌드 메뉴 |
| `Assets/WordRoom/Scenes/DataCheck.unity` | 개발용 데이터 확인 장면 (게임 UI 아님) |
| `Assets/WordRoom/Tests/EditMode/` | EditMode 테스트 |
| `Assets/WordRoom/Tests/Fixtures~/Synthetic/` | 합성 시험 패키지와 그 Python 기준값 (`~` 폴더라 Unity가 임포트하지 않음) |
| `DataValidation/real/` | 실데이터 Python 기준값·내보내기 보고서 (생성물, Git 제외) |
| `DataPackages/` | 작업 폴더(`staging/`), 교체된 이전 묶음(`previous/`, fixture는 `fixture_previous/`, 기본 최근 2개), 실패 작업(`failed/`), 진행 중 교체 기록(`swap/*.journal.json`) (Git 제외) |

## 2. 내보내기

```powershell
cd D:\Pro_KW\Unity\WordRoom\WordRoom_Unity
D:\Pro_KW\WordRoom\semantic_runtime\Scripts\python.exe -B Tools\DataExport\export_unity_data.py export --source D:\Pro_KW\WordRoom
# 활성 패키지를 현재 원본과 다시 대조만 할 때
D:\Pro_KW\WordRoom\semantic_runtime\Scripts\python.exe -B Tools\DataExport\export_unity_data.py verify --source D:\Pro_KW\WordRoom
# 합성 시험 패키지를 다시 만들 때 (내보내기 코드나 규격을 바꾼 경우)
D:\Pro_KW\WordRoom\semantic_runtime\Scripts\python.exe -B Tools\DataExport\export_unity_data.py fixture --source D:\Pro_KW\WordRoom
```

순서 (`export`):
1. **관계 DB 스냅샷**: `relations.sqlite`를 읽기 전용(`mode=ro`) 연결에서 SQLite 온라인 백업 API로 작업 폴더에 복사합니다. WAL에 커밋된 내용까지 일관된 시점으로 담기며, 원본 DB·WAL은 바뀌지 않습니다(SQLite가 읽기 잠금을 위해 `-shm` 공유 메모리 파일의 수정 시각만 갱신).
2. **원본 확인**: `cache/semantic/vocabulary.json`의 활성 어휘(현재 `v2_15000`)를 `kw_semantic.vocab_dir()`로 찾고, Python과 같은 일관성 검사(뜻 순서 해시, 행·열, 유한값, 단위 노름, 표기↔뜻 대응, fastText 유효 표시)를 합니다. 수량이 다르면 잘라내거나 다시 만들지 않고 실패합니다.
3. **패키지 작성**: 작업 폴더 `DataPackages/staging/<시각>/WordRoomData`에 씁니다.
4. **독립 재검증**: 쓴 파일을 다시 읽어 원본 JSON·`.npy` 바이트·스냅샷 DB 행과 대조합니다.
5. **Python 기준값·보고서**: 원본 Python 코드로 기준값(`python_reference.json`)을 계산하고, 보고서(`export_report.json`)까지 작업 폴더에서 완성합니다. 여기까지 실패하면 활성 묶음은 바뀌지 않고 작업 폴더는 `DataPackages/failed/`에 남습니다.
6. **묶음 교체**: 패키지·기준값·보고서를 같은 packageId의 한 묶음으로 바꿉니다(2.1절). 교체 성공이 확정된 뒤에만 오래된 백업과 작업 폴더를 정리합니다.

### 2.1 묶음 교체와 복구 (`Tools/DataExport/wr_swap.py`)

묶음은 세 위치입니다: `Assets/StreamingAssets/WordRoomData/`, `DataValidation/real/python_reference.json`, `DataValidation/real/export_report.json` (fixture는 `Tests/Fixtures~/Synthetic/` 아래 같은 이름). 세 항목은 각각 같은 드라이브 안의 이름 바꾸기로 옮기며, **여러 항목의 교체가 하나의 원자적 작업은 아닙니다.** 대신 교체 기록으로 중간 상태를 식별하고 되돌립니다.

1. **미완료 교체 정리**: `DataPackages/swap/<real|fixture>.journal.json`이 있으면 새 작업 전에 처리합니다. 기록 상태가 `committed`면 마무리(백업 표시·기록 삭제)만 하고, 그 밖의 상태면 이전 묶음으로 되돌립니다. 되돌릴 수 없으면 아무것도 지우지 않고 중단합니다(종료 코드 3).
2. **준비**: 위 1~5단계. 활성 항목은 건드리지 않습니다.
3. **기록·백업 폴더**: 교체 기록을 먼저 쓰고(각 항목의 활성·백업·작업 위치, 이전/새 packageId), 새 백업 폴더 `DataPackages/previous/<시각>_<이전 ID 12자>`를 만듭니다. 같은 이름이 있으면 `.1`, `.2`…를 붙이며 기존 백업을 덮어쓰지 않습니다. 기존 항목이 하나도 없으면(최초 내보내기) 백업 폴더를 만들지 않습니다.
4. **교체**: 항목마다 기존 항목을 백업 폴더로 옮긴 뒤 새 항목을 활성 위치로 옮기고, 단계마다 기록을 갱신합니다. 이동은 대상이 이미 있으면 거부합니다(덮어쓰기 없음).
5. **확정**: 세 활성 항목의 packageId가 모두 새 ID와 같을 때만 기록을 `committed`로 바꾸고, 백업 폴더의 `bundle.json`을 `superseded`로 표시한 뒤 기록을 지웁니다.
6. **정리**: `superseded` 표시가 있는 백업 묶음 중 최근 `--keep-previous`개(기본 2)를 남기고 지우며, 끝난 작업 폴더를 지웁니다. 진행 중·복구 실패 백업, 표시 없는 폴더(이전 버전 도구가 만든 것 포함), `staging/`·`failed/`는 지우지 않습니다. 정리 실패는 경고일 뿐 새 묶음은 활성 상태입니다.

복구 규칙(실패 시 즉시, 강제 종료 뒤에는 다음 실행에서): 항목별로 실제 위치를 보고 판단합니다. 백업 위치에 이전 항목이 있으면 활성 위치의 항목은 새 것이므로 작업 폴더로 되돌리고 이전 항목을 제자리로 옮깁니다. 백업이 없으면 활성 위치에 이전 항목(기록된 packageId)이 그대로 있어야 합니다. 원래 없던 항목은 활성 위치에서 치워 없는 상태로 되돌리고, 교체가 만든 빈 상위 폴더도 지웁니다. 판단이 기록과 맞지 않으면 그 항목은 손대지 않고 기록을 `rollback_failed`로 남깁니다. 복구는 이동만 하며 어떤 버전도 지우지 않습니다.

| 결과 | 메시지 | 종료 코드 | 상태 |
|---|---|---|---|
| 준비 실패 | `준비 실패 — 기존 … 변경하지 않았습니다` | 1 | 기존 묶음 그대로, 작업 폴더는 `failed/` |
| 교체 실패, 복구 성공 | `교체 실패 → 기존 묶음으로 복구 완료` | 2 | 기존 묶음, 새 묶음은 `failed/` 작업 폴더 |
| 교체 실패, 복구 실패 / 미완료 교체 정리 실패 | `수동 복구가 필요합니다` / `중단 —` + 항목별 위치 | 3 | 기록·백업·작업 폴더 모두 보존 |
| 교체 성공, 정리 실패 | `새 묶음 활성화 완료` + `경고(내보내기는 성공, 정리 단계)` | 0 | 새 묶음 활성 |

`verify`는 교체 기록이 남아 있거나 세 항목의 packageId가 다르면 종료 코드 3으로 알립니다.

시험용 오류 주입: 환경 변수 `WR_SWAP_FAULT`에 `backup:<항목>`, `install:<항목>`, `verify`, `rollback:<항목>`, `cleanup`(해당 단계에서 오류), `crash:after-backup:<항목>`, `crash:after-install:<항목>`(즉시 종료, 코드 97)을 쉼표로 지정합니다. `<항목>`은 `package`, `reference`, `report`. 운영 프로젝트가 아닌 시험용 복사본(`--unity`)에만 쓰세요. 지정하지 않으면 아무 영향이 없습니다.

쓰지 않는 것: `SemanticLexicon` 생성자(관계 DB를 쓰기 모드로 열어 저널 모드·스키마·규칙 재적용·이전 기록 이전을 수행), 판정·임베딩 모델, 원본 사전, fastText 원본 모델, 가상환경. 원본 파일은 읽기만 하며 Python 프로젝트에 `__pycache__`도 만들지 않습니다(`-B`, `sys.dont_write_bytecode`).

## 3. 패키지 규격 (스키마 1)

| 파일 | 형식 | 내용 |
|---|---|---|
| `manifest.json` | JSON | 형식 식별자 `WordRoomDataPackage`, `schemaVersion` 1, `packageId`, 데이터 식별 정보(`identity`), 수량, 파일별 크기·SHA-256·레코드 수·행/열·숫자 형식·바이트 순서·배치·행 키 |
| `words.json` | JSON | 빈도 순 표기 `words`, Python `len()` 글자 수 `lengths`, 표기→뜻 대응 `senseOffsets`/`senseRows`(CSR), fastText 유효성 `fasttextValid` |
| `senses.json` | JSON(열 배열) | 뜻 행별 `senseIds`(원본 뜻 ID), `wordRanks`, `entryIds`, `raws`, `pos`, `kinds`, `categories`, `definitions` |
| `e5_sense_vectors.bin` | 바이너리 | E5 뜻 벡터 float32, 행 = 뜻 행 |
| `fasttext_word_vectors.bin` | 바이너리 | fastText 표기 벡터 float32, 행 = 표기 빈도 순위. 벡터 없는 표기는 0행 + `fasttextValid`=0 |
| `relations.bin` | 바이너리 | 호환되는 뜻 쌍 판정 레코드(64바이트) |
| `relations.json` | JSON | 판정 버전·규칙·모델, 상태/답/방법/시각 문자열 표, 제외 수량, 스냅샷 정보, 준비 기록(`preparedSources`), 기준 단계 |
| `restricted.json` | JSON | fastText 제한 표기 목록(목록 버전, 순위, 선정 근거)과 적용 방식 |
| `settings.json` | JSON | 실제 적용 설정, 원시 값, 변경·기본값·무시 항목, 로더 안내문, 기준 단계 |

### 식별자

- **표기 순위**(word rank): `words.json` 배열 위치 = fastText 빈도 순위(0부터). 방의 정체성은 표기입니다.
- **뜻 행**(sense row): `senses.json` 배열 위치 = E5 벡터 행. 원본 정렬(정수 뜻 ID 오름차순)을 그대로 따릅니다. **뜻 행은 원본 뜻 ID가 아닙니다.** 원본 ID는 `senseIds[행]`, 역조회는 `TryGetSenseRow(id)`.
- 표기→뜻: `senseRows[senseOffsets[w] .. senseOffsets[w+1]]` (뜻 행 오름차순, Python `word_senses`와 같은 순서).
- 어휘 범위(10,000개 등)는 표기 순위·뜻 행을 바꾸지 않고 허용 표기만 거릅니다. 따라서 범위를 줄여도 뜻·벡터 대응은 그대로입니다.
- **패키지 ID**: 원본 파일 해시(ready·뜻 목록·벡터·fastText·설정·제한 목록·모델 manifest), 관계 내용 해시, 적용 설정 해시, 형식 버전으로 계산한 SHA-256. 모든 JSON의 `packageId`와 모든 바이너리 헤더(앞 16바이트)에 들어 있어, 다른 내보내기의 파일이 섞이면 해시 검사 없이도 검출됩니다.

### 바이너리 헤더 (64바이트, little-endian)

| 오프셋 | 형식 | 필드 |
|---|---|---|
| 0 | char[8] | 형식 식별자 `WRDMATF4`(float32 행렬) / `WRDRELV1`(관계 레코드) |
| 8 | u32 | 스키마 1 |
| 12 | u32 | 헤더 크기 64 |
| 16 | u64 | 행 수 |
| 24 | u32 | 열 수 (행렬: 차원, 관계: 레코드 바이트 64) |
| 28 | u32 | 숫자 형식 1=float32 IEEE-754, 2=관계 레코드 v1 |
| 32 | u32 | 바이트 순서 표시 0x01020304 (파일에는 `04 03 02 01`) |
| 36 | u32 | 배치 1=행 우선(C 순서) |
| 40 | u32 | 행 키 1=뜻 행, 2=표기 순위, 3=관계 레코드 |
| 44 | u32 | 원소 바이트 (float32 4, 관계 64) |
| 48 | byte[16] | 패키지 ID 앞 16바이트 |

본문은 헤더 바로 뒤에 이어지며 파일 크기 = 64 + 행×열×원소 바이트입니다. 벡터는 원본 `.npy`의 float32 바이트 그대로입니다(양자화·재정규화·재계산 없음).

### 관계 레코드 v1 (64바이트, `(min 뜻 행, max 뜻 행)` 오름차순, 뜻 쌍당 하나)

| 오프셋 | 형식 | 필드 |
|---|---|---|
| 0 / 4 | i32 / i32 | 뜻 행 A / B — DB 열 a/b 순서(문자열 뜻 ID a<b), 정방향 판정 = A→B |
| 8 | u8 | 상태 → `statusNames` (`judged`, `partial`, `invalid`, …) |
| 9 | u8 | `valid_yes` (저장값) |
| 10 / 11 | u8 | 정방향 / 역방향 답 코드 (0=없음, k=`answerNames[k-1]`) |
| 12 | u8 | 판정 방법 → `methodNames` |
| 13 | u8 | 플래그: bit0 `backward_skipped` 있음, bit1 그 값, bit2 `cached_scores` 있음 |
| 14 / 15 | u8 | 캐시 정방향 / 역방향 답 코드 |
| 16 | u16 | 판정 시각 → `createdAtValues` |
| 24 | f64 | 저장 점수(`score`, 반올림 없음) |
| 32 / 40 | f64 | 정방향 / 역방향 점수 (NaN=없음) |
| 48 / 56 | f64 | 캐시 정방향 / 역방향 점수 (NaN=없음) |

## 4. 관계 정보와 B 규칙

`kw_relations.RelationDB`가 읽는 행과 같은 행만 내보냅니다: 현재 판정 버전(판정 모델·지시문·사전으로 만든 해시)이고, 두 뜻이 현재 어휘에 있으며, 뜻풀이 서명(`signature`)이 일치하는 행. 0.8 이상 승인만이 아니라 **모든 상태**(승인 YES, 기준 미달 YES, NO, partial, invalid)를 원래 점수 그대로 보존합니다. 미검사 조합은 행을 만들지 않고 "기록 없음"으로 판단합니다.

- 뜻 쌍 승인: `status == judged` 이고 `valid_yes == 1` 이고 `score >= 기준`. partial(한 입력 순서만 판정)·invalid(응답 오류)는 YES가 있어도 승인하지 않습니다. 저장 컬럼을 그대로 쓰며(Python play와 동일), 내보내기 때 payload의 순서별 답으로 다시 계산한 값(`decide`)과 다른 행 수를 `relations.json counts.decideMismatches`에 기록합니다(현재 0).
- 단어 쌍 상태 (`RelationIndex.GetWordPairState`, Python `word_state`와 같은 순서): 승인 조합이 하나라도 있으면 **승인** → 아니면 저장 YES 기준 미달 → 오류·불완전 → 미검사 조합 → 모든 현재 조합이 NO(**전부 NO**). 한 조합의 NO는 단어 쌍 거절이 아닙니다.
- 같은 표기의 두 뜻 사이 기록은 단어 그래프에 넣지 않고(자기 연결 없음), 추이성(A–B, B–C → A–C)은 쓰지 않습니다.
- 최고 근거 (`TryGetBestEvidence`): YES 점수 최고 → E5 유사도 최고 → 뜻 행 순서 (`Simulation.evidence`와 같음).
- 기준 단계: 적용 설정의 `threshold_ladder` 결과(0.8, 0.7, … 0.1)를 float64 비트로 저장해 경계값에서 결과가 달라지지 않게 했습니다.

## 5. 설정

`settings.json` 원시 내용이 아니라 Python 1.1.0이 실제로 쓰는 값을 내보냅니다: `kw_app.App.load_saved_config`의 이전 설정 보정(의미 기반 기본 규칙, 10,000→15,000) 후 `kw_semantic_engine.load_config`(폐기 항목 무시, 0.5.0 이전 최소 글자 수 2→1, 누락 항목 기본값)를 쓰기 없이 재현합니다. 결과는 `settings.json`의 `entries`(`change`: same/changed/default)와 `droppedRawKeys`, `notices`에 있습니다. Python 원본 `settings.json`은 바꾸지 않습니다.

## 6. Unity 사용

```csharp
// 장면: WordRoomDataService 컴포넌트 (Start에서 작업 스레드로 한 번 로딩)
if (service.TryGetDataset(out var d)) { ... }      // 로딩 중·실패 시 false
var data = service.Dataset;                         // 준비 전이면 DataNotReadyException

// 장면 없이 (도구·테스트)
var d = WordRoomDataLoader.Load(new DirectoryPackageSource(path), new LoadOptions { VerifyHashes = true });

d.TryGetWordRank("경기", out var w);               // Python과 같은 표기 정규화
ReadOnlySpan<int> senses = d.SenseRowsOf(w);        // 뜻 행 → d.GetSense(row).SenseId / Definition
var scope = d.CreateScope(10000, 1, 8);             // 준비 범위를 넘으면 ArgumentException
var t = d.ThresholdLadder[0];                       // 0.8
var neighbors = d.Relations.GetApprovedNeighbors(w, t, scope);
var state = d.Relations.GetWordPairState(w, n, t);
d.Relations.TryGetBestEvidence(w, n, t, out var e); // e.SenseRow, e.NeighborSenseRow, e.Score
d.Relations.GetCombinations(w, n, t);               // 모든 뜻 조합과 저장 판정
d.TryFastTextSimilarity(w, n, out var ft);          // 벡터 없으면 false (엔진 보충은 [0,1]로 자름)
d.E5Similarity(rowA, rowB);
d.IsRestricted(w); d.Settings.GetInt("vocabulary_limit"); d.Identity.relationVersion;
```

- 벡터는 로딩 때 한 번 `float[]`로 읽고 조회는 `ReadOnlySpan` 뷰로 반환합니다(복사 없음, Inspector 직렬화 없음).
- 오류는 `WordRoomDataException.Code`로 구분합니다: `MissingFile`, `SizeMismatch`, `HashMismatch`, `PackageIdMismatch`, `UnsupportedSchema`, `InvalidFormat`, `CountMismatch`, `InvalidReference`, `DuplicateEntry`, `InvalidValue`, `UnsupportedPlatform`, `Cancelled`.
- 파일 접근은 `IPackageFileSource` 뒤에 있습니다. Editor·Windows 빌드는 `StreamingAssets/WordRoomData`를 폴더로 읽습니다(`Application.streamingAssetsPath` 기준, 개발 PC 절대 경로 없음). **Android는 지원하지 않습니다**: StreamingAssets가 APK 안에 있어 폴더로 읽을 수 없으므로 `persistentDataPath`로 복사하는 소스 구현이 필요하며, 현재는 `UnsupportedPlatform` 오류를 냅니다.

## 7. 개발용 확인 화면

`Assets/WordRoom/Scenes/DataCheck.unity`를 열고 Play. 로딩 상태·패키지/관계/모델 버전·수량, 적용 설정 차이, 관계 기준(0.8~0.1), 어휘 범위(15,000/10,000·글자 수), 단어 A·B의 뜻 목록(선택 가능), A의 승인 이웃과 최고 근거(클릭하면 B로 설정), A–B 관계 상태·뜻 조합별 판정(순서별 답·점수·방법·시각), fastText 유사도, 선택한 두 뜻의 E5 유사도, 제한 표기 여부를 보여 줍니다. 표시 값은 모두 로딩한 패키지에서 조회합니다. 메뉴 `WordRoom/Data`에서 장면 생성, 패키지 대조, Windows 확인 빌드를 실행할 수 있습니다.

## 8. 검증

- **Python 기준값** (`wr_reference.py`): 내보낸 패키지가 아니라 원본 파일을 원본 Python 코드로 조회합니다 — `RelationDB.word_state/words/rejected/evidence`, `SemanticLexicon.eligible_ids`·`WordView.eligible_ids`, `Simulation.evidence`, `threshold_ladder`, `load_config`, 엔진과 같은 numpy float32 내적.
- **비교** (`ReferenceComparer`): 표기·빈도 순서·글자 수, 단어별 뜻 ID, 뜻 문자열 전체, 벡터 전체 바이트 SHA-256과 ID로 찾은 행, fastText 유효성, 기준 단계 비트, 어휘 범위(오류 포함), 기준별 승인 이웃 전체, 범위 안 승인 이웃, 기록 있는 모든 단어 쌍의 기준별 상태, 최고 근거와 점수 비트, 전부 NO 쌍, 뜻 쌍 레코드와 순서별 답, 범주별 개수, 유사도 오차, 제한 표기, 적용 설정.
- **합성 자료** (`Fixtures~/Synthetic`): 실데이터에 없는 partial·invalid 판정, 정확히 0.8 / 0.7999999999999999 / 0.5000000000000001 점수, 다른 판정 버전·뜻풀이 변경·어휘 밖 행, fastText 없는 표기, 비BMP·따옴표·역슬래시·NBSP 문자열, 8글자·1글자 표기.
- **손상 검출**: 패키지·파일 누락, 바이트 변조(해시), 잘림(크기), 다른 내보내기의 JSON/바이너리(패키지 ID), 스키마, 차원, 범위 밖 뜻·표기 참조, 중복 레코드, NaN, 벡터 없음 표시 불일치.
- EditMode 테스트: Test Runner에서 `WordRoom.Data.Tests` 실행, 또는
  `Unity.exe -batchmode -projectPath <프로젝트> -runTests -testPlatform EditMode -assemblyNames WordRoom.Data.Tests -testResults results.xml`
- 빌드 자체 검증: `WordRoomDataCheck.exe -batchmode -nographics -wordroomVerify -wordroomReference <python_reference.json> -wordroomReport report.txt` → 로딩·메모리·기준값 비교·조회 시간 보고, 종료 코드 0(통과)/1(실패).

## 9. 데이터 갱신 절차

1. Python에서 어휘 준비 또는 관계 DB 준비를 마칩니다(내보내기는 준비 작업과 동시에 실행해도 스냅샷으로 일관된 시점을 읽지만, 끝난 뒤 실행하는 편이 기록이 명확합니다).
2. `export_unity_data.py export`를 실행합니다. 패키지 ID가 바뀌고 이전 묶음(패키지·기준값·보고서)은 `DataPackages/previous/<시각>_<ID>/`에 남습니다. 종료 코드와 메시지로 결과를 확인합니다(2.1절 표).
3. Unity에서 EditMode 테스트(`RealPackageMatchesPythonReference`)로 새 기준값과 대조합니다.
4. 되돌리기: Unity 에디터를 닫고, 현재 `WordRoomData` 폴더와 `DataValidation/real`의 두 JSON을 다른 곳으로 옮긴 뒤 `DataPackages/previous/<시각>_<ID>/`의 `WordRoomData`, `python_reference.json`, `export_report.json`을 각각 제자리로 옮깁니다. 세 항목을 함께 옮겨야 packageId가 맞습니다.
5. 패키지 규격을 바꾸면 `wr_format.py`와 `PackageFormat.cs`를 함께 고치고 스키마 번호를 올린 뒤 `fixture`를 다시 만듭니다.

## 10. 첫 내보내기 결과 (2026-10-19, 패키지 `6773adfc0093…`)

| 항목 | 값 |
|---|---|
| 원본 | `D:\Pro_KW\WordRoom` 활성 어휘 `v2_15000` (2026-10-16 준비), Python 1.1.0 |
| 표기 / 뜻 | 15,000 / 48,318 (1글자 607), E5 768차원, fastText 300차원(15,000개 모두 유효) |
| 관계 판정 버전 | `7a8d00eab7b5…` (Qwen3-4B-Instruct-2507 `cdbee75f`, 지시문 `direct-relation-v2`), 규칙 `either-order-yes-v1` |
| 관계 DB 행 | 85,440행(모두 현재 버전) 중 호환 82,921행 내보냄, 어휘 밖 뜻 2,519행 제외(서명 불일치 0, 다른 버전 0). 0.4.0 `judgments` 표 1,235행은 이미 이전된 구 기록이라 제외 |
| 호환 행 상태 | 모두 `judged` (YES 28,445 / NO 54,476). partial·invalid는 실데이터에 없어 합성 자료로 검증 |
| 승인 단어 쌍 | 0.8: 20,944 · 0.7: 21,294 · 0.6: 21,602 · 0.5~0.1: 21,832 |
| 범주(0.8) | 여러 조합 중 하나만 승인 15,451 · NO 조합이 있어도 승인 3,350 · 미검사 조합이 있어도 승인 19,142 · 전부 NO 2,309 · 미검사 남은 미승인 24,601 · 기준 미달 YES 888 · 오류·불완전 0 |
| 준비 기록 | `prepared_sources` 2,610행 (서명 불일치 59행 제외) |
| 제한 표기 | `ft-restrict-v2` 102개, 모두 어휘 안 |

### 패키지 크기

| 파일 | 바이트 | 레코드 |
|---|---:|---:|
| `e5_sense_vectors.bin` | 148,432,960 | 48,318 × 768 |
| `fasttext_word_vectors.bin` | 18,000,064 | 15,000 × 300 |
| `senses.json` | 7,526,148 | 48,318 |
| `relations.bin` | 5,307,008 | 82,921 |
| `words.json` | 582,147 | 15,000 |
| `relations.json` | 97,939 | |
| `restricted.json` / `settings.json` / `manifest.json` | 19,087 / 6,690 / 7,566 | |
| 합계 | 179,979,609 (171.6 MiB) | |

원본 벡터(`vectors.npy` 148.4MB + `fasttext.npy` 18.0MB)와 같은 바이트이며, 늘어난 것은 헤더와 JSON입니다.

## 11. 검증 결과와 측정 (2026-10-19)

검증은 열려 있는 에디터와 충돌하지 않도록 프로젝트를 임시 복사해 Unity 6000.3.13f1 batchmode로 실행했습니다(같은 소스·같은 .meta GUID). 기록: `DataValidation/verification/`.

| 범위 | 결과 |
|---|---|
| EditMode 테스트 `WordRoom.Data.Tests` | 22/22 통과 (합성 3, 손상 검출 16, 서비스·외부 실행 부재 2, 실데이터 1) |
| 실데이터 Python 기준값 비교 (에디터) | 27개 항목 모두 일치 |
| Windows x64 빌드 (Mono) `Builds/WordRoomDataCheck_Win64` | 빌드 성공, PATH에서 Python을 뺀 환경에서 `-wordroomVerify` 실행 → 27개 항목 일치, 종료 코드 0 |
| 확인 화면 | 빌드 창 모드 실행 화면 캡처(`datacheck*.png`): 한글 표시, 단어·뜻·관계 상태·근거·유사도 표시 확인 |
| Android | 미검증·미지원 (`UnsupportedPlatform`) |

| 측정 | 값 |
|---|---|
| 내보내기 전체 | 13.2초 (스냅샷 0.9 · 패키지 작성 3.9 · 독립 재검증 1.8 · Python 기준값 6.7) |
| Unity 최초 로딩 (SHA-256 검사 포함) | 에디터 2.75초, PC 빌드 2.89~3.08초 (E5 읽기+해시 약 1.8초) |
| 로딩 (해시 생략 `VerifyHashes=false`) | 0.72초 |
| 로딩 후 메모리 (PC 빌드) | 관리 힙 +187 MB, 프로세스 작업 집합 120 → 359 MB, 전용 메모리 194 → 446 MB |
| 승인 이웃 조회 | 0.03~0.05 µs/회 (기준 8단계 × 2,000단어, 10,000 범위 필터 포함) |
| 단어 쌍 상태 / 최고 근거 | 0.5 µs / 0.7~1.1 µs |
| fastText 유사도 / E5 유사도 | 0.69 µs / 1.78 µs |
| 유사도 최대 오차 | fastText 2.2e-15, E5 2.3e-15 (float64 합 대비) · 1.5e-7, 1.1e-7 (numpy float32 결과 대비) |

## 12. 다음 단계에 남은 일

> 아래 목록은 최초 데이터 이식 당시의 후속 과제입니다. 방 생성·좌표 난수·목표 근접도는 이후 `7200820`에서 이식·사용자 확인 완료했습니다. 자동 플레이어와 Android는 여전히 범위 밖이며 Python과 완전한 지도 일치는 보장하지 않습니다. 현재 다음 작업은 문 상호작용·실제 방 진입 연동입니다. 과거 제안인 해시 생략·메모리 매핑·SIMD 최적화를 이미 적용한 것으로 해석하지 않습니다.

- 방 생성 이식: 주변 배치 계획(백트래킹·기준 단계 완화), fastText 보충(약한 연결·제한 표기·전부 NO 쌍), 목표 근접도(E5 최대 뜻 쌍 + fastText 백분위), 좌표별 난수(`coordinate_rng`), 자동 플레이어 판 1/2. 이번 패키지에 필요한 데이터(승인 이웃·근거·점수·상태·전부 NO·제한 표기·적용 설정·기준 단계)는 모두 있습니다.
- Python과 같은 난수열(numpy `default_rng`/`random.Random`)을 쓸지, Unity 자체 난수로 바꾸고 결과 비교 방식을 바꿀지 결정이 필요합니다.
- 최고 근거의 E5 동점 처리: Python은 numpy float32 내적, Unity는 float64 합입니다. 현재 85,552건 모두 같지만, 점수가 완전히 같고 E5 차이가 1e-7 미만인 조합이 생기면 순서가 갈릴 수 있습니다.
- 최적화 제안(정확성 유지): 해시 검사를 개발 빌드에서만 하거나 백그라운드 사후 검사로 돌리기, E5 행렬을 메모리 매핑으로 읽어 관리 힙 줄이기, 대상 근접도 계산용 전체 행렬 곱의 SIMD/Burst 처리.
- Android: StreamingAssets → `persistentDataPath` 복사(또는 UnityWebRequest 스트리밍) `IPackageFileSource` 구현과 기기 검증.

