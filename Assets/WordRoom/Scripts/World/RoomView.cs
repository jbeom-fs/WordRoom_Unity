using System.Collections.Generic;
using TMPro;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// One pooled room instance bound to a coordinate. Added to instances at runtime (the prefab asset is not
    /// changed). Holds no logical data of its own: the word comes from RoomMap and stays there when returned.
    /// The four wall sign texts (TextMeshPro under Wall_*/WordTag/Word) are found once in Init and cleared on return.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomView : MonoBehaviour
    {
        const string Ungenerated = "미생성";

        /// <summary>Sign paths in the room prefab, indexed by Direction (상 +Z, 하 -Z, 좌 -X, 우 +X).</summary>
        static readonly string[] SignPaths = { "Wall_Up/WordTag/Word", "Wall_Down/WordTag/Word", "Wall_Left/WordTag/Word", "Wall_Right/WordTag/Word" };

        [SerializeField] string boundTo = "";      // Inspector display only

        TMP_Text[] signs;

        public bool Bound { get; private set; }
        public GridPos Position { get; private set; }
        public int Word { get; private set; }

        /// <summary>
        /// The text under each sign path, indexed by Direction; null where missing. Missing directions and paths
        /// are added to `missing` when given.
        /// </summary>
        internal static TMP_Text[] FindSigns(Transform room, List<string> missing)
        {
            var found = new TMP_Text[SignPaths.Length];
            for (var i = 0; i < SignPaths.Length; i++)
            {
                var word = room.Find(SignPaths[i]);
                found[i] = word != null ? word.GetComponentInChildren<TMP_Text>(true) : null;
                if (found[i] == null)
                    missing?.Add($"{Directions.Korean((Direction)i)}: {SignPaths[i]}{(word != null ? " 아래 TextMeshPro 없음" : " 경로 없음")}");
            }
            return found;
        }

        /// <summary>Called once after Instantiate: keeps the sign texts (no name lookups later).</summary>
        internal void Init()
        {
            signs = FindSigns(transform, null);
            ClearSigns();
        }

        /// <summary>Sets position, rotation and scale explicitly on every rent (no leftovers from earlier use).</summary>
        internal void Bind(GridPos p, int word, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = p;
            Word = word;
            Bound = true;
            boundTo = $"{p} · 단어 #{word}";
            name = $"Room {p}";
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
        }

        /// <summary>Bound to `p` and active in the scene (call only on a live instance).</summary>
        internal bool IsUsable(GridPos p) => Bound && Position == p && gameObject.activeInHierarchy;

        /// <summary>Neighbor words indexed by Direction (null = 미생성). Missing sign texts are skipped.</summary>
        internal void SetSigns(IReadOnlyList<string> words)
        {
            for (var i = 0; i < signs.Length; i++)
                if (signs[i] != null) signs[i].text = words[i] ?? Ungenerated;
        }

        internal void Unbind()
        {
            ClearSigns();
            Bound = false;
            boundTo = "";
            name = "Room (pooled)";
        }

        void ClearSigns()
        {
            foreach (var t in signs)
                if (t != null) t.text = "";
        }
    }
}
