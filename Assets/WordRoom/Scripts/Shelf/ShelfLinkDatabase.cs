using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Shelf
{
    /// <summary>Validated sidecar of the dictionary package, indexed by SenseId.</summary>
    internal sealed class ShelfLinkDatabase
    {
        public const string FileName = "links.json";

        internal readonly struct SenseLinks
        {
            public readonly string Definition;
            public readonly IReadOnlyList<ShelfTextPart> Links;

            public SenseLinks(string definition, IReadOnlyList<ShelfTextPart> links)
            {
                Definition = definition;
                Links = links;
            }
        }

        readonly Dictionary<string, SenseLinks> bySense;

        ShelfLinkDatabase(Dictionary<string, SenseLinks> bySense) => this.bySense = bySense;

        public SenseLinks Get(string senseId) => bySense[senseId];

        public static ShelfLinkDatabase Load(IPackageFileSource source, WordRoomDataset dataset, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            DatabaseDocument document;
            try
            {
                using var stream = source.OpenRead(FileName);
                var serializer = new DataContractJsonSerializer(typeof(DatabaseDocument));
                document = (DatabaseDocument)serializer.ReadObject(stream);
            }
            catch (FileNotFoundException e)
            {
                throw Error(DataErrorCode.MissingFile, "사전 링크 DB가 없습니다", e);
            }
            catch (DirectoryNotFoundException e)
            {
                throw Error(DataErrorCode.MissingFile, "사전 링크 DB 폴더가 없습니다", e);
            }
            catch (SerializationException e)
            {
                throw Error(DataErrorCode.InvalidFormat, $"JSON을 읽을 수 없습니다 ({e.Message})", e);
            }

            if (document == null || document.format != "WordRoomLinkDatabase")
                throw Error(DataErrorCode.InvalidFormat, "format이 WordRoomLinkDatabase여야 합니다");
            if (document.schemaVersion != 1)
                throw Error(DataErrorCode.UnsupportedSchema, $"지원하지 않는 schemaVersion: {document.schemaVersion}");
            if (document.packageId != dataset.PackageId)
                throw Error(DataErrorCode.PackageIdMismatch, $"사전과 링크 DB의 packageId가 다릅니다 (사전: {dataset.PackageId}, DB: {document.packageId})");
            if (document.offsetUnit != "utf16" || document.textBasis != "ShelfLinkText.DisplayDefinition")
                throw Error(DataErrorCode.InvalidFormat, "클릭 범위 기준은 ShelfLinkText.DisplayDefinition의 UTF-16이어야 합니다");
            if (document.senses == null)
                throw Error(DataErrorCode.InvalidFormat, "senses 배열이 없습니다");

            var index = new Dictionary<string, SenseLinks>(dataset.SenseCount, StringComparer.Ordinal);
            foreach (var sense in document.senses)
            {
                cancel.ThrowIfCancellationRequested();
                if (sense == null || !dataset.TryGetSenseRow(sense.senseId, out var row))
                    throw Error(DataErrorCode.InvalidReference, $"사전에서 뜻풀이를 찾을 수 없습니다 (senseId: {sense?.senseId})");
                if (index.ContainsKey(sense.senseId))
                    throw Error(DataErrorCode.DuplicateEntry, $"senseId {sense.senseId}의 링크 목록이 중복됩니다");
                if (sense.links == null)
                    throw Error(DataErrorCode.InvalidFormat, $"senseId {sense.senseId}에 links 배열이 없습니다");

                var definition = ShelfLinkText.DisplayDefinition(dataset.GetSense(row).Definition) ?? "";
                index.Add(sense.senseId, new SenseLinks(definition, ValidateLinks(sense, definition, dataset)));
            }
            if (index.Count != dataset.SenseCount)
            {
                for (var row = 0; row < dataset.SenseCount; row++)
                {
                    var id = dataset.GetSenseId(row);
                    if (!index.ContainsKey(id))
                        throw Error(DataErrorCode.CountMismatch, $"senseId {id}의 링크 목록이 누락됐습니다 ({index.Count:N0}/{dataset.SenseCount:N0})");
                }
            }
            return new ShelfLinkDatabase(index);
        }

        static IReadOnlyList<ShelfTextPart> ValidateLinks(SenseDocument sense, string definition, WordRoomDataset dataset)
        {
            if (sense.links.Length == 0) return Array.Empty<ShelfTextPart>();
            var parts = new ShelfTextPart[sense.links.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var link = sense.links[i];
                if (link == null || link.text == null || link.start < 0 || link.length <= 0 ||
                    link.length != link.text.Length || link.start > definition.Length - link.length ||
                    char.IsLowSurrogate(definition[link.start]) ||
                    (link.start + link.length < definition.Length && char.IsLowSurrogate(definition[link.start + link.length])) ||
                    string.CompareOrdinal(definition, link.start, link.text, 0, link.length) != 0)
                    throw Error(DataErrorCode.InvalidValue, $"senseId {sense.senseId}, links[{i}]의 클릭 범위 또는 text가 본문과 다릅니다");
                if (!dataset.TryGetWordRank(link.target, out var rank) || dataset.GetWord(rank) != link.target)
                    throw Error(DataErrorCode.InvalidReference, $"senseId {sense.senseId}, links[{i}]의 목적지 '{link.target}'가 사전에 없습니다");
                parts[i] = new ShelfTextPart(link.text, link.start, dataset.GetWord(rank));
            }

            Array.Sort(parts, (a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < parts.Length; i++)
                if (parts[i - 1].Start + parts[i - 1].Length > parts[i].Start)
                    throw Error(DataErrorCode.InvalidValue, $"senseId {sense.senseId}의 클릭 범위가 겹칩니다 (위치 {parts[i].Start})");
            return parts;
        }

        static WordRoomDataException Error(DataErrorCode code, string message, Exception inner = null) =>
            new WordRoomDataException(code, FileName, message, inner);

        // Required members let the parser distinguish an omitted start from a legitimate span at offset zero.
#pragma warning disable 0649
        [DataContract]
        sealed class DatabaseDocument
        {
            [DataMember(IsRequired = true)] public string format;
            [DataMember(IsRequired = true)] public int schemaVersion;
            [DataMember(IsRequired = true)] public string packageId;
            [DataMember(IsRequired = true)] public string offsetUnit;
            [DataMember(IsRequired = true)] public string textBasis;
            [DataMember(IsRequired = true)] public SenseDocument[] senses;
        }

        [DataContract]
        sealed class SenseDocument
        {
            [DataMember(IsRequired = true)] public string senseId;
            [DataMember(IsRequired = true)] public LinkDocument[] links;
        }

        [DataContract]
        sealed class LinkDocument
        {
            [DataMember(IsRequired = true)] public int start;
            [DataMember(IsRequired = true)] public int length;
            [DataMember(IsRequired = true)] public string text;
            [DataMember(IsRequired = true)] public string target;
        }
#pragma warning restore 0649
    }
}
