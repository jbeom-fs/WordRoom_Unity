using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    /// each case exact; the real package checks the cases named in the plan and that the reading screen's links and
    /// the room graph's connections are the same.
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

        static string RealBracketed(ShelfLinkGraph graph, WordRoomDataset d, string senseId, bool displayed = false)
        {
            Assert.IsTrue(d.TryGetSenseRow(senseId, out var row), senseId);
            var text = ShelfLinkText.DisplayDefinition(d.GetSense(row).Definition);
            var sb = new StringBuilder();
            var links = displayed ? graph.Doors(row) : graph.Extract(text, senseId);
            foreach (var p in ShelfLinkText.Split(text, links)) sb.Append(p.IsLink ? "[" + p.Text + "]" : p.Text);
            return sb.ToString();
        }

        [Test]
        public void RealPackage_NamedCases_AndScreenLinksEqualGraph()
        {
            var d = RealData();
            var graph = new ShelfLinkGraph(d, 15000, 2, new List<ShelfLinkGraph.RoomRule>(), CancellationToken.None);
            CollectionAssert.IsEmpty(graph.ExceptionProblems(), "적용되지 않는 예외가 있습니다");
            StringAssert.StartsWith("[영화]용 음화", RealBracketed(graph, d, "68510"));
            StringAssert.Contains("얻은 [결과]의 [비율]", RealBracketed(graph, d, "29811"));
            var flaw = RealBracketed(graph, d, "564928");
            StringAssert.Contains("모자라는", flaw);
            StringAssert.DoesNotContain("[모자]", flaw);
            StringAssert.StartsWith("[말과]의 [포유류]. 말과 비슷한데", RealBracketed(graph, d, "126851")); // 당나귀, before deduplication
            StringAssert.StartsWith("[말과]의 [포유류]", RealBracketed(graph, d, "146729"));     // 말
            foreach (var particleSense in new[] { "397604", "25981" })                              // 조사, 토
            {
                var text = RealBracketed(graph, d, particleSense);
                StringAssert.Contains("그 말과 다른", text, particleSense);
                StringAssert.DoesNotContain("[말과]", text, particleSense);
            }

            // Every sense: links cut the shown text only at link ranges, and the reading screen's links of a room are
            // exactly the graph's connections of that room (for a goal).
            Assert.IsTrue(graph.SetGoal("지구"));
            foreach (var (senseId, excerpt) in new[]
            {
                ("35148", "갈기는 말과 같으며"),              // 기린
                ("542424", "갈기는 말과 같으며"),             // 인수
                ("598839", "[크기]는 말과 같고"),             // 유니콘
                ("126851", "[말과]의 [포유류]. 말과 비슷한데"), // 당나귀
                ("397604", "그 말과 다른 말과의"),            // 조사
                ("315312", "강과 [시내]를 아울러"),           // 하천
                ("438835", "[신경과] [의사]"),                // 프로이트
                ("15109", "[방과] [후에]"),                   // 숙제
                ("173135", "[분과] 회의에"),                  // 본회의, 회의 already linked earlier
                ("308653", "[공과] [대학]에서"),             // 파스칼
                ("363597", "[공과] 대학 따위"),              // 케임브리지, 대학 already linked earlier
                ("1396731", "[공과] 계열을 아울러"),           // 이공계, 계열 already linked earlier
                ("570548", "[경과] 중에도"),                  // 복수
                ("70168", "[인과] [관계]와"),                 // 메커니즘
                ("535316", "‘[산과] [의사]’"),                // 산의
                ("1966", "[콩과]의 한해살이풀"),              // 땅콩
                ("17331", "말과 [동작]으로"),                // 연극
                ("64490", "말과 [글로] 표현할"),               // 문장
                ("65548", "[정신]과 [육체]를 훈련"),            // 수행
                ("461386", "말과 얼굴빛을 아울러"),            // 사기, 얼굴빛 is outside the package
            })
                StringAssert.Contains(excerpt, RealBracketed(graph, d, senseId, displayed: true), senseId);
            var checkedRooms = 0;
            for (var rank = 0; rank < d.WordCount; rank++)
            {
                var word = d.GetWord(rank);
                if (!graph.IsCandidate(word)) continue;
                var screen = new HashSet<string>();
                foreach (var row in d.GetSenseRows(rank))
                {
                    var sense = d.GetSense(row);
                    if (string.IsNullOrEmpty(sense.Definition)) continue;
                    var text = ShelfLinkText.DisplayDefinition(sense.Definition);
                    var doors = graph.Doors(row);
                    var joined = new StringBuilder();
                    foreach (var p in ShelfLinkText.Split(text, doors))
                    {
                        Assert.AreEqual(text.Substring(p.Start, p.Length), p.Text);
                        joined.Append(p.Text);
                    }
                    Assert.AreEqual(text, joined.ToString(), $"{word}({sense.SenseId})");
                    foreach (var link in doors)
                    {
                        Assert.AreEqual(link.Target, link.Text, $"{word}({sense.SenseId})");
                        Assert.AreNotEqual(word, link.Target, "자기 방 링크");
                        screen.Add(link.Target);
                    }
                    Assert.AreEqual(doors.Count, doors.Select(x => x.Target).Distinct().Count(), $"{word}({sense.SenseId}): 같은 뜻풀이의 중복 링크");
                }
                CollectionAssert.AreEquivalent(graph.ValidExits(word), screen, $"{word}: 읽기 화면 링크와 방 그래프 연결이 다릅니다");
                checkedRooms++;
            }
            Debug.Log($"[링크 규칙] 실데이터 방 {checkedRooms:N0}개에서 읽기 화면 링크와 그래프 연결 일치 확인");
            Assert.Greater(checkedRooms, 10000);

            // The random challenge is drawn on these connections as before: a start at least 10 links from its goal.
            Assert.IsTrue(graph.PickChallenge(new System.Random(20261126), 10, CancellationToken.None, out var goal, out var start, out var moves, out _));
            Assert.GreaterOrEqual(moves, 10);
            Assert.AreEqual(moves, graph.DistanceToGoal(start));
            Debug.Log($"[링크 규칙] 무작위 도전 예: {start} → {goal}, 최단 {moves}회");
        }
    }
}
