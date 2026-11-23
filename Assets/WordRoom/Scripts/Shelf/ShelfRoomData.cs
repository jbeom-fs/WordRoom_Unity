using System;
using System.Collections.Generic;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Settings of one room in the prototype: the headword, the dictionary sense shown in it and the words of that
    /// sense's definition chosen as links. The text itself always comes from the dictionary package. A setting with a
    /// senseId applies to that sense's room only; one without applies to the word's default room. A room without
    /// settings shows its sense with every link candidate (the word's first sense when no sense is given).
    /// </summary>
    [Serializable]
    public sealed class ShelfRoom
    {
        [Tooltip("표제어(사전 패키지의 표기)")]
        public string word;
        [Tooltip("이 방에 보여 줄 뜻의 senseId. 비어 있거나 이 표제어의 뜻이 아니면 첫 뜻을 씁니다. 갈래길 목적지처럼 뜻이 정해진 방에는 senseId가 같은 설정만 적용됩니다.")]
        public string senseId;
        [Tooltip("이동 링크로 선정할 단어(뜻풀이에 실제로 나오는 문 후보 중에서). 비어 있으면 후보 전부를 링크로 씁니다. 같은 표제어는 처음 나온 자리에만 문을 만들고, 후보가 아닌 단어(한 글자 등)는 벽 글로 남습니다. 여기 지정한 senseId는 그 사전 항목의 대표 뜻이 됩니다.")]
        public List<string> links = new List<string>();

        public ShelfRoom() { }

        public ShelfRoom(string word, string senseId, params string[] links)
        {
            this.word = word;
            this.senseId = senseId;
            this.links = new List<string>(links);
        }
    }

    /// <summary>One way of a homonym fork: the sign on its wall and the sense whose room it leads to.</summary>
    [Serializable]
    public sealed class ShelfBranch
    {
        [Tooltip("갈래 벽에 표시할 짧은 뜻 안내. 비어 있으면 뜻풀이 앞부분을 씁니다.")]
        public string label;
        [Tooltip("이 갈래 목적지 방의 senseId(갈림길 표제어의 뜻이어야 합니다)")]
        public string senseId;

        public ShelfBranch() { }

        public ShelfBranch(string label, string senseId)
        {
            this.label = label;
            this.senseId = senseId;
        }
    }

    /// <summary>
    /// Optional hints for a homonym fork. The fork itself comes from the dictionary: one branch per entry (EntryId) of
    /// the headword, and a headword with a single entry links straight to its room. A branch setting here only names
    /// the representative sense of the entry it belongs to (after the room settings) and that branch's sign.
    /// </summary>
    [Serializable]
    public sealed class ShelfFork
    {
        [Tooltip("갈래 설정을 참고할 표제어")]
        public string word;
        [Tooltip("갈래 참고 목록. senseId는 그 뜻이 속한 사전 항목의 대표 뜻, label은 그 갈래의 안내 문구가 됩니다. 갈래 수·순서는 사전 항목 기준이며, 표제어에 속하지 않는 senseId는 경고 후 무시합니다.")]
        public List<ShelfBranch> branches = new List<ShelfBranch>();

        public ShelfFork() { }

        public ShelfFork(string word, params ShelfBranch[] branches)
        {
            this.word = word;
            this.branches = new List<ShelfBranch>(branches);
        }
    }

    /// <summary>
    /// Fixed examples. 1: 항구(염전 sense) → 바닷물 → 바다. 2: 바다 → 지구(행성), through the 지구 fork detected
    /// from its dictionary entries. The fork settings only give the 행성 / 땅의 구획 signs; both examples share them.
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
            new ShelfRoom(HarborWord, HarborSense, "바닷물"),          // 염전에서 판에 댈 바닷물을 받는 웅덩이. (판: 한 글자, 벽 글)
            new ShelfRoom("바닷물", "532114", "바다"),                // 바다에 괴어 있는 짠물.
            new ShelfRoom(SeaWord, SeaSense),                         // every candidate; 지구 twice → one door
        };

        public static List<ShelfFork> Forks() => new List<ShelfFork>
        {
            new ShelfFork(EarthWord,
                new ShelfBranch("태양에서 셋째로 가까운 행성", PlanetSense),
                new ShelfBranch("일정한 기준으로 나눈 땅의 한 구획", DistrictSense)),
        };
    }

    /// <summary>
    /// A room's content resolved from the dictionary: the headword, the fixed sense and its definition cut into
    /// plain-text parts and link parts in reading order (concatenating every part's Text gives the definition).
    /// Rooms are told apart by Key (headword and sense), so two senses of one word are two rooms.
    /// </summary>
    public sealed class ShelfEntry
    {
        public string Word { get; }
        public string SenseId { get; }
        /// <summary>Dictionary entry (homograph) the sense belongs to; senses of one entry are one fork branch.</summary>
        public string EntryId { get; }
        public string Pos { get; }
        public string Definition { get; }
        public IReadOnlyList<ShelfTextPart> Parts { get; }
        public string Key => KeyOf(Word, SenseId);
        /// <summary>Number of word doors (exits) of the room.</summary>
        public int DoorCount
        {
            get
            {
                var n = 0;
                foreach (var p in Parts)
                    if (p.IsLink) n++;
                return n;
            }
        }
        /// <summary>Headword with the sense, for HUD and logs.</summary>
        public string Label => $"{Word}({SenseId})";

        public ShelfEntry(string word, string senseId, string entryId, string pos, string definition, IReadOnlyList<ShelfTextPart> parts)
        {
            Word = word;
            SenseId = senseId;
            EntryId = entryId;
            Pos = pos;
            Definition = definition;
            Parts = parts;
        }

        public static string KeyOf(string word, string senseId) => $"{word}#{senseId}";
    }
}
