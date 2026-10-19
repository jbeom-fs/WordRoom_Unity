using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using WordRoom.Data;

namespace WordRoom.UnityLayer
{
    /// <summary>
    /// Development-only data check screen (IMGUI, not the game UI): load state and versions, word and sense lookup,
    /// approved neighbors per threshold and vocabulary range, word-pair state with evidence, fastText and E5
    /// similarity, restriction flags. Everything shown comes from the loaded package.
    /// </summary>
    [RequireComponent(typeof(WordRoomDataService))]
    public sealed class WordRoomDataInspector : MonoBehaviour
    {
        WordRoomDataService service;
        Font font;
        GUIStyle wrap, bold, box;
        Vector2 scrollLeft, scrollRight, scrollNeighbors;
        string inputA = "", inputB = "";
        string limitText = "", minText = "", maxText = "";
        int thresholdIndex;
        int rankA = -1, rankB = -1, senseA = -1, senseB = -1;
        string messageA = "", messageB = "", scopeMessage = "";
        VocabularyScope scope;
        bool showSettings;
        readonly List<int> neighbors = new List<int>();
        string memoryText = "";

        void Awake() => service = GetComponent<WordRoomDataService>();

        void OnEnable()
        {
            if (service == null) service = GetComponent<WordRoomDataService>();
            service.StateChanged += OnStateChanged;
        }

        void OnDisable() => service.StateChanged -= OnStateChanged;

        void OnStateChanged(WordRoomDataService s)
        {
            if (s.State != DataLoadState.Ready) return;
            var d = s.Dataset;
            scope = d.CreateDefaultScope();
            limitText = scope.Limit.ToString(CultureInfo.InvariantCulture);
            minText = scope.MinLength.ToString(CultureInfo.InvariantCulture);
            maxText = scope.MaxLength.ToString(CultureInfo.InvariantCulture);
            memoryText = $"관리 힙 {GC.GetTotalMemory(false) / 1048576.0:F0} MB";
        }

        void EnsureStyles()
        {
            if (font == null)
                font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Arial" }, 15);
            GUI.skin.font = font;
            if (wrap != null) return;
            wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
            bold = new GUIStyle(wrap) { fontStyle = FontStyle.Bold };
            box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
        }

        void OnGUI()
        {
            EnsureStyles();
            GUILayout.BeginArea(new Rect(8, 8, Screen.width - 16, Screen.height - 16));
            GUILayout.Label("WordRoom 데이터 확인 (개발용 화면 · 게임 UI 아님)", bold);
            if (!service.TryGetDataset(out var d))
            {
                DrawNotReady();
                GUILayout.EndArea();
                return;
            }
            if (scope == null) OnStateChanged(service);
            DrawHeader(d);
            GUILayout.BeginHorizontal();
            var half = (Screen.width - 40) / 2f;
            GUILayout.BeginVertical(GUILayout.Width(half));
            scrollLeft = GUILayout.BeginScrollView(scrollLeft);
            DrawControls(d);
            DrawWord(d, "단어 A", ref inputA, ref rankA, ref senseA, ref messageA);
            DrawNeighbors(d);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(half));
            scrollRight = GUILayout.BeginScrollView(scrollRight);
            DrawWord(d, "단어 B", ref inputB, ref rankB, ref senseB, ref messageB);
            DrawPair(d);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawNotReady()
        {
            switch (service.State)
            {
                case DataLoadState.Failed:
                    GUILayout.Label("로딩 실패", bold);
                    GUILayout.Label(service.Error?.Message ?? "(원인 없음)", wrap);
                    GUILayout.Label($"패키지 위치: {service.PackagePath}", wrap);
                    GUILayout.Label("Python에서 Tools/DataExport/export_unity_data.py export를 실행해 패키지를 다시 만드세요.", wrap);
                    break;
                case DataLoadState.Loading:
                    GUILayout.Label($"로딩 중… {service.Stage} {service.Progress * 100:F0}%", wrap);
                    GUILayout.Label("로딩이 끝나기 전에는 조회하지 않습니다.", wrap);
                    break;
                default:
                    GUILayout.Label("로딩 시작 전", wrap);
                    if (GUILayout.Button("데이터 로딩", GUILayout.Width(160))) _ = service.LoadAsync();
                    break;
            }
        }

        void DrawHeader(WordRoomDataset d)
        {
            var id = d.Identity;
            var c = d.Manifest.counts;
            GUILayout.Label($"상태: 완료 · 패키지 {d.PackageId.Substring(0, 12)} (스키마 {d.Manifest.schemaVersion}, 내보낸 시각 {d.Manifest.createdAt}) · {d.LoadReport.TotalSeconds:F2}초 · {memoryText}", wrap);
            GUILayout.Label($"어휘 {id.vocabularyName} · 표기 {d.WordCount:N0} · 뜻 {d.SenseCount:N0} · E5 {d.E5Dimensions}차원 · fastText {d.FastTextDimensions}차원 (유효 {c.fasttextValidWords:N0}) · " +
                            $"관계 레코드 {d.Relations.RecordCount:N0} · 제한 표기 {c.restrictedInVocabulary}", wrap);
            GUILayout.Label($"Python {id.appVersion} · 관계 판정 {id.relationVersion.Substring(0, 12)} ({id.relationRule}) · 스냅샷 {id.relationSnapshotId} · " +
                            $"판정 모델 {id.judgeModel} · E5 {id.embeddingModel} · fastText {id.fasttextVersion} · 제한 목록 {id.restrictedListVersion}", wrap);
            GUILayout.Label($"경로: {service.PackagePath}", wrap);
            showSettings = GUILayout.Toggle(showSettings, "적용 설정·원시 설정 차이 보기");
            if (!showSettings) return;
            var sb = new StringBuilder();
            sb.AppendLine($"로더: {d.Settings.Document.loader}");
            foreach (var e in d.Settings.Differences())
                sb.AppendLine(e.change == "changed" ? $"변경 {e.key}: 원시 {e.rawJson} → 적용 {e.valueJson}" : $"기본값 {e.key} = {e.valueJson}");
            foreach (var x in d.Settings.DroppedRawKeys) sb.AppendLine($"무시 {x.key} = {x.rawJson} ({x.reason})");
            foreach (var n in d.Settings.Notices) sb.AppendLine($"안내: {n}");
            GUILayout.Label(sb.ToString(), box);
        }

        void DrawControls(WordRoomDataset d)
        {
            GUILayout.Label("관계 기준", bold);
            GUILayout.BeginHorizontal();
            for (var i = 0; i < d.ThresholdLadder.Count; i++)
            {
                var on = GUILayout.Toggle(i == thresholdIndex, d.ThresholdLadder[i].ToString("0.0#", CultureInfo.InvariantCulture), GUI.skin.button, GUILayout.Width(52));
                if (on) thresholdIndex = i;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("어휘 범위 (빈도 상위 N개 · 글자 수)", bold);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("15,000", GUILayout.Width(72))) limitText = "15000";
            if (GUILayout.Button("10,000", GUILayout.Width(72))) limitText = "10000";
            GUILayout.Label("상위", GUILayout.Width(32));
            limitText = GUILayout.TextField(limitText, GUILayout.Width(70));
            GUILayout.Label("글자", GUILayout.Width(32));
            minText = GUILayout.TextField(minText, GUILayout.Width(30));
            GUILayout.Label("~", GUILayout.Width(14));
            maxText = GUILayout.TextField(maxText, GUILayout.Width(30));
            if (GUILayout.Button("적용", GUILayout.Width(60)))
            {
                try
                {
                    scope = d.CreateScope(int.Parse(limitText, CultureInfo.InvariantCulture), int.Parse(minText, CultureInfo.InvariantCulture),
                        int.Parse(maxText, CultureInfo.InvariantCulture));
                    scopeMessage = "";
                }
                catch (Exception e)
                {
                    scopeMessage = e.Message;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"현재 범위: {scope}" + (scopeMessage.Length > 0 ? $" · 오류: {scopeMessage}" : ""), wrap);
        }

        void DrawWord(WordRoomDataset d, string title, ref string input, ref int rank, ref int sense, ref string message)
        {
            GUILayout.Space(6);
            GUILayout.Label(title, bold);
            GUILayout.BeginHorizontal();
            input = GUILayout.TextField(input, GUILayout.Width(220));
            if (GUILayout.Button("조회", GUILayout.Width(70))) Resolve(d, input, out rank, out sense, out message);
            GUILayout.EndHorizontal();
            if (message.Length > 0) GUILayout.Label(message, wrap);
            if (rank < 0) return;
            var restricted = d.GetRestrictedEntry(rank);
            GUILayout.Label($"{d.GetWord(rank)} · 빈도 순위 {rank + 1:N0} · {d.GetWordLength(rank)}글자 · 현재 범위 {(scope.Contains(rank) ? "안" : "밖")} · " +
                            $"fastText {(d.HasFastText(rank) ? "있음" : "없음")} · 제한 표기 {(restricted != null ? $"예 ({restricted.selection}, {string.Join("/", restricted.rules)})" : "아니오")}", wrap);
            foreach (var row in d.SenseRowsOf(rank))
            {
                var s = d.GetSense(row);
                var label = $"[{s.SenseId}] {s.Pos}{(s.Category.Length > 0 ? " · " + s.Category : "")} · {s.Definition}";
                if (GUILayout.Toggle(sense == row, label, GUI.skin.toggle)) sense = row;
            }
        }

        static void Resolve(WordRoomDataset d, string input, out int rank, out int sense, out string message)
        {
            if (d.TryGetWordRank(input, out rank))
            {
                sense = d.SenseRowsOf(rank)[0];
                message = "";
                return;
            }
            rank = -1;
            sense = -1;
            message = $"어휘에 없는 표기: {WordNormalizer.Normalize(input)}";
        }

        /// <summary>
        /// Optional command-line check of this screen in a player: -wordroomInspectA/-wordroomInspectB fill the two word
        /// fields as if typed, -wordroomScreenshot saves the screen and quits. Without these flags nothing happens.
        /// </summary>
        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            var a = Arg("-wordroomInspectA");
            var b = Arg("-wordroomInspectB");
            var shot = Arg("-wordroomScreenshot");
            if (a == null && b == null && shot == null) yield break;
            while (service.State == DataLoadState.NotStarted || service.State == DataLoadState.Loading) yield return null;
            if (service.TryGetDataset(out var d))
            {
                if (a != null)
                {
                    inputA = a;
                    Resolve(d, a, out rankA, out senseA, out messageA);
                }
                if (b != null)
                {
                    inputB = b;
                    Resolve(d, b, out rankB, out senseB, out messageB);
                }
            }
            if (shot == null) yield break;
            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(shot);
            yield return new WaitForSeconds(1.5f);
            Application.Quit(0);
        }

        void DrawNeighbors(WordRoomDataset d)
        {
            if (rankA < 0) return;
            var t = d.ThresholdLadder[thresholdIndex];
            neighbors.Clear();
            d.Relations.GetApprovedNeighbors(rankA, t, neighbors, scope);
            GUILayout.Space(6);
            GUILayout.Label($"{d.GetWord(rankA)}의 승인 이웃 (기준 {t:0.0#}, 현재 범위) {neighbors.Count:N0}개 · 전체 어휘 {d.Relations.CountApprovedNeighbors(rankA, t):N0}개", bold);
            scrollNeighbors = GUILayout.BeginScrollView(scrollNeighbors, GUILayout.Height(260));
            foreach (var n in neighbors)
            {
                d.Relations.TryGetBestEvidence(rankA, n, t, out var e);
                if (GUILayout.Button($"{d.GetWord(n)} · YES {e.Score:0.000} · [{d.GetSenseId(e.SenseRow)}]–[{d.GetSenseId(e.NeighborSenseRow)}]", GUI.skin.label))
                {
                    inputB = d.GetWord(n);
                    rankB = n;
                    senseB = e.NeighborSenseRow;
                    senseA = e.SenseRow;
                    messageB = "";
                }
            }
            GUILayout.EndScrollView();
        }

        void DrawPair(WordRoomDataset d)
        {
            if (rankA < 0 || rankB < 0) return;
            var t = d.ThresholdLadder[thresholdIndex];
            var rel = d.Relations;
            GUILayout.Space(8);
            GUILayout.Label($"{d.GetWord(rankA)} – {d.GetWord(rankB)} (기준 {t:0.0#})", bold);
            var state = rel.GetWordPairState(rankA, rankB, t);
            GUILayout.Label($"관계 상태: {WordPairStateCodes.Korean(state)}", wrap);
            if (rel.TryGetBestEvidence(rankA, rankB, t, out var best))
                GUILayout.Label($"최고 근거: [{d.GetSenseId(best.SenseRow)}] – [{d.GetSenseId(best.NeighborSenseRow)}] · YES 점수 {best.Score.ToString("R", CultureInfo.InvariantCulture)} " +
                                "(판정 모델 YES 점수, 유사도·확률 아님)", wrap);
            var ft = d.TryFastTextSimilarity(rankA, rankB, out var sim) ? sim.ToString("0.0000", CultureInfo.InvariantCulture) : "벡터 없음";
            var restrictedNote = d.IsRestricted(rankA) || d.IsRestricted(rankB) ? " · 제한 표기 포함(fastText 보충 시 약한 연결)" : "";
            GUILayout.Label($"fastText 표기 유사도: {ft}{restrictedNote}", wrap);
            if (senseA >= 0 && senseB >= 0)
                GUILayout.Label($"선택한 뜻 E5 유사도: [{d.GetSenseId(senseA)}] · [{d.GetSenseId(senseB)}] = {d.E5Similarity(senseA, senseB):0.0000}", wrap);
            GUILayout.Label("뜻 조합별 판정", bold);
            foreach (var c in rel.GetCombinations(rankA, rankB, t))
            {
                var line = $"[{d.GetSenseId(c.SenseRow)}]×[{d.GetSenseId(c.NeighborSenseRow)}] {WordPairStateCodes.Korean(c.State)}";
                if (c.RecordIndex >= 0)
                {
                    var j = rel.GetRecord(c.RecordIndex);
                    line += $" · {j.Status} · 저장 점수 {j.Score.ToString("R", CultureInfo.InvariantCulture)} · " +
                            $"[{d.GetSenseId(j.SenseRowA)}]→[{d.GetSenseId(j.SenseRowB)}] {j.ForwardAnswer ?? "없음"} {Score(j.ForwardScore)} / " +
                            $"역순 {j.BackwardAnswer ?? "없음"} {Score(j.BackwardScore)} · {j.Method} · {j.CreatedAt}";
                }
                GUILayout.Label(line, wrap);
            }
        }

        static string Score(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0000", CultureInfo.InvariantCulture);
    }
}
