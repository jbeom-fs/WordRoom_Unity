using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Furniture spacing and reading controls of the reading room (meters). The room itself is the fixed octagonal
    /// model (ShelfRoomView); its desk ring and the desk and lectern sizes come from the prefabs. Read whenever a room
    /// is furnished, so a change applies from the next room.
    /// </summary>
    [Serializable]
    public sealed class ShelfLayout
    {
        [Header("책상 위 독서대")]
        [Tooltip("상판 양 끝에 남길 여유(독서대 바깥 가장자리까지)")]
        [Min(0f)] public float edgeMargin = 0.06f;
        [Tooltip("한 책상 위 독서대 사이 최소 간격. 이 간격으로도 상판에 다 놓이지 않으면 상판을 넓히고, 그래도 부족하면 독서대를 줄입니다.")]
        [Min(0f)] public float minLecternGap = 0.06f;
        [Tooltip("이웃한 두 책상의 앞 모서리 사이에 남길 최소 보행 폭. 상판을 넓힐 수 있는 최대 폭이 이 값으로 정해집니다(책장 앞 통로는 책상 위치가 고정이라 그대로입니다).")]
        [Min(0.6f)] public float deskWalkway = 0.75f;

        [Header("조작")]
        [Tooltip("독서대를 조준할 수 있는 최대 거리(시점에서 m)")]
        [FormerlySerializedAs("interactDistance")]
        [Min(0.5f)] public float interactDistance = 2.5f;
        [Tooltip("독서대 앞 상호작용 범위: 독서대 중심에서 앞쪽(방 중앙 쪽)으로 이 거리 안에 발이 있어야 [E] 읽기가 나타납니다.")]
        [Min(0.5f)] public float readRange = 1.6f;

        /// <summary>Half the angle of one octagon side seen from the center.</summary>
        public const float HalfStep = 22.5f;

        /// <summary>
        /// Widest desk top that keeps `deskWalkway` between the front corners of neighboring desks (desk centers at
        /// `deskRadius` from the room center, `deskDepth` deep). The front corners bind: the wedge of a side is
        /// narrowest there.
        /// </summary>
        public float MaxTopWidth(float deskRadius, float deskDepth)
        {
            var front = deskRadius - deskDepth * 0.5f;
            var half = HalfStep * Mathf.Deg2Rad;
            return 2f * (front * Mathf.Tan(half) - deskWalkway * 0.5f / Mathf.Cos(half));
        }
    }
}
