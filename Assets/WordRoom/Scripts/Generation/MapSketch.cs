using System;
using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>
    /// Limits and styles of the map drawing, in map units (one room spacing = 1; GridPos (x, y) is the room center).
    /// Point limits: MaxStrokePoints per stroke, MaxStrokesPerAuthor per author, MaxTotalPoints for the whole session —
    /// with 4 participants the whole drawing stays below 16,384 points (128 KiB of coordinates) in one message.
    /// </summary>
    public static class SketchRules
    {
        public const int MaxStrokePoints = 256;
        public const int MaxStrokesPerAuthor = 128;
        public const int MaxTotalPoints = 16384;
        /// <summary>Authors a whole state may name (every participant of a coop game at most).</summary>
        public const int MaxAuthors = 8;

        /// <summary>Default line width (map units): about 5 canvas units at the default 120-unit cell.</summary>
        public const float StrokeWidth = 0.04f;
        public const float MinStrokeWidth = 0.01f;
        public const float MaxStrokeWidth = 0.2f;

        /// <summary>Default eraser radius (map units): about 14 canvas units at the default cell.</summary>
        public const float EraserRadius = 0.12f;
        public const float MinEraserRadius = 0.02f;
        public const float MaxEraserRadius = 0.5f;

        /// <summary>Largest coordinate a point may have (rooms lie within ±100,000 map units; the empty space around them may be drawn on too).</summary>
        public const float MaxCoordinate = 100001f;

        /// <summary>A piece left by the eraser shorter than this (map units) is dropped instead of kept as a sliver.</summary>
        public const float MinPieceLength = 0.0005f;
    }

    public enum SketchKind
    {
        Add = 1,
        Erase = 2,
        Clear = 3,
    }

    /// <summary>What an authority did with one edit request.</summary>
    public enum SketchOutcome
    {
        /// <summary>The drawing changed: one new revision (SketchChange).</summary>
        Changed,
        /// <summary>Valid, but nothing changed (an eraser that touched none of the author's lines, nothing to clear).</summary>
        Unchanged,
        /// <summary>Not applied (invalid, over a limit); nothing changed.</summary>
        Refused,
        /// <summary>The same or an older request of this author again (late or repeated): ignored, never applied twice.</summary>
        Duplicate,
    }

    public enum SketchApply
    {
        Applied,
        /// <summary>Already shown (an older or repeated revision) or of another session: nothing changes.</summary>
        Ignored,
        /// <summary>A revision is missing, or the change does not fit this state: the whole drawing is needed.</summary>
        Gap,
    }

    /// <summary>
    /// One line of the drawing (immutable): id (unique in its session, given by the authority), author PlayerId, width
    /// (map units) and its points (x, y pairs in map units). One point is a dot.
    /// </summary>
    public sealed class SketchStroke
    {
        readonly float[] points;

        public int Id { get; }
        public string Author { get; }
        public float Width { get; }
        public int Count => points.Length / 2;
        public float MinX { get; }
        public float MinY { get; }
        public float MaxX { get; }
        public float MaxY { get; }

        /// <summary>The points are copied (x0, y0, x1, y1, …).</summary>
        public SketchStroke(int id, string author, float width, float[] xy) : this(id, author, width, (float[])xy.Clone(), true) { }

        SketchStroke(int id, string author, float width, float[] owned, bool _)
        {
            if (owned.Length < 2 || owned.Length % 2 != 0) throw new ArgumentException("획의 점 배열 길이");
            Id = id;
            Author = author;
            Width = width;
            points = owned;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (var i = 0; i < owned.Length; i += 2)
            {
                minX = Math.Min(minX, owned[i]);
                maxX = Math.Max(maxX, owned[i]);
                minY = Math.Min(minY, owned[i + 1]);
                maxY = Math.Max(maxY, owned[i + 1]);
            }
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        internal static SketchStroke Own(int id, string author, float width, float[] owned) => new SketchStroke(id, author, width, owned, true);

        public float X(int i) => points[2 * i];
        public float Y(int i) => points[2 * i + 1];

        /// <summary>Every point in one place: drawn as a dot.</summary>
        public bool IsDot => MaxX - MinX <= 0f && MaxY - MinY <= 0f;

        /// <summary>The points appended to `target` (x, y pairs).</summary>
        public void CopyTo(List<float> target) => target.AddRange(points);
    }

    /// <summary>One confirmed revision of the drawing: who, which request, the lines removed (ids) and added (new ids).</summary>
    public sealed class SketchChange
    {
        public string SessionId;
        public long Revision;
        public string PlayerId;
        public long RequestId;
        public SketchKind Kind;
        public int[] Removed = new int[0];
        public SketchStroke[] Added = new SketchStroke[0];
    }

    /// <summary>
    /// The map drawing of one session (no Unity type; nothing of it touches rooms, visits, generation or its random
    /// numbers). Lines are kept in the order they were confirmed (also the drawing order); every author edits only its
    /// own lines. Used three ways:
    /// as the authority (single play, the coop host) — Submit checks one request of an author (its id must be newer
    /// than the author's last one, so a repeated or late request is never applied twice and an add sent before a 모두
    /// 지우기 never comes back), applies it whole or not at all and returns the change with the ids it gave;
    /// as a replica (every coop display) — Apply takes the authority's changes in revision order, a missing one is a Gap;
    /// as a preview (a clone of either) — the Preview* methods show a device's own unconfirmed edits with temporary ids,
    /// never a revision.
    /// The eraser removes from the author's lines every part within its radius plus half the line width of the eraser's
    /// whole path (segment by segment, so a fast drag leaves no gap); a line cut in the middle becomes separate pieces
    /// (author and width kept, new ids, appended), never joined across the erased part. Pieces count against the limits;
    /// an erase that would exceed them changes nothing.
    /// </summary>
    public sealed class MapSketch
    {
        readonly List<SketchStroke> strokes = new List<SketchStroke>();
        readonly Dictionary<int, SketchStroke> byId = new Dictionary<int, SketchStroke>();
        readonly Dictionary<string, int> countOf = new Dictionary<string, int>();
        readonly Dictionary<string, long> lastRequest = new Dictionary<string, long>();

        public string SessionId { get; }
        /// <summary>Confirmed revisions so far (0 = an empty drawing; previews never change it).</summary>
        public long Revision { get; private set; }
        /// <summary>The next id the authority gives (larger than every confirmed id).</summary>
        public int NextId { get; private set; } = 1;
        public int TotalPoints { get; private set; }
        public IReadOnlyList<SketchStroke> Strokes => strokes;

        public MapSketch(string sessionId)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public int StrokesOf(string author) => author != null && countOf.TryGetValue(author, out var n) ? n : 0;

        /// <summary>The latest request of `author` the authority took (0 = none).</summary>
        public long LastRequestOf(string author) => author != null && lastRequest.TryGetValue(author, out var r) ? r : 0;

        public IEnumerable<KeyValuePair<string, long>> LastRequests => lastRequest;

        public bool TryGet(int id, out SketchStroke stroke) => byId.TryGetValue(id, out stroke);

        /// <summary>An independent copy (lines are immutable and shared).</summary>
        public MapSketch Clone()
        {
            var c = new MapSketch(SessionId) { Revision = Revision, NextId = NextId, TotalPoints = TotalPoints };
            c.strokes.AddRange(strokes);
            foreach (var kv in byId) c.byId.Add(kv.Key, kv.Value);
            foreach (var kv in countOf) c.countOf.Add(kv.Key, kv.Value);
            foreach (var kv in lastRequest) c.lastRequest.Add(kv.Key, kv.Value);
            return c;
        }

        // ---- authority ----------------------------------------------------------------------------------------
        /// <summary>
        /// One request of `author` (its PlayerId as the authority knows it, never a value of the request): Add a line of
        /// `points` with width `size`; Erase along the path `points` with radius `size`; Clear every line of the author.
        /// The request id must be newer than the author's last one (else Duplicate); it is taken whatever the outcome.
        /// Changed returns the change (one new revision); Unchanged, Refused and Duplicate change nothing.
        /// </summary>
        public SketchOutcome Submit(string author, long requestId, SketchKind kind, float size, float[] points, out SketchChange change, out string refusal)
        {
            change = null;
            refusal = null;
            if (string.IsNullOrEmpty(author) || requestId < 1) return Refuse("잘못된 그림 요청", out refusal);
            if (requestId <= LastRequestOf(author)) return SketchOutcome.Duplicate;
            lastRequest[author] = requestId;
            switch (kind)
            {
                case SketchKind.Add:
                {
                    if (!ValidWidth(size)) return Refuse($"선 굵기가 허용 범위({SketchRules.MinStrokeWidth}~{SketchRules.MaxStrokeWidth})를 벗어났습니다", out refusal);
                    if (!ValidPoints(points, out var why)) return Refuse(why, out refusal);
                    var n = points.Length / 2;
                    if (StrokesOf(author) >= SketchRules.MaxStrokesPerAuthor)
                        return Refuse($"한 사람이 남길 수 있는 선({SketchRules.MaxStrokesPerAuthor}개)을 모두 썼습니다. 지우개나 모두 지우기로 줄이세요", out refusal);
                    if (TotalPoints + n > SketchRules.MaxTotalPoints)
                        return Refuse($"지도 그림 전체 한도({SketchRules.MaxTotalPoints:N0}점)에 도달했습니다. 지우개나 모두 지우기로 줄이세요", out refusal);
                    var s = new SketchStroke(NextId, author, size, points);
                    change = Confirm(author, requestId, kind, new int[0], new[] { s });
                    return SketchOutcome.Changed;
                }
                case SketchKind.Erase:
                {
                    if (!ValidRadius(size)) return Refuse($"지우개 반지름이 허용 범위({SketchRules.MinEraserRadius}~{SketchRules.MaxEraserRadius})를 벗어났습니다", out refusal);
                    if (!ValidPoints(points, out var why)) return Refuse(why, out refusal);
                    var removed = new List<int>();
                    var pieces = new List<KeyValuePair<SketchStroke, float[]>>();
                    var removedPoints = 0;
                    var addedPoints = 0;
                    var cut = new List<float[]>();
                    foreach (var s in strokes)
                    {
                        if (s.Author != author) continue;                   // other authors' lines are never touched
                        cut.Clear();
                        if (!SketchGeometry.Cut(s, points, size, cut)) continue;
                        removed.Add(s.Id);
                        removedPoints += s.Count;
                        foreach (var p in cut)
                        {
                            pieces.Add(new KeyValuePair<SketchStroke, float[]>(s, p));
                            addedPoints += p.Length / 2;
                        }
                    }
                    if (removed.Count == 0) return SketchOutcome.Unchanged;
                    if (StrokesOf(author) - removed.Count + pieces.Count > SketchRules.MaxStrokesPerAuthor ||
                        TotalPoints - removedPoints + addedPoints > SketchRules.MaxTotalPoints)
                        return Refuse("지운 뒤 남는 조각이 그림 한도를 넘어 이번 지우기를 적용하지 않았습니다. 선을 더 넓게 지우거나 모두 지우기를 쓰세요", out refusal);
                    var added = new SketchStroke[pieces.Count];
                    for (var i = 0; i < added.Length; i++) added[i] = SketchStroke.Own(NextId + i, author, pieces[i].Key.Width, pieces[i].Value);
                    change = Confirm(author, requestId, kind, removed.ToArray(), added);
                    return SketchOutcome.Changed;
                }
                case SketchKind.Clear:
                {
                    var removed = new List<int>();
                    foreach (var s in strokes)
                        if (s.Author == author) removed.Add(s.Id);
                    if (removed.Count == 0) return SketchOutcome.Unchanged;
                    change = Confirm(author, requestId, kind, removed.ToArray(), new SketchStroke[0]);   // never refused for the limits: it only frees
                    return SketchOutcome.Changed;
                }
                default:
                    return Refuse("알 수 없는 그림 요청", out refusal);
            }
        }

        static SketchOutcome Refuse(string why, out string refusal)
        {
            refusal = why;
            return SketchOutcome.Refused;
        }

        SketchChange Confirm(string author, long requestId, SketchKind kind, int[] removed, SketchStroke[] added)
        {
            var c = new SketchChange { SessionId = SessionId, Revision = Revision + 1, PlayerId = author, RequestId = requestId, Kind = kind, Removed = removed, Added = added };
            Put(c);
            return c;
        }

        void Put(SketchChange c)
        {
            foreach (var id in c.Removed) Remove(id);
            foreach (var s in c.Added)
            {
                Add(s);
                NextId = Math.Max(NextId, s.Id + 1);
            }
            Revision = c.Revision;
            if (c.RequestId > LastRequestOf(c.PlayerId)) lastRequest[c.PlayerId] = c.RequestId;
        }

        void Add(SketchStroke s)
        {
            strokes.Add(s);
            byId.Add(s.Id, s);
            countOf[s.Author] = StrokesOf(s.Author) + 1;
            TotalPoints += s.Count;
        }

        void Remove(int id)
        {
            if (!byId.TryGetValue(id, out var s)) return;
            byId.Remove(id);
            strokes.Remove(s);
            var n = StrokesOf(s.Author) - 1;
            if (n > 0) countOf[s.Author] = n;
            else countOf.Remove(s.Author);
            TotalPoints -= s.Count;
        }

        public static bool ValidWidth(float w) => IsFinite(w) && w >= SketchRules.MinStrokeWidth && w <= SketchRules.MaxStrokeWidth;

        public static bool ValidRadius(float r) => IsFinite(r) && r >= SketchRules.MinEraserRadius && r <= SketchRules.MaxEraserRadius;

        /// <summary>1..MaxStrokePoints finite points within MaxCoordinate.</summary>
        public static bool ValidPoints(float[] xy, out string why)
        {
            why = null;
            if (xy == null || xy.Length < 2 || xy.Length % 2 != 0 || xy.Length > 2 * SketchRules.MaxStrokePoints)
            {
                why = $"그림 점 수(최대 {SketchRules.MaxStrokePoints})";
                return false;
            }
            foreach (var v in xy)
                if (!IsFinite(v) || Math.Abs(v) > SketchRules.MaxCoordinate)
                {
                    why = "그림 좌표 범위";
                    return false;
                }
            return true;
        }

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        // ---- replica ------------------------------------------------------------------------------------------
        /// <summary>
        /// The authority's change: applied only as the next revision of this session and only when it fits (removed lines
        /// exist and are the author's, added ids are new, the limits hold); otherwise nothing changes (Ignored or Gap).
        /// </summary>
        public SketchApply Apply(SketchChange c, out string why)
        {
            why = null;
            if (c == null || c.SessionId != SessionId)
            {
                why = "다른 세션의 그림 변경";
                return SketchApply.Ignored;
            }
            if (c.Revision <= Revision) return SketchApply.Ignored;
            if (c.Revision != Revision + 1)
            {
                why = $"그림 순번 {c.Revision}(현재 {Revision})";
                return SketchApply.Gap;
            }
            var removedPoints = 0;
            var seen = new HashSet<int>();
            foreach (var id in c.Removed)
            {
                if (!seen.Add(id) || !byId.TryGetValue(id, out var s) || s.Author != c.PlayerId)
                {
                    why = $"지울 선 {id}가 이 상태와 맞지 않음";
                    return SketchApply.Gap;
                }
                removedPoints += s.Count;
            }
            var addedPoints = 0;
            var fresh = new HashSet<int>();
            foreach (var s in c.Added)
            {
                if (s.Id < NextId || s.Author != c.PlayerId || byId.ContainsKey(s.Id) || !fresh.Add(s.Id) || !ValidWidth(s.Width) || s.Count > SketchRules.MaxStrokePoints)
                {
                    why = $"추가할 선 {s.Id}가 이 상태와 맞지 않음";
                    return SketchApply.Gap;
                }
                addedPoints += s.Count;
            }
            if (StrokesOf(c.PlayerId) - c.Removed.Length + c.Added.Length > SketchRules.MaxStrokesPerAuthor ||
                TotalPoints - removedPoints + addedPoints > SketchRules.MaxTotalPoints)
            {
                why = "그림 한도를 넘는 변경";
                return SketchApply.Gap;
            }
            Put(c);
            return SketchApply.Applied;
        }

        /// <summary>
        /// A whole drawing as sent by the authority, checked (ids positive, unique and ascending, below `nextId`; authors
        /// within MaxAuthors; every line valid; the limits) into a new state; null with the reason otherwise.
        /// </summary>
        public static MapSketch Restore(string sessionId, long revision, int nextId, IList<SketchStroke> lines, IList<KeyValuePair<string, long>> lastRequests, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sessionId) || revision < 0 || nextId < 1)
            {
                error = "그림 상태의 세션·순번";
                return null;
            }
            var m = new MapSketch(sessionId) { Revision = revision, NextId = nextId };
            var authors = new HashSet<string>();
            var lastId = 0;
            foreach (var s in lines)
            {
                if (s.Id <= lastId || s.Id >= nextId || string.IsNullOrEmpty(s.Author) || !ValidWidth(s.Width) || s.Count > SketchRules.MaxStrokePoints)
                {
                    error = $"그림 상태의 선 {s.Id}";
                    return null;
                }
                lastId = s.Id;
                authors.Add(s.Author);
                m.Add(s);
                if (m.StrokesOf(s.Author) > SketchRules.MaxStrokesPerAuthor || m.TotalPoints > SketchRules.MaxTotalPoints)
                {
                    error = "그림 상태가 한도를 넘음";
                    return null;
                }
            }
            foreach (var kv in lastRequests)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value < 0 || m.lastRequest.ContainsKey(kv.Key))
                {
                    error = "그림 상태의 요청 기록";
                    return null;
                }
                authors.Add(kv.Key);
                m.lastRequest.Add(kv.Key, kv.Value);
            }
            if (authors.Count > SketchRules.MaxAuthors)
            {
                error = "그림 상태의 작성자 수";
                return null;
            }
            return m;
        }

        // ---- preview (this device's own unconfirmed edits; never a revision) ------------------------------------
        /// <summary>Shows a line not confirmed yet (`id` negative, given by the caller).</summary>
        public void PreviewAdd(SketchStroke s)
        {
            if (byId.ContainsKey(s.Id)) return;
            Add(s);
        }

        /// <summary>Shows an erase not confirmed yet on the author's lines (pieces get ids from `nextTempId` downward). True when something changed.</summary>
        public bool PreviewErase(string author, float[] path, float radius, ref int nextTempId)
        {
            var hit = new List<SketchStroke>();
            var cut = new List<float[]>();
            var pieces = new List<SketchStroke>();
            foreach (var s in strokes)
            {
                if (s.Author != author) continue;
                cut.Clear();
                if (!SketchGeometry.Cut(s, path, radius, cut)) continue;
                hit.Add(s);
                foreach (var p in cut) pieces.Add(SketchStroke.Own(nextTempId--, author, s.Width, p));
            }
            foreach (var s in hit) Remove(s.Id);
            foreach (var s in pieces) Add(s);
            return hit.Count > 0;
        }

        /// <summary>Shows a 모두 지우기 not confirmed yet. True when something changed.</summary>
        public bool PreviewClear(string author)
        {
            var mine = new List<int>();
            foreach (var s in strokes)
                if (s.Author == author) mine.Add(s.Id);
            foreach (var id in mine) Remove(id);
            return mine.Count > 0;
        }
    }

    /// <summary>The eraser's cut of one line (plain geometry in map units).</summary>
    public static class SketchGeometry
    {
        const float Eps = 1e-6f;

        /// <summary>
        /// Whether any part of `s` lies within `radius` + half its width of the eraser path `path` (x, y pairs; one point
        /// is a disc). When it does, `kept` gets the remaining pieces (each at least 2 points and MinPieceLength long,
        /// at most MaxStrokePoints; a dot that is touched leaves nothing).
        /// </summary>
        public static bool Cut(SketchStroke s, float[] path, float radius, List<float[]> kept)
        {
            var reach = radius + s.Width * 0.5f;
            var pathCount = path.Length / 2;
            if (pathCount == 0) return false;
            float pMinX = float.MaxValue, pMinY = float.MaxValue, pMaxX = float.MinValue, pMaxY = float.MinValue;
            for (var j = 0; j < pathCount; j++)
            {
                pMinX = Math.Min(pMinX, path[2 * j]);
                pMaxX = Math.Max(pMaxX, path[2 * j]);
                pMinY = Math.Min(pMinY, path[2 * j + 1]);
                pMaxY = Math.Max(pMaxY, path[2 * j + 1]);
            }
            if (s.MaxX < pMinX - reach || s.MinX > pMaxX + reach || s.MaxY < pMinY - reach || s.MinY > pMaxY + reach) return false;

            if (s.Count == 1 || s.IsDot)
            {
                for (var j = 0; j < Math.Max(1, pathCount - 1); j++)
                {
                    Segment(path, j, pathCount, out var qx0, out var qy0, out var qx1, out var qy1);
                    if (PointSegmentDistanceSq(s.X(0), s.Y(0), qx0, qy0, qx1, qy1) <= reach * reach) return true;   // a dot goes whole
                }
                return false;
            }

            var touched = false;
            var current = new List<float>();
            var erased = new List<(float lo, float hi)>();
            for (var i = 0; i < s.Count - 1; i++)
            {
                float ax = s.X(i), ay = s.Y(i), bx = s.X(i + 1), by = s.Y(i + 1);
                erased.Clear();
                float sMinX = Math.Min(ax, bx) - reach, sMaxX = Math.Max(ax, bx) + reach, sMinY = Math.Min(ay, by) - reach, sMaxY = Math.Max(ay, by) + reach;
                for (var j = 0; j < Math.Max(1, pathCount - 1); j++)
                {
                    Segment(path, j, pathCount, out var qx0, out var qy0, out var qx1, out var qy1);
                    if (Math.Max(qx0, qx1) < sMinX || Math.Min(qx0, qx1) > sMaxX || Math.Max(qy0, qy1) < sMinY || Math.Min(qy0, qy1) > sMaxY) continue;
                    if (Hit(ax, ay, bx, by, qx0, qy0, qx1, qy1, reach, out var t0, out var t1)) erased.Add((t0, t1));
                }
                if (erased.Count == 0)
                {
                    Keep(current, kept, ax, ay, bx, by, 0f, 1f);
                    continue;
                }
                touched = true;
                erased.Sort((p, q) => p.lo.CompareTo(q.lo));
                var t = 0f;
                foreach (var (lo, hi) in erased)
                {
                    if (lo > t + Eps) Keep(current, kept, ax, ay, bx, by, t, lo);
                    else Flush(current, kept);                              // the piece reaching here ends where the erased part starts
                    t = Math.Max(t, hi);
                }
                if (t < 1f - Eps) Keep(current, kept, ax, ay, bx, by, t, 1f);
                else Flush(current, kept);
            }
            Flush(current, kept);
            if (!touched) kept.Clear();
            return touched;
        }

        /// <summary>Segment j of the path (a single point is a zero-length segment).</summary>
        static void Segment(float[] path, int j, int count, out float x0, out float y0, out float x1, out float y1)
        {
            x0 = path[2 * j];
            y0 = path[2 * j + 1];
            var k = count > 1 ? j + 1 : j;
            x1 = path[2 * k];
            y1 = path[2 * k + 1];
        }

        /// <summary>The kept part [k0, k1] of segment a→b: continues the open piece from a, or starts a new one; ends it unless it reaches b.</summary>
        static void Keep(List<float> current, List<float[]> kept, float ax, float ay, float bx, float by, float k0, float k1)
        {
            if (k0 <= Eps)
            {
                if (current.Count == 0)
                {
                    current.Add(ax);
                    current.Add(ay);
                }
            }
            else
            {
                Flush(current, kept);
                current.Add(ax + (bx - ax) * k0);
                current.Add(ay + (by - ay) * k0);
            }
            if (k1 >= 1f - Eps)
            {
                current.Add(bx);
                current.Add(by);
                if (current.Count >= 2 * SketchRules.MaxStrokePoints)          // never longer than a line may be: the rest continues from here
                {
                    Flush(current, kept);
                    current.Add(bx);
                    current.Add(by);
                }
            }
            else
            {
                current.Add(ax + (bx - ax) * k1);
                current.Add(ay + (by - ay) * k1);
                Flush(current, kept);
            }
        }

        static void Flush(List<float> current, List<float[]> kept)
        {
            if (current.Count >= 4)
            {
                var length = 0.0;
                for (var i = 2; i < current.Count; i += 2)
                {
                    var dx = current[i] - current[i - 2];
                    var dy = current[i + 1] - current[i - 1];
                    length += Math.Sqrt(dx * dx + dy * dy);
                }
                if (length >= SketchRules.MinPieceLength) kept.Add(current.ToArray());
            }
            current.Clear();
        }

        /// <summary>
        /// The part t∈[t0, t1] of segment a→b within `r` of segment q0→q1 (the capsule around it: the two end discs and
        /// the band between; a line meets the convex capsule in one interval, so the hull of the three is exact).
        /// </summary>
        static bool Hit(float ax, float ay, float bx, float by, float qx0, float qy0, float qx1, float qy1, float r, out float t0, out float t1)
        {
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            float dx = bx - ax, dy = by - ay;
            if (dx * dx + dy * dy < Eps * Eps)                                 // a zero-length piece of line: erased or not as a point
            {
                t0 = 0f;
                t1 = 1f;
                return PointSegmentDistanceSq(ax, ay, qx0, qy0, qx1, qy1) <= r * r;
            }
            Disc(ax, ay, dx, dy, qx0, qy0, r, ref lo, ref hi);
            Disc(ax, ay, dx, dy, qx1, qy1, r, ref lo, ref hi);
            float ux = qx1 - qx0, uy = qy1 - qy0;
            var length = (float)Math.Sqrt(ux * ux + uy * uy);
            if (length > Eps)
            {
                ux /= length;
                uy /= length;
                float fx = ax - qx0, fy = ay - qy0;
                if (Slab(fx * ux + fy * uy, dx * ux + dy * uy, 0f, length, out var s0, out var s1) &&
                    Slab(-fx * uy + fy * ux, -dx * uy + dy * ux, -r, r, out var h0, out var h1))
                {
                    var a = Math.Max(s0, h0);
                    var b = Math.Min(s1, h1);
                    if (a <= b)
                    {
                        lo = Math.Min(lo, a);
                        hi = Math.Max(hi, b);
                    }
                }
            }
            t0 = Math.Max(0f, lo);
            t1 = Math.Min(1f, hi);
            return t0 <= t1;
        }

        static void Disc(float ax, float ay, float dx, float dy, float cx, float cy, float r, ref float lo, ref float hi)
        {
            float fx = ax - cx, fy = ay - cy;
            var a = dx * dx + dy * dy;
            var b = 2f * (fx * dx + fy * dy);
            var c = fx * fx + fy * fy - r * r;
            var disc = b * b - 4f * a * c;
            if (disc < 0f) return;
            var root = (float)Math.Sqrt(disc);
            lo = Math.Min(lo, (-b - root) / (2f * a));
            hi = Math.Max(hi, (-b + root) / (2f * a));
        }

        /// <summary>t with min ≤ value + slope·t ≤ max.</summary>
        static bool Slab(float value, float slope, float min, float max, out float t0, out float t1)
        {
            if (Math.Abs(slope) < Eps)
            {
                t0 = float.NegativeInfinity;
                t1 = float.PositiveInfinity;
                return value >= min && value <= max;
            }
            var a = (min - value) / slope;
            var b = (max - value) / slope;
            t0 = Math.Min(a, b);
            t1 = Math.Max(a, b);
            return true;
        }

        public static float PointSegmentDistanceSq(float px, float py, float x0, float y0, float x1, float y1)
        {
            float dx = x1 - x0, dy = y1 - y0;
            var l = dx * dx + dy * dy;
            var t = l > 0f ? Math.Max(0f, Math.Min(1f, ((px - x0) * dx + (py - y0) * dy) / l)) : 0f;
            var ex = x0 + t * dx - px;
            var ey = y0 + t * dy - py;
            return ex * ex + ey * ey;
        }
    }
}
