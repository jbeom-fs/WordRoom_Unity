using System;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Corridor dimensions in meters (room-local: entrance at Z 0, corridor toward +Z, side walls at ±X).
    /// A definition runs along one wall as a single line: plain text drawn on the wall and word doors, in reading
    /// order. It starts on the left wall at the entrance and reads inward; whatever does not fit in wallLength
    /// continues on the right wall from the corridor end back toward the entrance (both read left to right when
    /// facing the wall). If both walls are not enough, wallLength grows until the definition fits.
    /// Read when a room is built, so a change applies to rooms built afterwards, not to the room already shown.
    /// </summary>
    [Serializable]
    public sealed class ShelfLayout
    {
        [Header("복도")]
        [Tooltip("복도 안쪽 폭(좌·우 벽 안쪽 면 사이)")]
        [Min(1.5f)] public float corridorWidth = 3f;
        [Tooltip("한쪽 벽에 글·문을 배치하는 기준 길이. 왼쪽 벽에 담지 못한 항목부터 오른쪽 벽으로 이어지며, 두 벽으로도 모자라면 이 길이를 늘려 다시 배치합니다.")]
        [Min(2f)] public float wallLength = 9f;
        [Tooltip("입구 벽에서 배치 구간 시작까지의 빈 길이")]
        [Min(0f)] public float entryLength = 1f;
        [Tooltip("배치 구간 끝에서 끝벽(제목)까지의 빈 길이")]
        [Min(0f)] public float endLength = 1f;
        [Tooltip("이웃한 글·문 사이 간격. 원문에 띄어쓰기가 있으면 공백 폭을 더합니다.")]
        [Min(0f)] public float itemGap = 0.12f;
        [Tooltip("벽 높이")]
        [Min(2.5f)] public float wallHeight = 3f;
        [Tooltip("벽 두께")]
        [Min(0.05f)] public float wallThickness = 0.15f;

        [Header("글")]
        [Tooltip("벽 글·문 단어의 중심 높이(바닥 기준). 눈높이 1.6m 부근.")]
        [Min(0.5f)] public float textCenter = 1.6f;
        [Tooltip("벽 글·문 단어 글자 크기(TextMeshPro 3D). 문 단어가 문 폭보다 길면 1까지 줄입니다.")]
        [Min(0.3f)] public float textSize = 2f;

        [Header("단어 문 = 통로")]
        [Tooltip("문 폭 = 연결 통로·다음 방 입구의 폭. 복도 폭 −0.2m를 넘지 않게 줄여 씁니다. 열린 문은 같은 폭만큼 벽 속(읽는 방향 쪽)으로 들어가므로 다음 문은 그 수납 공간 뒤에 놓입니다.")]
        [Min(0.9f)] public float doorWidth = 1.4f;
        [Tooltip("통로(문) 높이. 플레이어 키 1.8m보다 높게.")]
        [Min(2f)] public float passageHeight = 2.1f;
        [Tooltip("손잡이 높이(바닥 기준). 문 단어 바로 아래.")]
        [Min(0.5f)] public float handleHeight = 1.15f;

        [Header("통로")]
        [Tooltip("현재 방 벽과 다음 방 입구 사이 연결 통로 길이")]
        [Min(0.3f)] public float tunnelLength = 1.5f;
        [Tooltip("손잡이를 당긴 뒤 문이 벽 속으로 밀려 열리기까지의 시간(초, 손잡이 동작 포함)")]
        [Min(0f)] public float openSeconds = 0.8f;
        [Tooltip("다음 방 입구 경계에서 이만큼 안쪽(m)으로 발이 들어가면 입장으로 판정합니다.")]
        [Min(0f)] public float entryMargin = 0.15f;

        [Header("동음이의어 갈림길")]
        [Tooltip("갈림길 홀 깊이(입구 벽에서 갈래 입구 벽까지)")]
        [Min(1.5f)] public float hallDepth = 2.5f;
        [Tooltip("이웃한 갈래 목적지 방 사이 간격")]
        [Min(0.1f)] public float branchGap = 0.3f;
        [Tooltip("갈래 안내 글자 크기. 갈래 폭에 들어가지 않으면 0.6까지 줄입니다.")]
        [Min(0.3f)] public float forkLabelSize = 1.4f;
        [Tooltip("갈래 안내 글 중심 높이(갈래 입구 위 벽)")]
        [Min(0.5f)] public float forkLabelHeight = 2.5f;

        [Header("끝벽 제목")]
        [Tooltip("제목 글자 크기")]
        [Min(0.1f)] public float titleSize = 6f;
        [Tooltip("제목 패널 두께")]
        [Min(0.01f)] public float panelDepth = 0.04f;

        [Header("조작")]
        [Tooltip("손잡이를 조준·당길 수 있는 최대 거리(시점에서 m)")]
        [Min(0.5f)] public float interactDistance = 2.5f;
        [Tooltip("시작 방에서 입구 벽으로부터 떨어진 시작 위치(m)")]
        [Min(0.4f)] public float spawnDepth = 0.7f;

        /// <summary>Door / passage width actually used (fits the next room's entrance).</summary>
        public float DoorWidth => Mathf.Max(0.9f, Mathf.Min(doorWidth, corridorWidth - 0.2f));

        /// <summary>An open door slides by its width plus this into the wall.</summary>
        public const float SlideClearance = 0.02f;

        /// <summary>Solid wall kept after a stored door before the next door's opening.</summary>
        public const float PocketWall = 0.1f;

        /// <summary>Wall length needed beside a door to store it when open (after its own edge, in reading direction).</summary>
        public float PocketLength => DoorWidth + SlideClearance + PocketWall;
    }

    /// <summary>Optional materials; an empty slot uses the primitive's default material tinted with the fallback color.</summary>
    [Serializable]
    public sealed class ShelfMaterials
    {
        [Tooltip("벽")] public Material wall;
        [Tooltip("바닥")] public Material floor;
        [Tooltip("제목 패널")] public Material panel;
        [Tooltip("손잡이")] public Material handle;
        [Tooltip("통로 문")] public Material door;

        [Header("글자 색")]
        [Tooltip("벽 글(벽 재질 위에 바로 그려지므로 벽과 대비되는 색)")] public Color textColor = new Color32(0x2E, 0x24, 0x1B, 0xFF);
        [Tooltip("문 단어(이동 링크, 문 재질 위)")] public Color linkColor = new Color32(0xF2, 0xC4, 0x6D, 0xFF);
        [Tooltip("끝벽 제목(제목 패널 위)")] public Color titleColor = new Color32(0xF1, 0xE6, 0xCF, 0xFF);
    }
}
