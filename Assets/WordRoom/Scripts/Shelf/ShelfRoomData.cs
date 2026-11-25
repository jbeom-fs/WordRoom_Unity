using System;
using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Link selection of one sense in the prototype: the words of that sense's definition used as links. The text
    /// itself always comes from the dictionary package. A setting with a senseId applies to that sense only; one
    /// without applies to every sense of the word that has no setting of its own. A sense without settings uses every
    /// link candidate of its definition.
    /// </summary>
    [Serializable]
    public sealed class ShelfRoom
    {
        [Tooltip("표제어(사전 패키지의 표기)")]
        public string word;
        [Tooltip("링크 설정을 적용할 뜻의 senseId. 비어 있으면 이 표제어에서 자기 설정이 없는 모든 뜻에 적용합니다.")]
        public string senseId;
        [Tooltip("이동 링크로 선정할 단어(그 뜻풀이에 실제로 나오는 링크 후보 중에서). 비어 있으면 후보 전부를 링크로 씁니다. 한 뜻풀이에서 같은 표제어는 처음 유효한 자리에만 링크를 만들고, 후보가 아닌 단어(한 글자 등)는 일반 글로 남습니다.")]
        public List<string> links = new List<string>();

        public ShelfRoom() { }

        public ShelfRoom(string word, string senseId, params string[] links)
        {
            this.word = word;
            this.senseId = senseId;
            this.links = new List<string>(links);
        }
    }

    /// <summary>
    /// Short title of one lectern (one sense of a headword), shown after the sense number in the "[E] 읽기" hint and on
    /// the reading screen. It applies to the lectern of that senseId only. Lecterns without a setting get a title cut
    /// from their definition.
    /// </summary>
    [Serializable]
    public sealed class ShelfLecternLabel
    {
        [Tooltip("표제어")]
        public string word;
        [Tooltip("짧은 표제를 붙일 뜻의 senseId(그 뜻의 독서대에만 적용)")]
        public string senseId;
        [Tooltip("방 중앙에서 읽을 짧은 뜻 표제. 비어 있으면 그 뜻풀이 끝부분을 씁니다.")]
        public string label;

        public ShelfLecternLabel() { }

        public ShelfLecternLabel(string word, string senseId, string label)
        {
            this.word = word;
            this.senseId = senseId;
            this.label = label;
        }
    }

    /// <summary>
    /// Fixed examples (start headword → goal headword). 바다 → 지구: the 바다 lectern's definition links 지구 once
    /// (first place), one click. 항구's 염전 sense links only 바닷물, whose definition links 바다.
    /// </summary>
    public static class ShelfExample
    {
        public const string HarborWord = "항구";
        public const string HarborSense = "316979";
        public const string SeaWord = "바다";
        public const string SeaSense = "331764";
        public const string EarthWord = "지구";
        public const string PlanetSense = "278681";
        public const string DistrictSense = "480437";

        public static List<ShelfRoom> Rooms() => new List<ShelfRoom>
        {
            new ShelfRoom(HarborWord, HarborSense, "바닷물"),          // 염전에서 판에 댈 바닷물을 받는 웅덩이. (판: 한 글자, 일반 글)
            new ShelfRoom("바닷물", "532114", "바다"),                // 바다에 괴어 있는 짠물.
            new ShelfRoom(SeaWord, SeaSense),                         // every candidate; 지구 twice → first place only
        };

        public static List<ShelfLecternLabel> Labels() => new List<ShelfLecternLabel>
        {
            new ShelfLecternLabel(EarthWord, PlanetSense, "행성"),
            new ShelfLecternLabel(EarthWord, DistrictSense, "땅의 구획"),
            new ShelfLecternLabel(HarborWord, HarborSense, "염전 웅덩이"),
            new ShelfLecternLabel(HarborWord, "439232", "배가 드나드는 곳"),
            new ShelfLecternLabel("해안", "317034", "낮 동안"),
            new ShelfLecternLabel("해안", "317035", "바다와 육지의 경계"),
            new ShelfLecternLabel("해안", "317036", "웃는 얼굴"),
        };
    }

    /// <summary>The sense of a lectern's document: its display definition cut into plain parts and links in reading order.</summary>
    public sealed class ShelfSense
    {
        public string SenseId { get; }
        public string Definition { get; }
        /// <summary>Concatenating every part's Text gives Definition; link parts lead to their Target headword's room.</summary>
        public IReadOnlyList<ShelfTextPart> Parts { get; }

        public ShelfSense(string senseId, string definition, IReadOnlyList<ShelfTextPart> parts)
        {
            SenseId = senseId;
            Definition = definition;
            Parts = parts;
        }
    }

    /// <summary>
    /// What one lectern holds: one sense of the headword that has at least one link, with its dictionary entry
    /// (EntryId), its number within that entry and the short title. The number is the sense's place among the entry's
    /// senses with a definition, fixed before links are filtered, so it stays when other senses of the entry get no
    /// lectern (0 when the entry has a single sense: no number shown).
    /// </summary>
    public sealed class ShelfDocument
    {
        public string Word { get; }
        public string EntryId { get; }
        public string Pos { get; }
        /// <summary>1-based number within the entry, or 0 when the entry has one sense.</summary>
        public int Number { get; }
        public string Label { get; }
        public ShelfSense Sense { get; }

        public ShelfDocument(string word, string entryId, string pos, int number, string label, ShelfSense sense)
        {
            Word = word;
            EntryId = entryId;
            Pos = pos;
            Number = number;
            Label = label;
            Sense = sense;
        }

        /// <summary>The number and short title shown in the hint and on the reading screen ("2. 땅의 구획" or "행성").</summary>
        public string Title => Number > 0 ? $"{Number}. {Label}" : Label;

        public string SenseId => Sense.SenseId;

        public int LinkCount
        {
            get
            {
                var n = 0;
                foreach (var p in Sense.Parts)
                    if (p.IsLink) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// A headword's reading room resolved from the dictionary: one document per sense with at least one link, in
    /// dictionary order (entries in order, senses in order within each: the lectern order). Only the goal room may have
    /// none. Resolved once per headword and kept, so a revisited room has the same lecterns in the same places.
    /// </summary>
    public sealed class ShelfWordRoom
    {
        public string Word { get; }
        public IReadOnlyList<ShelfDocument> Documents { get; }

        public ShelfWordRoom(string word, IReadOnlyList<ShelfDocument> documents)
        {
            Word = word;
            Documents = documents;
        }

        /// <summary>Distinct headwords reachable by a link anywhere in the room (its exits).</summary>
        public int ExitCount
        {
            get
            {
                var set = new HashSet<string>();
                foreach (var d in Documents)
                    foreach (var p in d.Sense.Parts)
                        if (p.IsLink) set.Add(p.Target);
                return set.Count;
            }
        }
    }
}
