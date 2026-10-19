using System;
using System.Collections.Generic;
using System.Globalization;
using WordRoom.Data;

namespace WordRoom.Generation
{
    /// <summary>
    /// Generation settings of one experiment (port of the fields kw_semantic_engine.Config uses for generation).
    /// Defaults come from the data package's settings.json, i.e. what Python 1.1.0 applies after its loader
    /// (kw_app.load_saved_config → load_config); the Unity debug component overrides only the fields it shows.
    /// An experiment keeps its own copy (Clone) so later Inspector edits never change a running experiment.
    /// </summary>
    public sealed class GenerationConfig
    {
        public long Seed = 42;
        public int VocabularyLimit = 15000;
        public int MinWordLength = 1;
        public int MaxWordLength = 8;
        public bool AvoidAdjacentRepeat = true;
        public string RepeatScope = "nearby";        // 'nearby' (within RepeatRadius) or 'global'
        public int RepeatRadius = 3;
        public int SelectionTop = 4;
        public double Temperature = 0.08;
        public int RecentWindow = 64;
        public double Diversity = 0.6;
        public double NeighborWeight = 0.5;
        public double RelationThreshold = 0.8;
        public double RelationStep = 0.1;
        public double RelationFloor = 0.1;
        public int PlacementBudget = 2000;
        public bool FastTextSupplement = true;
        public double FastTextPreferredMin = 0.2;
        public bool TargetGuidance = true;
        public double GuidanceStrength = 0.0;
        public int GuidanceStart = 10;
        public int GuidanceRamp = 40;
        public double GuidanceMax = 0.03;
        public string GuidanceBasis = "unique";      // 'unique' (distinct rooms visited) or 'moves'
        public int MaxMoves = 300;

        /// <summary>Where each value came from (for logs and documentation).</summary>
        public string Source = "코드 기본값(Python Config 기본값과 같음)";

        public GenerationConfig Clone() => (GenerationConfig)MemberwiseClone();

        /// <summary>Effective settings exported with the data package (Python load_config result).</summary>
        public static GenerationConfig FromSettings(EffectiveSettings s)
        {
            var c = new GenerationConfig
            {
                Seed = s.GetInt("seed"),
                VocabularyLimit = s.GetInt("vocabulary_limit"),
                MinWordLength = s.GetInt("min_word_length"),
                MaxWordLength = s.GetInt("max_word_length"),
                AvoidAdjacentRepeat = s.GetBool("avoid_adjacent_repeat"),
                RepeatScope = s.GetString("repeat_scope"),
                RepeatRadius = s.GetInt("repeat_radius"),
                SelectionTop = s.GetInt("selection_top"),
                Temperature = s.GetDouble("temperature"),
                RecentWindow = s.GetInt("recent_window"),
                Diversity = s.GetDouble("diversity"),
                NeighborWeight = s.GetDouble("neighbor_weight"),
                RelationThreshold = s.GetDouble("relation_threshold"),
                RelationStep = s.GetDouble("relation_step"),
                RelationFloor = s.GetDouble("relation_floor"),
                PlacementBudget = s.GetInt("placement_budget"),
                FastTextSupplement = s.GetBool("fasttext_supplement"),
                FastTextPreferredMin = s.GetDouble("fasttext_preferred_min"),
                TargetGuidance = s.GetBool("target_guidance"),
                GuidanceStrength = s.GetDouble("guidance_strength"),
                GuidanceStart = s.GetInt("guidance_start"),
                GuidanceRamp = s.GetInt("guidance_ramp"),
                GuidanceMax = s.GetDouble("guidance_max"),
                GuidanceBasis = s.GetString("guidance_basis"),
                MaxMoves = s.GetInt("max_moves"),
                Source = "데이터 패키지 settings.json (Python 1.1.0 load_config 적용값)",
            };
            return c;
        }

        /// <summary>Same checks as Config.validate for the fields used here. Throws ArgumentException.</summary>
        public void Validate()
        {
            void Need(bool ok, string message)
            {
                if (!ok) throw new ArgumentException(message);
            }
            Need(VocabularyLimit >= 1 && MinWordLength >= 1 && MaxWordLength >= 1, "vocabulary_limit, min/max_word_length: 양의 정수여야 합니다.");
            Need(MaxWordLength >= MinWordLength, "최대 글자 수는 최소 글자 수 이상이어야 합니다.");
            Need(RepeatScope == "global" || RepeatScope == "nearby", "중복 금지 범위는 지도 전체 또는 주변 거리여야 합니다.");
            Need(RepeatRadius >= 1 && SelectionTop >= 1 && RecentWindow >= 1 && MaxMoves >= 1, "repeat_radius, selection_top, recent_window, max_moves: 양의 정수여야 합니다.");
            Need(Temperature >= 0 && Temperature <= 1, "선택 온도는 0~1 사이여야 합니다.");
            Need(Diversity >= 0 && Diversity <= 1 && NeighborWeight >= 0 && NeighborWeight <= 1, "diversity, neighbor_weight: 0~1 사이여야 합니다.");
            foreach (var v in new[] { RelationThreshold, RelationStep, RelationFloor })
                Need(v > 0 && v <= 1 && !double.IsNaN(v), "relation_threshold·relation_step·relation_floor: 0보다 크고 1 이하여야 합니다.");
            Need(RelationFloor <= RelationThreshold, "최저 기준은 시작 기준보다 높을 수 없습니다.");
            Need(PlacementBudget >= 1, "placement_budget: 양의 정수여야 합니다.");
            Need(FastTextPreferredMin >= 0 && FastTextPreferredMin <= 1, "fasttext_preferred_min: 0~1 사이여야 합니다.");
            Need(GuidanceStrength >= 0 && GuidanceStrength <= 1 && GuidanceMax >= 0 && GuidanceMax <= 1, "guidance_strength, guidance_max: 0~1 사이여야 합니다.");
            Need(GuidanceMax >= GuidanceStrength, "guidance_max는 guidance_strength 이상이어야 합니다.");
            Need(GuidanceStart >= 0 && GuidanceRamp >= 0, "guidance_start, guidance_ramp: 0 이상의 정수여야 합니다.");
            Need(GuidanceBasis == "unique" || GuidanceBasis == "moves", "guidance_basis는 'unique' 또는 'moves'여야 합니다.");
        }

        /// <summary>
        /// threshold_ladder: start, start-step, ... down to the floor in decimal arithmetic, floor always last.
        /// When start/step/floor equal the exported effective settings, the exported float64 bits are used instead.
        /// </summary>
        public double[] ThresholdLadder(WordRoomDataset data)
        {
            var s = data.Settings;
            if (RelationThreshold == s.GetDouble("relation_threshold") && RelationStep == s.GetDouble("relation_step") &&
                RelationFloor == s.GetDouble("relation_floor"))
            {
                var exported = new double[data.ThresholdLadder.Count];
                for (var i = 0; i < exported.Length; i++) exported[i] = data.ThresholdLadder[i];
                return exported;
            }
            decimal Dec(double x) => decimal.Parse(x.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal start = Dec(RelationThreshold), step = Dec(RelationStep), floor = Dec(RelationFloor);
            var list = new List<decimal>();
            for (var t = start; t >= floor; t -= step) list.Add(t);
            if (list.Count == 0 || list[list.Count - 1] != floor) list.Add(floor);
            return list.ConvertAll(t => double.Parse(t.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).ToArray();
        }

        public override string ToString() =>
            $"seed {Seed} · 어휘 상위 {VocabularyLimit:N0} ({MinWordLength}~{MaxWordLength}글자) · 관계 기준 {RelationThreshold:0.##}→{RelationFloor:0.##} (폭 {RelationStep:0.##}) · " +
            $"탐색 한도 {PlacementBudget} · fastText 보충 {(FastTextSupplement ? "켬" : "끔")} (선호 하한 {FastTextPreferredMin:0.##}) · " +
            $"목표 유도 {(TargetGuidance ? $"켬 ({GuidanceBasis}, {GuidanceStart}부터 {GuidanceRamp}에 걸쳐 {GuidanceStrength:0.###}→{GuidanceMax:0.###})" : "끔")} · " +
            $"중복 금지 {(AvoidAdjacentRepeat ? (RepeatScope == "global" ? "지도 전체" : $"거리 {RepeatRadius}") : "끔")} · 최대 이동 {MaxMoves}";
    }
}
