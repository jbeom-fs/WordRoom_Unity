using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using WordRoom.Shelf;
using WordRoom.UnityLayer;

namespace WordRoom.Data.Tests
{
    /// <summary>
    /// Link extraction rules (ShelfLinkText.FindCandidates): eojeol ranges, the clicked range of each link, confirmed
    /// exceptions (per sense, verb forms, counters, eojeol splits), whole-eojeol headwords before endings, ambiguous
    /// splits left plain, door conditions applied after the split without re-splitting. Synthetic headword sets keep
    /// each case exact. The real package also checks representative results of the comparison rules.
    /// </summary>
    public sealed class ShelfLinkTextTests
    {
        /// <summary>Links of `text` shown as "[link]" over the original text (the reading screen's clickable ranges).</summary>
        static string Bracketed(string text, string senseId, string[] entries, Func<string, bool> isDoor = null)
        {
            var set = new HashSet<string>(entries);
            isDoor ??= w => set.Contains(w) && w.Length >= 2;
            var links = ShelfLinkText.FindCandidates(text, senseId, set.Contains, isDoor);
            var parts = ShelfLinkText.Split(text, links);
            var sb = new StringBuilder();
            var plain = new StringBuilder();
            foreach (var p in parts)
            {
                Assert.AreEqual(text.Substring(p.Start, p.Length), p.Text, "부분 글자가 원문 범위와 다릅니다");
                if (p.IsLink) Assert.AreEqual(p.Target, p.Text, "링크 글자와 표제어가 다릅니다");
                sb.Append(p.IsLink ? "[" + p.Text + "]" : p.Text);
                plain.Append(p.Text);
            }
            Assert.AreEqual(text, plain.ToString(), "링크 표시를 뺀 본문이 원문과 다릅니다");
            return sb.ToString();
        }

        [Test]
        public void ConfirmedSuffix_LinksOnlyTheHeadword()
        {
            Assert.AreEqual("[영화]용 [음화]", Bracketed("영화용 음화", "68510", new[] { "영화", "음화" }));
            var links = ShelfLinkText.FindCandidates("영화용 음화", "68510", new HashSet<string> { "영화" }.Contains, w => true);
            Assert.AreEqual(1, links.Count);
            Assert.AreEqual(0, links[0].Start);
            Assert.AreEqual(2, links[0].Length);
            // -용 is cut only where it was confirmed.
            Assert.AreEqual("영화용 음화", Bracketed("영화용 음화", "1", new[] { "영화" }));
            Assert.AreEqual("영화용 음화", Bracketed("영화용 음화", null, new[] { "영화" }));
        }

        [Test]
        public void ParticleAndConfirmedSplit()
        {
            Assert.AreEqual("[바다]의 경계", Bracketed("바다의 경계", null, new[] { "바다" }));
            Assert.AreEqual("[바다]의 [경계]", Bracketed("바다의 경계", null, new[] { "바다", "경계" }));
            // 결 + 과의 and 결과 + 의 are both possible: the confirmed eojeol split decides.
            Assert.AreEqual("얻은 [결과]의 [비율]", Bracketed("얻은 결과의 비율", "29811", new[] { "결", "결과", "비율" }));
        }

        [Test]
        public void VerbFormsPrefixesAndCounters_StayPlain()
        {
            Assert.AreEqual("잘못되고 모자라는 점", Bracketed("잘못되고 모자라는 점", "564928", new[] { "모자", "점" }));
            Assert.AreEqual("비공개", Bracketed("비공개", null, new[] { "공개" }));
            Assert.AreEqual("[비공개]", Bracketed("비공개", null, new[] { "공개", "비공개" }));
            Assert.AreEqual("비공개로", Bracketed("비공개로", null, new[] { "공개" }));
            Assert.AreEqual("두 개의 상자", Bracketed("두 개의 상자", null, new[] { "개", "개의" }));
            Assert.AreEqual("두 개의 [상자]", Bracketed("두 개의 상자", null, new[] { "개", "개의", "상자" }));
        }

        [Test]
        public void AmbiguousSplit_StaysPlain_AndIsNeverChangedForLength()
        {
            // 길 + 이라는 / 길이 + 라는: ambiguous, no exception → plain (and no 길이 link to dodge the one-letter 길).
            Assert.AreEqual("길이라는 말", Bracketed("길이라는 말", null, new[] { "길", "길이", "말" }));
            // Only 길 + 이라는 is possible: 길 has one letter, so no link, and 길이 is never tried.
            Assert.AreEqual("길이라는 말", Bracketed("길이라는 말", null, new[] { "길", "말" }));
            // One split only: taken.
            Assert.AreEqual("[공자]라는 [사람]", Bracketed("공자라는 사람", null, new[] { "공자", "사람" }));
        }

        [Test]
        public void WholeEojeol_BeforeEndings_AndBoundaries()
        {
            Assert.AreEqual("[에도] 시대", Bracketed("에도 시대", null, new[] { "에도" }));
            Assert.AreEqual("바다의경계는", Bracketed("바다의경계는", null, new[] { "바다", "경계" }));
            Assert.AreEqual("43배이다", Bracketed("43배이다", null, new[] { "배" }));
            Assert.AreEqual("[바다]·[육지]", Bracketed("바다·육지", null, new[] { "바다", "육지" }));
        }

        [Test]
        public void Comparison_ReadsNounPlusParticle_EvenWhenTheWholeEojeolIsAHeadword()
        {
            // 말 and 말과 both registered: before 다른 it is 말 + 과; 말 has one letter, so 말과 stays plain text.
            var both = new[] { "말", "말과" };
            Assert.AreEqual("그 말과 다른", Bracketed("그 말과 다른", null, both));
            Assert.AreEqual("[생각]과 다른", Bracketed("생각과 다른", null, new[] { "생각", "생각과" }));
            Assert.AreEqual("[결과]와 비슷한", Bracketed("결과와 비슷한", null, new[] { "결", "결과", "결과와" }));
            foreach (var word in new[] { "같은", "같다", "다른", "다르다", "비슷한", "비슷하다" })
                Assert.AreEqual($"[생각]과 {word}", Bracketed($"생각과 {word}", null, new[] { "생각", "생각과" }), word);
            // Only spaces count as "right before": punctuation, another word or no next word leaves the usual rules.
            Assert.AreEqual("[말과], 다른", Bracketed("말과, 다른", null, both));
            // 말과의 splits two ways (말 + 과의, 말과 + 의): without a confirmed per-sense split it stays plain text.
            Assert.AreEqual("말과의 [포유류]", Bracketed("말과의 포유류", null, new[] { "말", "말과", "포유류" }));
            Assert.AreEqual("[말과] [포유류]", Bracketed("말과 포유류", null, new[] { "말", "말과", "포유류" }));
            Assert.AreEqual("[말과]", Bracketed("말과", null, both));
            // The particle must fit the last syllable: 사과(apple) ends in 과 after a vowel, so it stays whole.
            Assert.AreEqual("[사과] 같은", Bracketed("사과 같은", null, new[] { "사", "사과" }));
            // Confirmed exceptions keep priority over the comparison (a per-sense split of the same eojeol).
            Assert.AreEqual("[말과]의 포유류", Bracketed("말과의 포유류", "146729", new[] { "말", "말과" }));
        }

        [TestCase("생각과 같고", "[생각]과 같고")]
        [TestCase("생각과 같으며", "[생각]과 같으며")]
        [TestCase("생각과 함께", "[생각]과 함께")]
        [TestCase("생각과는 비슷한데", "[생각]과는 비슷한데")]
        [TestCase("생각과도 비슷한데", "[생각]과도 비슷한데")]
        [TestCase("결과와는 같으며", "[결과]와는 같으며")]
        [TestCase("결과와도 비슷한데", "[결과]와도 비슷한데")]
        [TestCase("생각과의 문법적 관계", "[생각]과의 문법적 관계")]
        [TestCase("결과와의 관계", "[결과]와의 관계")]
        [TestCase("생각과의 문법적. 관계", "생각과의 문법적. 관계")]
        [TestCase("생각과. 같이", "[생각과]. 같이")]
        [TestCase("생각과\n같이", "[생각과]\n같이")]
        [TestCase("사과는 같으며", "[사과]는 같으며")]
        [TestCase("산과 산 사이", "산과 산 사이")]
        [TestCase("강과 시내를 아울러", "강과 [시내]를 아울러")]
        [TestCase("계열과 공과 계열을 아울러", "[계열]과 [공과] [계열]을 아울러")]
        [TestCase("인과 관계를 설명하다", "[인과] 관계를 설명하다")]
        public void Conjunction_UsesMatchingParticleAndLocalContext(string text, string expected)
        {
            Assert.AreEqual(expected, Bracketed(text, null, new[]
                { "생각", "생각과", "결", "결과", "결과와", "사", "사과", "산", "산과", "강", "강과", "시내", "공", "공과", "계열", "인", "인과" }));
        }

        // ---- real package ------------------------------------------------------------------------------------------

        static WordRoomDataset realData;

        static WordRoomDataset RealData()
        {
            var package = Path.Combine(Application.streamingAssetsPath, PackageFormat.DefaultFolderName);
            if (!File.Exists(Path.Combine(package, PackageFormat.Manifest))) Assert.Ignore($"실데이터 패키지가 없습니다({package})");
            return realData ??= WordRoomDataLoader.Load(new DirectoryPackageSource(package), new LoadOptions { VerifyHashes = false });
        }

        static string RealBracketed(WordRoomDataset d, string senseId, Func<string, bool> isEntry)
        {
            Assert.IsTrue(d.TryGetSenseRow(senseId, out var row), senseId);
            var text = ShelfLinkText.DisplayDefinition(d.GetSense(row).Definition);
            var sb = new StringBuilder();
            var links = ShelfLinkText.FindCandidates(text, senseId, isEntry,
                word => d.TryGetWordRank(word, out var rank) && d.GetWordLength(rank) >= 2 && !d.IsRestricted(rank));
            foreach (var p in ShelfLinkText.Split(text, links)) sb.Append(p.IsLink ? "[" + p.Text + "]" : p.Text);
            return sb.ToString();
        }

        [Test]
        public void RealPackage_RuleBasedExtraction()
        {
            var d = RealData();
            bool IsEntry(string word) => d.TryGetWordRank(word, out var rank) && d.GetWord(rank) == word;
            CollectionAssert.IsEmpty(ShelfLinkText.ExceptionProblems(IsEntry), "적용되지 않는 예외가 있습니다");
            StringAssert.StartsWith("[영화]용 음화", RealBracketed(d, "68510", IsEntry));
            StringAssert.Contains("얻은 [결과]의 [비율]", RealBracketed(d, "29811", IsEntry));
            var flaw = RealBracketed(d, "564928", IsEntry);
            StringAssert.Contains("모자라는", flaw);
            StringAssert.DoesNotContain("[모자]", flaw);
            StringAssert.StartsWith("[말과]의 [포유류]. 말과 비슷한데", RealBracketed(d, "126851", IsEntry));
            StringAssert.StartsWith("[말과]의 [포유류]", RealBracketed(d, "146729", IsEntry));
            foreach (var particleSense in new[] { "397604", "25981" })                              // 조사, 토
            {
                var text = RealBracketed(d, particleSense, IsEntry);
                StringAssert.Contains("그 말과 다른", text, particleSense);
                StringAssert.DoesNotContain("[말과]", text, particleSense);
            }

        }
    }
}
