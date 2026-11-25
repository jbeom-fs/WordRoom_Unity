using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Reading room dimensions in meters. Room-local frame: center of the floor at the origin (floor top at Y 0),
    /// side 0 toward +Z (the player faces it on arrival), sides numbered clockwise seen from above (side 1 toward +X+Z).
    /// The room is a regular octagon; its apothem (center to inner wall face) grows when one side must hold more
    /// lecterns than fit. Read when a room is built, so a change applies to rooms built afterwards.
    /// </summary>
    [Serializable]
    public sealed class ShelfLayout
    {
        [Header("팔각형 방")]
        [Tooltip("방 중앙에서 벽 안쪽 면까지의 최소 거리. 한 변에 독서대가 많으면 이보다 넓어집니다.")]
        [Min(2.5f)] public float apothem = 4f;
        [Tooltip("벽 높이")]
        [Min(2.5f)] public float wallHeight = 3f;
        [Tooltip("벽 두께")]
        [Min(0.05f)] public float wallThickness = 0.15f;

        [Header("독서대")]
        [Tooltip("벽 안쪽 면에서 독서대 중심까지의 거리(독서대 뒤 보행 여유 포함)")]
        [Min(0.4f)] public float lecternInset = 1f;
        [Tooltip("한 변에 독서대를 나란히 둘 때 중심 사이 간격")]
        [Min(0.9f)] public float lecternSpacing = 1.4f;
        [Tooltip("변 양 끝(모서리)에 남길 보행 여유")]
        [Min(0.2f)] public float cornerMargin = 0.6f;
        [Tooltip("독서대 상판 폭")]
        [Min(0.4f)] public float lecternWidth = 0.8f;
        [Tooltip("독서대 상판 높이(바닥 기준)")]
        [Min(0.6f)] public float lecternHeight = 1.05f;

        [Header("방 표제어")]
        [Tooltip("방 표제어 글자 크기(앞·뒤 벽 윗부분)")]
        [Min(0.3f)] public float titleSize = 5f;
        [Tooltip("방 표제어 중심 높이")]
        [Min(1f)] public float titleHeight = 2.62f;

        [Header("조작")]
        [Tooltip("독서대를 조준할 수 있는 최대 거리(시점에서 m)")]
        [FormerlySerializedAs("interactDistance")]
        [Min(0.5f)] public float interactDistance = 2.5f;
        [Tooltip("독서대 앞 상호작용 범위: 독서대 중심에서 앞쪽(방 중앙 쪽)으로 이 거리 안에 발이 있어야 [E] 읽기가 나타납니다.")]
        [Min(0.5f)] public float readRange = 1.6f;

        /// <summary>Half the interior angle step: an octagon side spans 45°.</summary>
        public const float HalfStep = 22.5f;

        /// <summary>Apothem that fits `perSide` lecterns on one side, at least `apothem`.</summary>
        public float ApothemFor(int perSide)
        {
            var chord = Mathf.Max(1, perSide) * lecternSpacing + 2f * cornerMargin;   // along the lectern line
            var needed = chord * 0.5f / Mathf.Tan(HalfStep * Mathf.Deg2Rad) + lecternInset;
            return Mathf.Max(apothem, needed);
        }
    }

    /// <summary>Optional materials; an empty slot uses the primitive's default material tinted with the fallback color.</summary>
    [Serializable]
    public sealed class ShelfMaterials
    {
        [Tooltip("벽")] public Material wall;
        [Tooltip("바닥")] public Material floor;
        [Tooltip("독서대 나무")] public Material lectern;
        [Tooltip("독서대 장식(테두리)")] [FormerlySerializedAs("handle")] public Material trim;
        [Tooltip("독서대 위 양피지. 비어 있으면 양피지 색으로 칠합니다.")] public Material parchment;

        [Header("글자 색")]
        [Tooltip("벽에 직접 쓰는 방 표제어")] public Color textColor = new Color32(0x2E, 0x24, 0x1B, 0xFF);
        [Tooltip("강조 색(HUD의 목표 도달 표시)")] public Color linkColor = new Color32(0xF2, 0xC4, 0x6D, 0xFF);
    }
}
