using System.Collections.Generic;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.Game
{
    /// <summary>
    /// This device's map drawing for the current session (plain object, owned by MapScreen; no Unity object).
    /// Confirmed: the drawing as decided — in single play by this board itself (it is the authority: Submit applies at
    /// once with the same rules as the coop host), in coop by the host (the board only takes the host's changes and whole
    /// drawings; CoopGameplayFlow attaches it with its sender). Pending (coop): this device's edits sent and not answered
    /// yet, shown over the confirmed drawing as previews, dropped when the host's change, answer or whole drawing covers
    /// their request (a refusal is told), or when CoopGameplayFlow gives them up — never shown as confirmed. Display is
    /// confirmed + pending + the eraser drag in progress; Version grows whenever it changes. Detach (new game, end of the
    /// coop game, another session) drops everything; nothing is saved.
    /// </summary>
    public sealed class MapSketchBoard
    {
        /// <summary>Edits waiting for the host at most; a new one beyond is refused with a note (모두 지우기 never is).</summary>
        public const int MaxPending = 16;

        /// <summary>Sends one edit to the authority; false when it could not be sent.</summary>
        public delegate bool Sender(SketchKind kind, long requestId, float size, float[] points);

        sealed class Pending
        {
            public long RequestId;
            public SketchKind Kind;
            public float Size;
            public float[] Points;
            public double SentAt;
            public SketchStroke Line;          // Add: its preview line (made once, temporary id)
        }

        readonly List<Pending> pending = new List<Pending>();
        readonly List<float[]> erasePreview = new List<float[]>();
        MapSketch confirmed;
        MapSketch display;
        bool stale = true;
        float eraseRadius;
        Sender send;
        long nextRequest;
        int nextTempId = -1;

        public string Session { get; private set; }
        public string LocalPlayer { get; private set; }
        public bool Attached => Session != null;
        /// <summary>Coop: the host decides (edits are sent); false in single play.</summary>
        public bool Remote => send != null;
        /// <summary>Grows whenever Display changes (also on Detach).</summary>
        public int Version { get; private set; }
        public long ConfirmedRevision => confirmed != null ? confirmed.Revision : 0;
        public int PendingCount => pending.Count;
        /// <summary>The id of this device's latest edit in this session (0 = none yet).</summary>
        public long LastRequestId => nextRequest;
        /// <summary>When the oldest unanswered edit was sent (realtime; +∞ when none).</summary>
        public double OldestPendingAt => pending.Count > 0 ? pending[0].SentAt : double.PositiveInfinity;

        /// <summary>The latest note for the user (limits, refusals, sending problems); NoteVersion grows with each.</summary>
        public string Note { get; private set; }
        public bool NoteIsError { get; private set; }
        public int NoteVersion { get; private set; }

        /// <summary>Single play: this board decides. Nothing changes when already attached to the same session that way.</summary>
        public void AttachLocal(string session, string localPlayer)
        {
            if (Attached && send == null && Session == session && LocalPlayer == localPlayer) return;
            Begin(session, localPlayer, null);
        }

        /// <summary>Coop: the host decides; `sender` sends this device's edits. Nothing changes when already attached the same way.</summary>
        public void AttachRemote(string session, string localPlayer, Sender sender)
        {
            if (Attached && send == sender && Session == session && LocalPlayer == localPlayer) return;
            Begin(session, localPlayer, sender);
        }

        void Begin(string session, string localPlayer, Sender sender)
        {
            Detach();
            if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(localPlayer)) return;
            Session = session;
            LocalPlayer = localPlayer;
            send = sender;
            confirmed = new MapSketch(session);
            nextRequest = 0;
            Changed();
        }

        /// <summary>The drawing of this session is gone (new game, end of the coop game): confirmed, pending and previews.</summary>
        public void Detach()
        {
            Session = null;
            LocalPlayer = null;
            send = null;
            confirmed = null;
            display = null;
            pending.Clear();
            erasePreview.Clear();
            Note = null;
            NoteVersion++;
            Changed();
        }

        /// <summary>Confirmed + pending + the eraser drag in progress (null when not attached). Rebuilt only after a change.</summary>
        public MapSketch Display
        {
            get
            {
                if (!Attached) return null;
                if (stale) Rebuild();
                return display;
            }
        }

        void Changed()
        {
            stale = true;
            Version++;
        }

        void Rebuild()
        {
            stale = false;
            display = confirmed.Clone();
            foreach (var p in pending)
                switch (p.Kind)
                {
                    case SketchKind.Add:
                        if (p.Line == null) p.Line = new SketchStroke(nextTempId--, LocalPlayer, p.Size, p.Points);
                        display.PreviewAdd(p.Line);
                        break;
                    case SketchKind.Erase: display.PreviewErase(LocalPlayer, p.Points, p.Size, ref nextTempId); break;
                    case SketchKind.Clear: display.PreviewClear(LocalPlayer); break;
                }
            foreach (var path in erasePreview) display.PreviewErase(LocalPlayer, path, eraseRadius, ref nextTempId);
        }

        public void SetNote(string text, bool error)
        {
            Note = text;
            NoteIsError = error;
            NoteVersion++;
        }

        // ---- this device's edits ----------------------------------------------------------------------------------
        /// <summary>
        /// One edit of this device's own drawing (Add: `size` = width, `points` = the line; Erase: `size` = radius,
        /// `points` = the eraser's path; Clear: no points). Single play: decided now. Coop: sent to the host and shown as
        /// pending. A line over the limits known here is refused at once with a note (the authority checks again).
        /// True when it was applied or sent.
        /// </summary>
        public bool Submit(SketchKind kind, float size, float[] points)
        {
            if (!Attached) return false;
            if (kind == SketchKind.Add && !CanAdd(points.Length / 2, out var why))
            {
                SetNote(why, true);
                return false;
            }
            var id = ++nextRequest;
            if (send == null)
            {
                var outcome = confirmed.Submit(LocalPlayer, id, kind, size, points, out _, out var refusal);
                if (outcome == SketchOutcome.Refused) SetNote(refusal, true);
                if (outcome == SketchOutcome.Changed) Changed();
                return outcome == SketchOutcome.Changed || outcome == SketchOutcome.Unchanged;
            }
            if (kind != SketchKind.Clear && pending.Count >= MaxPending)
            {
                SetNote("그림 변경이 호스트에게 확인되기를 기다리는 중입니다. 잠시 뒤 다시 그리세요", true);
                return false;
            }
            if (!send(kind, id, size, points))
            {
                SetNote("그림 변경을 호스트에게 보내지 못했습니다. 다시 그려 주세요", true);
                return false;
            }
            pending.Add(new Pending { RequestId = id, Kind = kind, Size = size, Points = points, SentAt = Time.realtimeSinceStartupAsDouble });
            Changed();
            return true;
        }

        /// <summary>A new line of `points` points fits the limits of what is shown now (own lines, whole drawing).</summary>
        public bool CanAdd(int points, out string why)
        {
            why = null;
            var d = Display;
            if (d == null) why = "지금은 그릴 수 없습니다";
            else if (d.StrokesOf(LocalPlayer) >= SketchRules.MaxStrokesPerAuthor)
                why = $"한 사람이 남길 수 있는 선({SketchRules.MaxStrokesPerAuthor}개)을 모두 썼습니다. 지우개나 모두 지우기로 줄이세요";
            else if (d.TotalPoints + points > SketchRules.MaxTotalPoints)
                why = $"지도 그림 전체 한도({SketchRules.MaxTotalPoints:N0}점)에 도달했습니다. 지우개나 모두 지우기로 줄이세요";
            return why == null;
        }

        /// <summary>The eraser drag in progress: `path` erased from this device's own lines on the display only (never sent; EndErasePreview drops it).</summary>
        public void PreviewErase(float[] path, float radius)
        {
            if (!Attached) return;
            eraseRadius = radius;
            erasePreview.Add(path);
            if (stale) return;                                             // the coming rebuild applies it
            if (display.PreviewErase(LocalPlayer, path, radius, ref nextTempId)) Version++;
        }

        public void EndErasePreview()
        {
            if (erasePreview.Count == 0) return;
            erasePreview.Clear();
            Changed();
        }

        // ---- the host's results (coop) ----------------------------------------------------------------------------
        /// <summary>A confirmed change: applied in revision order (see MapSketch.Apply); this device's edits it answers are no longer pending.</summary>
        public SketchApply ApplyChange(SketchChange c, out string why)
        {
            why = null;
            if (!Attached || send == null) return SketchApply.Ignored;
            var r = confirmed.Apply(c, out why);
            if (r != SketchApply.Applied) return r;
            if (c.PlayerId == LocalPlayer) DropPending(c.RequestId);    // the host takes one author's edits in order: earlier ones were answered
            Changed();
            return r;
        }

        /// <summary>The host's answer to an edit of this device that changed nothing or was refused (told once).</summary>
        public void ApplyReply(long requestId, bool refused, string reason)
        {
            if (!Attached || send == null || !DropPending(requestId)) return;
            if (refused) SetNote(string.IsNullOrEmpty(reason) ? "호스트가 그림 변경을 받아들이지 않았습니다" : reason, true);
            Changed();
        }

        /// <summary>The host's whole drawing: shown when not older than the confirmed one; edits it already covers are no longer pending.</summary>
        public bool ApplyCopy(MapSketch copy)
        {
            if (!Attached || send == null || copy == null || copy.SessionId != Session || copy.Revision < confirmed.Revision) return false;
            confirmed = copy;
            DropPending(copy.LastRequestOf(LocalPlayer));
            Changed();
            return true;
        }

        /// <summary>
        /// Unanswered edits with a request id up to `upTo` are given up: shown no more (a late confirmation still shows
        /// the host's result). The caller decides from the host's records (or a final timeout) which ones; `why` is told.
        /// </summary>
        public void AbandonPending(long upTo, string why)
        {
            if (pending.RemoveAll(p => p.RequestId <= upTo) == 0) return;
            SetNote(why, true);
            Changed();
        }

        bool DropPending(long upTo)
        {
            var n = pending.RemoveAll(p => p.RequestId <= upTo);
            return n > 0;
        }
    }
}
