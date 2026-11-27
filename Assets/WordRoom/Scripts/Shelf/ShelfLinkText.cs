using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WordRoom.Shelf
{
    /// <summary>
    /// A range of a definition: plain text (Target null) or a link word with the headword it leads to. Start and
    /// Length are the range in the original definition, so particles, spaces and punctuation stay where they were.
    /// </summary>
    public readonly struct ShelfTextPart
    {
        public readonly string Text;
        public readonly int Start;
        public readonly int Length;
        public readonly string Target;

        public ShelfTextPart(string text, int start, string target)
        {
            Text = text;
            Start = start;
            Length = text.Length;
            Target = target;
        }

        public bool IsLink => Target != null;
    }

    /// <summary>
    /// Display text and link ranges for a definition. The game uses the validated database for ranges.
    /// FindCandidates provides the rule-based extraction used for comparison and regression checks.
    /// An eojeol (word unit) is a maximal run of letters and digits; spaces, punctuation and symbols (· % ㎢ …) are
    /// its boundaries, so no candidate ever joins the end of one eojeol to the start of the next ("바다의 경계" never
    /// gives "의경"). An eojeol gives at most one candidate, always a headword at its start; the candidate's Start and
    /// Length are the clicked range in the definition, its Text exactly the letters there ("영화용 음화" → [영화]용), so
    /// the rest of the eojeol, the spaces and the punctuation stay plain text as written. In order:
    /// 0. a confirmed per-sense split (SenseSplits: "68510|영화용" → 영화, the suffix -용 only where its context was
    ///    checked) is taken first; a confirmed verb form (모자라는) gives none; a counter after a quantity (개의 right
    ///    after 한, 두, 몇, 여러, a number…, only spaces between: "두 개의" is 개 + 의) gives the counter, which is too
    ///    short to be a door; a confirmed eojeol split (WordSplits: 결과의 → 결과) is taken;
    /// 0b. when 와/과 (optionally followed by 의, 는 or 도) can be part of a registered noun, compare that noun
    ///    with a registered noun plus the fitting particle. Choose the particle reading when the local construction
    ///    supports it ("그 말과 다른", "강과 시내를 아울러"); otherwise use the usual whole-word and ending rules;
    /// 1. else the whole eojeol, when it is a registered headword ("바다", the noun "에도", "비공개" — a prefix is never
    ///    cut off, so 비공개 never links 공개);
    /// 2. else the headword before one allowed ending — a particle, a particle combination or a form of the copula
    ///    이다 / 이라… ("바다의" → 바다 + 의, "해안이다" → 해안 + 이다, "공자라는" → 공자 + 라는) — but only when exactly
    ///    one ending leaves a registered headword. When two or more do ("결과의": 결 + 과의 or 결과 + 의; "길이라는": 길 +
    ///    이라는 or 길이 + 라는) and no confirmed exception above decides, the eojeol stays plain text.
    /// Splitting looks only at registered headwords (isEntry); the door conditions (vocabulary, length, restricted
    /// list, definition: isDoor) are applied afterwards to the one headword decided, and when it fails the whole eojeol
    /// stays plain text: a split is never changed to pass them. Nothing else is cut: no suffixes or prefixes outside the
    /// exceptions, no stacked endings, no inflection, so a run without its spaces ("바다의경계는") or one with digits or
    /// Latin ("43배이다") stays plain text. Own-room links, repeats within a definition and per-challenge link settings
    /// are handled by ShelfLinkGraph on this result.
    /// </summary>
    public static class ShelfLinkText
    {
        /// <summary>Particles and particle combinations that follow a noun.</summary>
        static readonly string[] ParticleEndings =
        {
            "은", "는", "이", "가", "을", "를", "에", "의", "와", "과", "로", "으로", "도", "만",
            "에서", "에게", "에게서", "께", "께서", "한테", "까지", "부터", "처럼", "보다", "마저", "조차", "만큼", "밖에",
            "로서", "으로서", "로써", "으로써", "이나", "이란", "이라도",
            "에는", "에서는", "에게는", "으로는", "로는", "와는", "과는", "까지는", "부터는", "보다는",
            "에도", "에서도", "에게도", "으로도", "로도", "와도", "과도", "까지도",
            "와의", "과의", "에서의", "으로의", "로의", "에의", "까지의", "부터의", "만의",
            "로서의", "으로서의", "로서는", "으로서는", "에게서는",
            "에서부터", "으로부터", "로부터",
        };

        /// <summary>
        /// Forms of the copula 이다 (and quoting/naming 이라…) after a noun: after a consonant with 이 (전신이라고도), after
        /// a vowel without it (공자라는). Two-letter-or-longer forms only; one-letter vowel forms (다, 며, 고, 란) would cut
        /// too many ordinary words. A cut is only taken when the part before it is a valid dictionary word.
        /// </summary>
        static readonly string[] CopulaEndings =
        {
            "이다", "이며", "이고", "이요", "이었다", "이었고", "이었으며", "이므로", "이지만", "이면", "이어서", "이어야",
            "입니다", "였다", "였으며",
            "이라", "이라는", "이라고", "이라고도", "이라고는", "이라며", "이라면", "이라서", "이라야",
            "라는", "라고", "라고도", "라고는", "라며", "라면", "라서",
        };

        /// <summary>All endings, longest first, so the whole ending is tried before its parts (이라고도 before 라고도, 이었다 before 이다, 으로 before 로).</summary>
        static readonly string[] Endings = BuildEndings();

        static string[] BuildEndings()
        {
            var set = new HashSet<string>(ParticleEndings);
            set.UnionWith(CopulaEndings);
            var list = new List<string>(set);
            list.Sort((a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b));
            return list.ToArray();
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

        // ---- display text ----------------------------------------------------------------------------------------

        /// <summary>
        /// The definition as shown and searched: every parenthesized span — (…) or （…）, nesting allowed — that
        /// contains at least one Han character is removed with its brackets; other spans stay ("화가(1746~1828)").
        /// Nothing is put in its place, so a following particle joins the word again ("동판화(銅版畫)에도" →
        /// "동판화에도", "타자(他者)와의" → "타자와의"); a span between two spaces leaves one space. An unclosed bracket
        /// stays as written. Candidate positions are offsets in this string.
        /// </summary>
        public static string DisplayDefinition(string raw)
        {
            if (string.IsNullOrEmpty(raw) || (raw.IndexOf('(') < 0 && raw.IndexOf('（') < 0)) return raw;
            var sb = new StringBuilder(raw.Length);
            var i = 0;
            while (i < raw.Length)
            {
                if (IsOpen(raw[i]))
                {
                    var close = MatchingClose(raw, i);
                    if (close > i && HasHan(raw, i + 1, close))
                    {
                        i = close + 1;
                        var spaceBefore = sb.Length == 0 || char.IsWhiteSpace(sb[sb.Length - 1]);
                        if (spaceBefore && i < raw.Length && char.IsWhiteSpace(raw[i])) i++;   // no doubled space
                        continue;
                    }
                }
                sb.Append(raw[i]);
                i++;
            }
            return sb.ToString();
        }

        static bool IsOpen(char c) => c == '(' || c == '（';

        static bool IsClose(char c) => c == ')' || c == '）';

        /// <summary>Index of the bracket closing the one at `open`, or -1 when it is never closed.</summary>
        static int MatchingClose(string s, int open)
        {
            var depth = 0;
            for (var i = open; i < s.Length; i++)
            {
                if (IsOpen(s[i])) depth++;
                else if (IsClose(s[i]) && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>A Han character (CJK unified ideographs, extension A, compatibility, or a supplementary-plane ideograph) in [from, to).</summary>
        static bool HasHan(string s, int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var c = s[i];
                if ((c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || (c >= '豈' && c <= '﫿')) return true;
                if (c >= '\uD840' && c <= '\uD87F' && i + 1 < to && char.IsLowSurrogate(s[i + 1])) return true;   // U+20000–U+2FFFF
            }
            return false;
        }

        /// <summary>
        /// Candidate link words of `definition` in reading order, as link parts: Start/Length the clicked range in
        /// `definition`, Text the letters there, Target the headword (equal to Text: a headword is always the start of
        /// its eojeol).
        /// </summary>
        /// <param name="isEntry">
        /// True when the surface is a registered headword (exact spelling), with no door conditions — used to decide
        /// where an eojeol splits.
        /// </param>
        /// <param name="isDoor">True when that headword may become a door (vocabulary, length, restricted list, definition).</param>
        /// <param name="senseId">SenseId of the room whose definition this is (for the per-room split exceptions).</param>
        public static List<ShelfTextPart> FindCandidates(string definition, string senseId, Func<string, bool> isEntry, Func<string, bool> isDoor) =>
            FindCandidates(definition, senseId, isEntry, isDoor, null);

        internal static List<ShelfTextPart> FindCandidates(string definition, string senseId, Func<string, bool> isEntry,
            Func<string, bool> isDoor, Func<string, string, bool> hasParallelUse)
        {
            var found = new List<ShelfTextPart>();
            if (string.IsNullOrEmpty(definition)) return found;
            var eojeols = Eojeols(definition);
            for (var k = 0; k < eojeols.Count; k++)
            {
                var e = eojeols[k];
                // Keep counter behavior across whitespace. Punctuation and line breaks stop forward context.
                var previous = k > 0 && eojeols[k - 1].SpacesAfter ? eojeols[k - 1].Text : null;
                var next = e.ContextAfter ? eojeols[k + 1].Text : null;
                var following = next != null && eojeols[k + 1].ContextAfter ? eojeols[k + 2].Text : null;
                var word = Headword(e.Text, previous, next, following, senseId, isEntry, hasParallelUse);
                // The headword is the eojeol's start: the clicked range is [start, start + its length) of the definition.
                if (word != null && isDoor(word)) found.Add(new ShelfTextPart(definition.Substring(e.Start, word.Length), e.Start, word));
            }
            return found;
        }

        /// <summary>One eojeol and whether the gap to the next one allows context.</summary>
        readonly struct Eojeol
        {
            public readonly int Start;
            public readonly string Text;
            public readonly bool SpacesAfter;
            public readonly bool ContextAfter;

            public Eojeol(int start, string text, bool spacesAfter, bool contextAfter)
            {
                Start = start;
                Text = text;
                SpacesAfter = spacesAfter;
                ContextAfter = contextAfter;
            }
        }

        /// <summary>The eojeols (maximal runs of letters and digits) of `text` in order.</summary>
        static List<Eojeol> Eojeols(string text)
        {
            var list = new List<(int Start, int End)>();
            var i = 0;
            while (i < text.Length)
            {
                if (!IsWordChar(text[i]))
                {
                    i++;
                    continue;
                }
                var start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                list.Add((start, i));
            }
            var result = new List<Eojeol>(list.Count);
            for (var k = 0; k < list.Count; k++)
            {
                var spaces = k + 1 < list.Count;
                var lineBreak = false;
                for (var j = list[k].End; spaces && j < list[k + 1].Start; j++)
                {
                    var c = text[j];
                    spaces = char.IsWhiteSpace(c);
                    lineBreak |= c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029';
                }
                result.Add(new Eojeol(list[k].Start, text.Substring(list[k].Start, list[k].End - list[k].Start), spaces, spaces && !lineBreak));
            }
            return result;
        }

        // ---- 와/과 context ------------------------------------------------------------------------------------------

        static readonly string[] ComparisonAndCompanionStems =
        {
            "같", "다르", "다른", "달리", "비슷", "닮", "흡사",
            "함께", "더불어", "벗하", "싸우", "맞서", "견주", "사귀",
            "반응하", "결합하", "대립하", "관계되", "관계있", "관련되",
        };

        static readonly string[] RelationNouns = { "관계", "대응", "융화", "조화", "차이", "사이", "연관", "대조" };
        static readonly string[] NounContextEndings = { "이", "가", "은", "는", "을", "를", "의", "에", "에서", "로", "으로" };

        // Independent uses of two registered nouns in parallel: A(이)나 B, or the paired modifiers A적·B적(인).
        static readonly Regex AlternativeNouns = new Regex(
            @"(?<![\p{L}\p{Nd}])([가-힣]+?)(이나|나)[ \t]+([가-힣]+)(?![\p{L}\p{Nd}])", RegexOptions.CultureInvariant);
        static readonly Regex ParallelModifiers = new Regex(
            @"(?<![\p{L}\p{Nd}])([가-힣]+)적[ \t]*[·ㆍ][ \t]*([가-힣]+)적(?:인|으로)?(?![\p{L}\p{Nd}])",
            RegexOptions.CultureInvariant);

        internal static void AddParallelUses(string definition, Func<string, bool> isEntry, HashSet<(string, string)> uses)
        {
            if (definition.IndexOf("나 ", StringComparison.Ordinal) >= 0 || definition.IndexOf("나\t", StringComparison.Ordinal) >= 0)
            {
                foreach (Match match in AlternativeNouns.Matches(definition))
                {
                    var left = match.Groups[1].Value;
                    var right = match.Groups[3].Value;
                    if (FitsFinalConsonant(left[left.Length - 1], match.Groups[2].Length == 2) && isEntry(left) && isEntry(right))
                        uses.Add((left, right));
                }
            }

            if (definition.IndexOf('·') < 0 && definition.IndexOf('ㆍ') < 0) return;
            foreach (Match match in ParallelModifiers.Matches(definition))
            {
                var left = match.Groups[1].Value;
                var right = match.Groups[2].Value;
                if (isEntry(left) && isEntry(right)) uses.Add((left, right));
            }
        }

        static bool FitsFinalConsonant(char syllable, bool needsConsonant) =>
            syllable < '가' || syllable > '힣' || ((syllable - '가') % 28 != 0) == needsConsonant;

        // Index of the conjunction in X과 or X과의/는/도 (likewise 와), leaving at least one letter before it.
        static int ConjunctionIndex(string eojeol)
        {
            var index = eojeol.Length - 1;
            if (eojeol[index] == '의' || eojeol[index] == '는' || eojeol[index] == '도') index--;
            return index > 0 && (eojeol[index] == '와' || eojeol[index] == '과') ? index : -1;
        }

        static bool IsNounPlusConjunction(string eojeol, Func<string, bool> isEntry)
        {
            if (eojeol == null) return false;
            var index = ConjunctionIndex(eojeol);
            return index > 0 && index == eojeol.Length - 1
                && FitsFinalConsonant(eojeol[index - 1], eojeol[index] == '과')
                && isEntry(eojeol.Substring(0, index));
        }

        static bool NounInContext(string eojeol, string noun)
        {
            if (!eojeol.StartsWith(noun, StringComparison.Ordinal)) return false;
            if (eojeol.Length == noun.Length) return true;
            var ending = eojeol.Substring(noun.Length);
            return Array.IndexOf(NounContextEndings, ending) >= 0;
        }

        static bool IsRelationNoun(string eojeol)
        {
            if (eojeol == null) return false;
            foreach (var noun in RelationNouns)
                if (NounInContext(eojeol, noun)) return true;
            return false;
        }

        static string BeforeObjectParticle(string eojeol)
        {
            if (eojeol.Length < 2) return null;
            var ending = eojeol[eojeol.Length - 1];
            if (ending != '을' && ending != '를') return null;
            return FitsFinalConsonant(eojeol[eojeol.Length - 2], ending == '을')
                ? eojeol.Substring(0, eojeol.Length - 1) : null;
        }

        static string BeforeMeansParticle(string eojeol)
        {
            if (eojeol.Length < 2 || eojeol[eojeol.Length - 1] != '로') return null;
            var length = eojeol.EndsWith("으로", StringComparison.Ordinal) ? eojeol.Length - 2 : eojeol.Length - 1;
            if (length == 0) return null;
            var syllable = eojeol[length - 1];
            if (syllable < '가' || syllable > '힣') return null;
            var final = (syllable - '가') % 28;
            return (final != 0 && final != 8) == (length == eojeol.Length - 2) ? eojeol.Substring(0, length) : null;
        }

        static bool HasConjunctionContext(string stem, char followingParticle, string previous, string next, string following,
            Func<string, bool> isEntry, Func<string, string, bool> hasParallelUse)
        {
            if (next == null) return false;
            if (followingParticle == '의')
                return IsRelationNoun(next) || (IsRelationNoun(following) &&
                    (next.EndsWith("적", StringComparison.Ordinal) || next.EndsWith("적인", StringComparison.Ordinal)));

            foreach (var predicate in ComparisonAndCompanionStems)
                if (next.StartsWith(predicate, StringComparison.Ordinal)) return true;
            if (followingParticle != '\0') return false;
            if (NounInContext(next, stem) || (next == "직접적인" && IsRelationNoun(following))) return true;

            var objectNoun = BeforeObjectParticle(next);
            if (objectNoun != null && (following == "아울러" || following == "통틀어"))
            {
                // In an already coordinated phrase, 공과 can instead modify the second item: 계열과 공과 계열을 아울러.
                return !IsNounPlusConjunction(previous, isEntry);
            }

            // An overt shared case marker and an independently attested parallel use of the same two nouns.
            var second = objectNoun ?? BeforeMeansParticle(next);
            return second != null && hasParallelUse != null && hasParallelUse(stem, second);
        }

        // ---- quantity context ------------------------------------------------------------------------------------------

        /// <summary>
        /// Counter forms read as counter + particle right after a quantity eojeol (only spaces between), checked before the
        /// whole eojeol is taken as a headword: "두 개의" is 개 + 의, never the noun 개의(介意). The counter (one letter) is
        /// no door, so the eojeol stays plain text as written; no candidate ever spans the space. A 개의 elsewhere is judged
        /// as usual (per-sense splits list the confirmed 개(dog) + 의 uses).
        /// </summary>
        static readonly Dictionary<string, string> CounterForms = new Dictionary<string, string>
        {
            { "개의", "개" },
        };

        /// <summary>Quantity words before a counter: the native numerals and their usual groupings, 몇 and 여러.</summary>
        static readonly HashSet<string> QuantityWords = new HashSet<string>
        {
            "한", "두", "세", "네", "몇", "여러",
            "다섯", "여섯", "일곱", "여덟", "아홉", "열", "스무",
            "한두", "두세", "서너", "두어", "대여섯", "예닐곱", "수십", "수백", "수천", "십여", "몇십", "몇백",
        };

        static readonly string[] Units = { "한", "두", "세", "네", "다섯", "여섯", "일곱", "여덟", "아홉" };
        static readonly string[] Tens = { "열", "스물", "서른", "마흔", "쉰", "예순", "일흔", "여든", "아흔" };

        /// <summary>
        /// A quantity before a counter: a listed quantity word, a native tens numeral with or without its unit (열세,
        /// 마흔다섯), or a number written in digits, optionally with 여 ("10", "40여"; "7,100여" ends in "100여").
        /// </summary>
        static bool IsQuantity(string eojeol)
        {
            if (string.IsNullOrEmpty(eojeol)) return false;
            if (QuantityWords.Contains(eojeol)) return true;
            var digits = eojeol.EndsWith("여", StringComparison.Ordinal) ? eojeol.Length - 1 : eojeol.Length;
            if (digits > 0)
            {
                var all = true;
                for (var k = 0; k < digits && all; k++) all = eojeol[k] >= '0' && eojeol[k] <= '9';
                if (all) return true;
            }
            foreach (var t in Tens)
            {
                if (!eojeol.StartsWith(t, StringComparison.Ordinal)) continue;
                if (eojeol.Length == t.Length) return true;
                var rest = eojeol.Substring(t.Length);
                foreach (var u in Units)
                    if (rest == u) return true;
            }
            return false;
        }

        // ---- sense number references -----------------------------------------------------------------------------------

        /// <summary>‘「n」’을/를 세는 단위: a counting sense referring to sense n of its own entry (no headword in the quotes).</summary>
        static readonly Regex SelfUnitReference = new Regex("‘「([0-9]+)」’(을|를)(?= 세는 단위)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The display definition with each "‘「n」’을 세는 단위" written out with the room's headword ("사람을 세는 단위"),
        /// 을/를 chosen by the headword's last syllable. Only a reference to another sense of the same entry is replaced:
        /// n is the sense number within the entry before links are filtered, 1 ≤ n ≤ senseCount and n ≠ the sense's own
        /// number; a reference naming another headword (‘마음「1」’) or another number stays as written. The dictionary
        /// text itself is not changed: `links` (found in `definition`) are moved to the new text with the same targets,
        /// and the inserted headword is plain text. Returns `definition` and `links` unchanged when nothing is replaced.
        /// </summary>
        public static string ResolveSelfReferences(string definition, string headword, int number, int senseCount,
            IReadOnlyList<ShelfTextPart> links, out IReadOnlyList<ShelfTextPart> movedLinks)
        {
            movedLinks = links;
            if (string.IsNullOrEmpty(definition) || string.IsNullOrEmpty(headword) || senseCount < 2 || definition.IndexOf('「') < 0) return definition;
            var sb = new StringBuilder(definition.Length);
            var spans = new List<(int Start, int End, int Shift)>();    // replaced ranges of `definition`, shift after each
            var cursor = 0;
            var shift = 0;
            foreach (Match m in SelfUnitReference.Matches(definition))
            {
                if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > senseCount || n == number) continue;
                var text = headword + ObjectParticle(headword, m.Groups[2].Value);
                sb.Append(definition, cursor, m.Index - cursor).Append(text);
                cursor = m.Index + m.Length;
                shift += text.Length - m.Length;
                spans.Add((m.Index, cursor, shift));
            }
            if (spans.Count == 0) return definition;
            sb.Append(definition, cursor, definition.Length - cursor);

            var moved = new List<ShelfTextPart>(links.Count);
            foreach (var link in links)
            {
                var offset = 0;
                var inside = false;
                foreach (var span in spans)
                {
                    if (link.Start + link.Length <= span.Start) break;
                    if (link.Start < span.End) { inside = true; break; }
                    offset = span.Shift;
                }
                if (!inside) moved.Add(new ShelfTextPart(link.Text, link.Start + offset, link.Target));
            }
            movedLinks = moved;
            return sb.ToString();
        }

        /// <summary>을 after a final consonant, 를 after a vowel; `fallback` when the last letter is no Hangul syllable.</summary>
        static string ObjectParticle(string word, string fallback)
        {
            var c = word[word.Length - 1];
            if (c < '가' || c > '힣') return fallback;
            return (c - '가') % 28 != 0 ? "을" : "를";
        }

        // ---- confirmed split exceptions ------------------------------------------------------------------------------
        // Checked against the display definition (Han parentheses removed) of the Standard Korean Dictionary package.
        // Only eojeols whose default split (longest ending first) loses a proper door are listed; eojeols the default
        // rule already splits correctly are not. A listed headword must be the start of the eojeol and a registered
        // headword, else the exception is ignored (see ExceptionProblems); door conditions and room links still apply.

        /// <summary>
        /// Per-room splits, for an eojeol whose split depends on the context: "SenseId|eojeol" → headword. Checked first.
        /// 말과의 is 말과(horse family) + 의 in animal definitions; in 조사(397604)/토(25981) it is 말(word) + 과의, whose
        /// headword has one letter (no door), so the ambiguous eojeol staying plain text there loses nothing. The 말과 of
        /// "그 말과 다른" there needs no entry: the comparison rule reads it as 말 + 과.
        /// A suffix is only cut here, where its use was read: 영화용 is 영화 + 용 (for films) in 필름(68510).
        /// </summary>
        static readonly Dictionary<string, string> SenseSplits = new Dictionary<string, string>
        {
            { "68510|영화용", "영화" },      // 필름 "영화용 음화와 양화를 통틀어 이르는 말." (suffix -용)
            { "126851|말과의", "말과" },     // 당나귀 "말과의 포유류."
            { "146729|말과의", "말과" },     // 말 "말과의 포유류."
            // Two endings leave a headword (자연 + 과의 / 자연과 + 의 …); the context reads the first.
            { "138335|자연과의", "자연" },   // 라이트 "자연과의 융화를 꾀한"
            { "244222|행정과는", "행정" },   // 위원회 "일반 행정과는 달리"
            { "242361|전화로부터", "전화" }, // 워너 "고미술을 전화(戰禍)로부터 보호하기" (전화09 is a sense of the 전화 room)
            { "375423|과거로부터", "과거" }, // 시간 "과거로부터 현재와 미래로"
            // 개(dog) + 의, and one counter after a variable, with no quantity word before them.
            { "3402|개의", "개" },           // 강아지 "개의 새끼."
            { "97555|개의", "개" },          // 구보 "개의 쓸개 속에 든 결석을…"
            { "120292|개의", "개" },         // 뉴펀들랜드 "개의 한 품종."
            { "159957|개의", "개" },         // 바둑이 "…또는 그런 개의 이름."
            { "341533|개의", "개" },         // 구분 "개의 똥."
            { "378211|개의", "개" },         // 구조 "…주로 개의 몸에 붙어 기생한다."
            { "451771|개의", "개" },         // 구사 "개의 쓸개 속에 든 결석을…"
            { "456469|개의", "개" },         // 런던 "…개의 야성과 환경을 다룬…"
            { "525185|개의", "개" },         // 구간 "개의 간을 한방에서 이르는 말."
            { "21092|개의", "개" },          // 자유도 "<I>n</I>개의 질점계의…" (counter after a variable)
        };

        /// <summary>
        /// Confirmed verb or adjective forms that look like a noun plus an ending (모자라는 = 모자라다, not 모자 + 라는).
        /// Matched against the whole eojeol before any cut; such an eojeol stays plain text.
        /// </summary>
        static readonly HashSet<string> NotNounForms = new HashSet<string>
        {
            "모자라는",          // 단점(564928) "잘못되고 모자라는 점."
        };

        /// <summary>
        /// Eojeols split the same way in every use found in the package: eojeol → headword (the rest stays plain text).
        /// Each has two splits on registered headwords (결과의: 결 + 과의 or 결과 + 의), so without this entry it would stay
        /// plain text; the uses found all read the listed one.
        /// </summary>
        static readonly Dictionary<string, string> WordSplits = new Dictionary<string, string>
        {
            { "결과의", "결과" },     // 9: 효율(29811) "얻은 결과의 비율", 과실, 기록, 이의, 인연, 지향, 효율성, 고효율, 효율화
            { "결과도", "결과" },     // 3: 연기, 공문, 군
            { "경로의", "경로" },     // 1: 속도
            { "공로의", "공로" },     // 1: 서훈
            { "교과의", "교과" },     // 5: 자연, 자연과, 커리큘럼, 설과, 지도
            { "나이라는", "나이" },   // 1: 이모 "머리털이 나기 시작하는 나이라는 뜻"
            { "나이라서", "나이" },   // 1: 정성 "한창 나이라서"
            { "네로의", "네로" },     // 1: 베드로
            { "도로는", "도로" },     // 1: 곡도 "철도는 …, 도로는 …"
            { "도로의", "도로" },     // 4: 랠리, 램프, 다윈, 도표
            { "맛과의", "맛과" },     // 2: 마(129), 마(40326) "맛과의 여러해살이 덩굴풀"
            { "무과의", "무과" },     // 3: 원시, 대사, 정량
            { "미만의", "미만" },     // 12: 경차, 절반, 아동, 군정, 미성년자, 소호, 중형차(3), 소형차, 어린이집, 국민연금
            { "사과의", "사과" },     // 5: 곡성군, 음성, 이과("사과(四果)의 하나"), 콩코드, 진사("사과의 말")
            { "사이라는", "사이" },   // 1: 초월 "월나라의 사이라는 뜻"
            { "서로의", "서로" },     // 8: 밀착, 회전, 동맹, 부정, 항성, 중화, 정성, 로밍
            { "선로의", "선로" },     // 2: 부스터, 전철
            { "소아과의", "소아과" }, // 1: 소아의 (기본 규칙은 소아 + 과의)
            { "신라는", "신라" },     // 3: 국사, 상사, 대사 "통일 신라는"
            { "아이라는", "아이" },   // 2: 미아, 이세
            { "열과의", "열과" },     // 1: 분과 "열과(裂果)의 하나"
            { "원자로의", "원자로" }, // 3: 리튬, 나트륨, 흑연 (기본 규칙은 원자 + 로의)
            { "인과의", "인과" },     // 4: 화교, 사견, 경계, 행복 "인과의 도리"
            { "전과의", "전과" },     // 1: 별 "전과의 범행 수"
            { "콩과의", "콩과" },     // 12: 동부, 등, 땅콩, 콩과, 동부, 기초, 대두, 소형, 콩, 클로버, 팥, 자형
            { "통로는", "통로" },     // 2: 버스
            { "포로의", "포로" },     // 1: 적십자
            { "학과의", "학과" },     // 1: 진도
            { "항로의", "항로" },     // 2: 사방, 사우샘프턴
            { "회로의", "회로" },     // 7: 칩, 낸드, 단자, 단락, 쇼트, 결선, 잭
            { "효과는", "효과" },     // 1: 대리인
            { "효과의", "효과" },     // 2: 계약, 기능성
        };

        /// <summary>Exceptions whose headword is not the start of the eojeol or not a registered headword (they are ignored).</summary>
        public static List<string> ExceptionProblems(Func<string, bool> isEntry)
        {
            var problems = new List<string>();
            foreach (var p in SenseSplits)
            {
                var eojeol = p.Key.Substring(p.Key.IndexOf('|') + 1);
                if (!ValidException(eojeol, p.Value, isEntry)) problems.Add($"{p.Key} → {p.Value}");
            }
            foreach (var p in WordSplits)
                if (!ValidException(p.Key, p.Value, isEntry)) problems.Add($"{p.Key} → {p.Value}");
            return problems;
        }

        static bool ValidException(string eojeol, string headword, Func<string, bool> isEntry) =>
            !string.IsNullOrEmpty(headword) && eojeol.StartsWith(headword, StringComparison.Ordinal) && isEntry(headword);

        /// <summary>
        /// The headword an eojeol is split into, decided on registered headwords only (no door conditions):
        /// 1. a per-room exception for this room's SenseId; 2. a confirmed verb form gives none; 3. a counter form right
        /// after a quantity eojeol (`previous`, only spaces between) gives the counter; 4. an eojeol exception;
        /// 4b. a competing noun + 와/과 reading with a matching final consonant and supporting context;
        /// 5. the whole eojeol when it is a headword (even when spelled like an ending, e.g.
        /// the noun 에도); 6. else the
        /// part before an allowed ending when exactly one ending leaves a headword; two or more (길이라는: 길 + 이라는 or
        /// 길이 + 라는) give none. The caller then applies the door conditions to that one headword, so a failing one
        /// leaves the eojeol as plain text.
        /// </summary>
        static string Headword(string eojeol, string previous, string next, string following, string senseId,
            Func<string, bool> isEntry, Func<string, string, bool> hasParallelUse)
        {
            if (senseId != null && SenseSplits.TryGetValue(senseId + "|" + eojeol, out var bySense) && ValidException(eojeol, bySense, isEntry))
                return bySense;
            if (NotNounForms.Contains(eojeol)) return null;
            if (CounterForms.TryGetValue(eojeol, out var counter) && IsQuantity(previous) && ValidException(eojeol, counter, isEntry))
                return counter;
            if (WordSplits.TryGetValue(eojeol, out var byWord) && ValidException(eojeol, byWord, isEntry)) return byWord;

            var whole = isEntry(eojeol);
            var conjunction = ConjunctionIndex(eojeol);
            var incompatibleConjunction = false;
            if (conjunction > 0)
            {
                var tail = conjunction == eojeol.Length - 1 ? '\0' : eojeol[eojeol.Length - 1];
                // A nominal reading is either the whole eojeol or the longer noun before 의/는/도.
                if (whole || (tail != '\0' && isEntry(eojeol.Substring(0, conjunction + 1))))
                {
                    incompatibleConjunction = !FitsFinalConsonant(eojeol[conjunction - 1], eojeol[conjunction] == '과');
                    if (!incompatibleConjunction)
                    {
                        var stem = eojeol.Substring(0, conjunction);
                        if (isEntry(stem) && HasConjunctionContext(stem, tail, previous, next, following, isEntry, hasParallelUse))
                            return stem;
                    }
                }
            }

            if (whole) return eojeol;
            string found = null;
            foreach (var ending in Endings)
            {
                if (eojeol.Length <= ending.Length || !eojeol.EndsWith(ending, StringComparison.Ordinal)) continue;
                var stemLength = eojeol.Length - ending.Length;
                if (incompatibleConjunction && stemLength == conjunction) continue;
                var stem = eojeol.Substring(0, stemLength);
                if (!isEntry(stem)) continue;
                if (found != null) return null;                  // ambiguous split, no confirmed exception: plain text
                found = stem;
            }
            return found;
        }

        /// <summary>
        /// The definition cut at the given links (in order, not overlapping) into plain parts and link parts; the
        /// plain parts are the original text between them. Concatenating every part's Text gives the definition.
        /// </summary>
        public static List<ShelfTextPart> Split(string definition, IReadOnlyList<ShelfTextPart> links)
        {
            var parts = new List<ShelfTextPart>();
            if (string.IsNullOrEmpty(definition)) return parts;
            var cursor = 0;
            foreach (var link in links)
            {
                if (link.Start < cursor) continue;
                if (link.Start > cursor) parts.Add(new ShelfTextPart(definition.Substring(cursor, link.Start - cursor), cursor, null));
                parts.Add(link);
                cursor = link.Start + link.Length;
            }
            if (cursor < definition.Length) parts.Add(new ShelfTextPart(definition.Substring(cursor), cursor, null));
            return parts;
        }
    }
}
