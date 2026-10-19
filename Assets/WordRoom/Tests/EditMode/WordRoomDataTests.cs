using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using WordRoom.Data.Documents;
using WordRoom.Data.Validation;
using WordRoom.UnityLayer;

namespace WordRoom.Data.Tests
{
    /// <summary>Synthetic fixture: cases absent from the real data (partial, invalid, exact-boundary scores, excluded rows).</summary>
    public sealed class FixtureTests
    {
        static string FixtureRoot => Path.Combine(Application.dataPath, "WordRoom", "Tests", "Fixtures~", "Synthetic");
        static string FixturePackage => Path.Combine(FixtureRoot, PackageFormat.DefaultFolderName);
        static WordRoomDataset cached;

        static WordRoomDataset Load() => cached ?? (cached = WordRoomDataLoader.Load(new DirectoryPackageSource(FixturePackage)));

        static int W(WordRoomDataset d, string word)
        {
            Assert.IsTrue(d.TryGetWordRank(word, out var r), word);
            return r;
        }

        static int S(WordRoomDataset d, string senseId)
        {
            Assert.IsTrue(d.TryGetSenseRow(senseId, out var r), senseId);
            return r;
        }

        [Test]
        public void MatchesPythonReference()
        {
            var report = ReferenceComparer.Compare(Load(), ReferenceComparer.LoadReference(Path.Combine(FixtureRoot, "python_reference.json")));
            Debug.Log("[합성 자료] " + report);
            Assert.IsTrue(report.Passed, report.ToString());
        }

        [Test]
        public void RelationStatesFollowRuleB()
        {
            var d = Load();
            var rel = d.Relations;
            double t08 = d.ThresholdLadder[0], t07 = d.ThresholdLadder[1], t06 = d.ThresholdLadder[2], t05 = d.ThresholdLadder[3];
            Assert.AreEqual(0.8, t08);
            // One approved combination approves the word pair; the NO of the other combination does not reject it.
            Assert.AreEqual(WordPairState.Approved, rel.GetWordPairState(W(d, "경기"), W(d, "체육"), t08));
            Assert.IsTrue(rel.TryGetBestEvidence(W(d, "경기"), W(d, "체육"), t08, out var e));
            Assert.AreEqual("102", d.GetSenseId(e.SenseRow));
            // Score exactly 0.8 is approved at 0.8; 0.7999999999999999 is not (float64 kept, no rounding).
            Assert.AreEqual(WordPairState.Approved, rel.GetWordPairState(W(d, "경기"), W(d, "경영"), t08));
            Assert.AreEqual(WordPairState.BelowThreshold, rel.GetWordPairState(W(d, "배"), W(d, "바다"), t08));
            Assert.AreEqual(WordPairState.Approved, rel.GetWordPairState(W(d, "배"), W(d, "바다"), t07));
            Assert.AreEqual(WordPairState.BelowThreshold, rel.GetWordPairState(W(d, "체육"), W(d, "경영"), t06));
            Assert.AreEqual(WordPairState.Approved, rel.GetWordPairState(W(d, "체육"), W(d, "경영"), t05));
            // A YES in one input order only (partial) or next to an invalid answer is never approved.
            foreach (var t in d.ThresholdLadder)
            {
                Assert.AreEqual(WordPairState.ErrorOrIncomplete, rel.GetWordPairState(W(d, "배"), W(d, "선박"), t));
                Assert.AreEqual(WordPairState.ErrorOrIncomplete, rel.GetWordPairState(W(d, "배"), W(d, "과일"), t));
            }
            Assert.IsTrue(rel.TryGetSensePair(S(d, "99"), S(d, "700"), out var partial));
            Assert.AreEqual("partial", partial.Status);
            Assert.IsFalse(partial.ValidYes);
            Assert.AreEqual("YES", partial.ForwardAnswer);
            Assert.IsNull(partial.BackwardAnswer);
            Assert.IsTrue(double.IsNaN(partial.BackwardScore));
            Assert.IsTrue(rel.TryGetSensePair(S(d, "210"), S(d, "410"), out var invalid));
            Assert.AreEqual("invalid", invalid.Status);
            // Every combination NO → rejected; one NO with another combination unchecked → unchecked.
            Assert.AreEqual(WordPairState.Rejected, rel.GetWordPairState(W(d, "사과"), W(d, "배"), t08));
            CollectionAssert.Contains(rel.GetRejectedPartners(W(d, "사과")).ToList(), W(d, "배"));
            Assert.AreEqual(WordPairState.Unchecked, rel.GetWordPairState(W(d, "감정"), W(d, "사과"), t08));
            // Rows of another judge version, changed definitions or senses outside the vocabulary are not evidence.
            Assert.AreEqual(WordPairState.Unchecked, rel.GetWordPairState(W(d, "체육"), W(d, "바다"), t08));
            Assert.AreEqual(1, d.RelationInfo.counts.excludedSignatureMismatch);
            Assert.AreEqual(1, d.RelationInfo.counts.excludedOutsideVocabulary);
            Assert.AreEqual(1, d.RelationInfo.counts.otherVersionRows);
            // No self-edge from two senses of one surface; approval is per word pair, never transitive.
            CollectionAssert.DoesNotContain(rel.GetApprovedNeighbors(W(d, "배"), d.ThresholdLadder[7]), W(d, "배"));
            Assert.AreEqual(WordPairState.Unchecked, rel.GetWordPairState(W(d, "경기"), W(d, "과일"), d.ThresholdLadder[7]));
        }

        [Test]
        public void VocabularySettingsAndText()
        {
            var d = Load();
            var top8 = d.CreateScope(8, 1, 8);
            Assert.AreEqual(8, top8.Count);
            Assert.IsFalse(top8.Contains(W(d, "조각")));
            Assert.AreEqual(7, d.CreateScope(8, 2, 8).Count);
            Assert.Throws<ArgumentException>(() => d.CreateScope(13, 1, 8));
            Assert.Throws<ArgumentException>(() => d.CreateScope(12, 1, 9));
            // Range filtering keeps the original sense rows and vectors of the remaining surfaces.
            var ship = W(d, "선박");
            Assert.IsTrue(top8.Contains(ship));
            CollectionAssert.AreEqual(new[] { S(d, "700") }, d.GetSenseRows(ship));
            Assert.AreEqual(1, d.Settings.GetInt("min_word_length"));
            Assert.AreEqual(12, d.Settings.GetInt("vocabulary_limit"));
            Assert.AreEqual("configs/fasttext_restricted.json", d.Settings.GetString("fasttext_restricted"));
            Assert.IsTrue(d.Settings.Differences().Any(x => x.key == "min_word_length" && x.rawJson == "2" && x.valueJson == "1"));
            Assert.IsTrue(d.Settings.DroppedRawKeys.Any(x => x.key == "search_budget"));
            Assert.IsTrue(d.IsRestricted(W(d, "감정")));
            Assert.IsTrue(d.IsRestricted(W(d, "나")));
            Assert.IsFalse(d.IsRestricted(W(d, "바다")));
            Assert.IsFalse(d.HasFastText(W(d, "조각")));
            Assert.IsFalse(d.TryFastTextSimilarity(W(d, "조각"), W(d, "바다"), out _));
            Assert.AreEqual(1.0, d.FastTextSimilarity(W(d, "바다"), W(d, "바다")), 1e-6);
            Assert.AreEqual(1.0, d.E5Similarity(S(d, "512"), S(d, "512")), 1e-6);
            StringAssert.Contains("𝄞", d.GetSense(S(d, "512")).Definition);
            StringAssert.Contains("\"따옴표\"와 \\역슬래시\\", d.GetSense(S(d, "102")).Definition);
            StringAssert.Contains(" ", d.GetSense(S(d, "205")).Definition);
            Assert.AreEqual(W(d, "경기"), W(d, "경기1"));
            Assert.AreEqual(W(d, "가나다라마바사아"), W(d, "가나-다라마바사아"));
            Assert.AreEqual(8, d.GetWordLength(W(d, "가나다라마바사아")));
        }
    }

    /// <summary>Damaged, mixed or invalid packages must fail with a specific error instead of loading.</summary>
    public sealed class CorruptionTests
    {
        static string FixturePackage => Path.Combine(Application.dataPath, "WordRoom", "Tests", "Fixtures~", "Synthetic", PackageFormat.DefaultFolderName);
        string dir;

        [SetUp]
        public void Copy()
        {
            dir = Path.Combine(Path.GetTempPath(), "wordroom_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(FixturePackage)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        }

        [TearDown]
        public void Remove() => Directory.Delete(dir, true);

        DataErrorCode Fail()
        {
            var e = Assert.Throws<WordRoomDataException>(() => WordRoomDataLoader.Load(new DirectoryPackageSource(dir)));
            Debug.Log("검출: " + e.Message);
            return e.Code;
        }

        void Edit(string name, Action<byte[]> change, bool rehash)
        {
            var path = Path.Combine(dir, name);
            var bytes = File.ReadAllBytes(path);
            change(bytes);
            File.WriteAllBytes(path, bytes);
            if (rehash) Rehash(name);
        }

        void EditText(string name, Func<string, string> change, bool rehash = true)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, change(File.ReadAllText(path, Encoding.UTF8)), new UTF8Encoding(false));
            if (rehash) Rehash(name);
        }

        /// <summary>Makes the manifest agree with a modified file, so deeper checks (not the hash) must find the problem.</summary>
        void Rehash(string name)
        {
            var path = Path.Combine(dir, PackageFormat.Manifest);
            var m = JsonUtility.FromJson<ManifestDocument>(File.ReadAllText(path, Encoding.UTF8));
            var bytes = File.ReadAllBytes(Path.Combine(dir, name));
            var entry = m.files.First(f => f.name == name);
            entry.bytes = bytes.Length;
            using (var sha = SHA256.Create()) entry.sha256 = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
            File.WriteAllText(path, JsonUtility.ToJson(m, true), new UTF8Encoding(false));
        }

        void EditManifest(Action<ManifestDocument> change)
        {
            var path = Path.Combine(dir, PackageFormat.Manifest);
            var m = JsonUtility.FromJson<ManifestDocument>(File.ReadAllText(path, Encoding.UTF8));
            change(m);
            File.WriteAllText(path, JsonUtility.ToJson(m, true), new UTF8Encoding(false));
        }

        [Test]
        public void IntactCopyLoads() => Assert.DoesNotThrow(() => WordRoomDataLoader.Load(new DirectoryPackageSource(dir)));

        [Test]
        public void MissingPackage()
        {
            File.Delete(Path.Combine(dir, PackageFormat.Manifest));
            Assert.AreEqual(DataErrorCode.MissingFile, Fail());
        }

        [Test]
        public void MissingFile()
        {
            File.Delete(Path.Combine(dir, PackageFormat.Relations));
            Assert.AreEqual(DataErrorCode.MissingFile, Fail());
        }

        [Test]
        public void FlippedVectorByteIsHashMismatch()
        {
            Edit(PackageFormat.E5Vectors, b => b[PackageFormat.HeaderBytes + 10] ^= 0x01, false);
            Assert.AreEqual(DataErrorCode.HashMismatch, Fail());
        }

        [Test]
        public void FlippedDefinitionByteIsHashMismatch()
        {
            Edit(PackageFormat.Senses, b => b[b.Length - 5] ^= 0x01, false);
            Assert.AreEqual(DataErrorCode.HashMismatch, Fail());
        }

        [Test]
        public void TruncatedFileIsSizeMismatch()
        {
            var path = Path.Combine(dir, PackageFormat.Relations);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.Take(bytes.Length - 64).ToArray());
            Assert.AreEqual(DataErrorCode.SizeMismatch, Fail());
        }

        [Test]
        public void JsonFromAnotherExportIsDetected()
        {
            EditText(PackageFormat.Words, t => t.Replace("\"packageId\":\"", "\"packageId\":\"f"));
            Assert.AreEqual(DataErrorCode.PackageIdMismatch, Fail());
        }

        [Test]
        public void BinaryFromAnotherExportIsDetected()
        {
            Edit(PackageFormat.FastTextVectors, b => b[48] ^= 0xFF, true);
            Assert.AreEqual(DataErrorCode.PackageIdMismatch, Fail());
        }

        [Test]
        public void UnsupportedSchema()
        {
            EditManifest(m => m.schemaVersion = 2);
            Assert.AreEqual(DataErrorCode.UnsupportedSchema, Fail());
        }

        [Test]
        public void WrongDimensionIsCountMismatch()
        {
            EditManifest(m => m.counts.e5Dimensions = 15);
            Assert.AreEqual(DataErrorCode.CountMismatch, Fail());
        }

        [Test]
        public void RelationRecordWithUnknownSense()
        {
            Edit(PackageFormat.Relations, b => BitConverter.GetBytes(9999).CopyTo(b, PackageFormat.HeaderBytes), true);
            Assert.AreEqual(DataErrorCode.InvalidReference, Fail());
        }

        [Test]
        public void DuplicateRelationRecord()
        {
            Edit(PackageFormat.Relations, b => Array.Copy(b, PackageFormat.HeaderBytes, b, PackageFormat.HeaderBytes + PackageFormat.RelationRecordBytes,
                PackageFormat.RelationRecordBytes), true);
            Assert.AreEqual(DataErrorCode.DuplicateEntry, Fail());
        }

        [Test]
        public void SenseWithUnknownWord()
        {
            EditText(PackageFormat.Senses, t => t.Replace("\"wordRanks\":[", "\"wordRanks\":[99"));
            Assert.AreEqual(DataErrorCode.InvalidReference, Fail());
        }

        [Test]
        public void NaNInVector()
        {
            Edit(PackageFormat.E5Vectors, b => BitConverter.GetBytes(float.NaN).CopyTo(b, PackageFormat.HeaderBytes + 4 * 20), true);
            Assert.AreEqual(DataErrorCode.InvalidValue, Fail());
        }

        [Test]
        public void MaskedFastTextMarkedValid()
        {
            EditText(PackageFormat.Words, t =>
            {
                var i = t.IndexOf("\"fasttextValid\":[", StringComparison.Ordinal);
                var head = t.Substring(0, i);
                var list = t.Substring(i).Replace(",0,", ",1,");
                return head + list;
            });
            Assert.AreEqual(DataErrorCode.InvalidValue, Fail());
        }

        [Test]
        public void ExtraFilesAreIgnored()
        {
            File.WriteAllText(Path.Combine(dir, "manifest.json.meta"), "unity meta");
            Assert.DoesNotThrow(() => WordRoomDataLoader.Load(new DirectoryPackageSource(dir)));
        }
    }

    public sealed class ServiceTests
    {
        [Test]
        public void QueriesBeforeLoadingAreRejected()
        {
            var go = new GameObject("service");
            try
            {
                var service = go.AddComponent<WordRoomDataService>();
                Assert.AreEqual(DataLoadState.NotStarted, service.State);
                Assert.IsFalse(service.TryGetDataset(out _));
                Assert.Throws<DataNotReadyException>(() => _ = service.Dataset);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void NoModelInferenceNetworkOrProcessInDataCode()
        {
            var root = Path.Combine(Application.dataPath, "WordRoom", "Scripts");
            var forbidden = new[] { "Process.Start", "ProcessStartInfo", "UnityWebRequest", "HttpClient", "WebClient", "TcpClient", "System.Net", "python.exe" };
            var hits = new List<string>();
            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                hits.AddRange(forbidden.Where(text.Contains).Select(f => $"{Path.GetFileName(file)}: {f}"));
            }
            CollectionAssert.IsEmpty(hits);
        }
    }

    /// <summary>The real exported package against the Python baseline (skipped when the package was not exported here).</summary>
    public sealed class RealDataTests
    {
        static string Package => Path.Combine(Application.streamingAssetsPath, PackageFormat.DefaultFolderName);
        static string Reference => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DataValidation", "real", "python_reference.json"));

        [Test]
        public void RealPackageMatchesPythonReference()
        {
            if (!File.Exists(Path.Combine(Package, PackageFormat.Manifest)) || !File.Exists(Reference))
                Assert.Ignore($"실데이터 패키지 또는 Python 기준값이 없습니다. Tools/DataExport/export_unity_data.py export를 먼저 실행하세요. ({Package}, {Reference})");
            var before = GC.GetTotalMemory(true);
            var d = WordRoomDataLoader.Load(new DirectoryPackageSource(Package));
            var after = GC.GetTotalMemory(true);
            Debug.Log($"[실데이터] {d.LoadReport} · 관리 힙 증가 {(after - before) / 1048576.0:F1} MB");
            Assert.AreEqual(15000, d.WordCount);
            Assert.AreEqual(48318, d.SenseCount);
            Assert.AreEqual(768, d.E5Dimensions);
            Assert.AreEqual(300, d.FastTextDimensions);
            var report = ReferenceComparer.Compare(d, ReferenceComparer.LoadReference(Reference));
            Debug.Log("[실데이터] " + report);
            Debug.Log("[실데이터] " + DatasetBenchmark.Run(d));
            var quick = WordRoomDataLoader.Load(new DirectoryPackageSource(Package), new LoadOptions { VerifyHashes = false });
            Debug.Log($"[실데이터] 해시 생략 로딩 {quick.LoadReport}");
            Assert.IsTrue(report.Passed, report.ToString());
            foreach (var f in d.Manifest.files)
                StringAssert.DoesNotMatch(@"(?i)(safetensors|cc\.ko|\.gguf|\.pt$|\.onnx)", f.name, "모델 파일이 패키지에 포함되면 안 됩니다");
        }
    }
}
