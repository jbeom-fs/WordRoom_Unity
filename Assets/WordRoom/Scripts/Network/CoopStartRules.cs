using System.Collections.Generic;

namespace WordRoom.Network
{
    public enum CoopStage
    {
        /// <summary>In the waiting room; no start in progress.</summary>
        Waiting,
        /// <summary>A start was confirmed: loading the game scene and the data, waiting for the host's initial world.</summary>
        Loading,
        /// <summary>The initial world arrived; it is being checked, shown and the player placed.</summary>
        Placing,
        /// <summary>Placement reported done; waiting for the common start.</summary>
        Placed,
        /// <summary>The common start time is known; waiting for it.</summary>
        Counting,
        /// <summary>Started (in the start room only, at this stage).</summary>
        Playing,
        /// <summary>The start was stopped; everyone goes back to the waiting room.</summary>
        Aborted,
        /// <summary>Left the room or disconnected; this object is about to go away.</summary>
        Ended,
    }

    /// <summary>One confirmed start as this device takes part in it (values fixed by the host's start message).</summary>
    public sealed class CoopEntry
    {
        public string Attempt { get; }
        public int Host { get; }
        /// <summary>Participants fixed at the start (ActorNumbers, ascending); nobody is added later.</summary>
        public int[] Actors { get; }
        public int LocalActor { get; }
        public string LocalPlayerId => CoopRules.PlayerIdOf(LocalActor);
        /// <summary>0 쉬움 · 1 보통 · 2 어려움.</summary>
        public int Difficulty { get; }
        public int SettingsRevision { get; }

        public CoopEntry(string attempt, int host, int[] actors, int localActor, int difficulty, int settingsRevision)
        {
            Attempt = attempt;
            Host = host;
            Actors = actors;
            LocalActor = localActor;
            Difficulty = difficulty;
            SettingsRevision = settingsRevision;
        }
    }

    /// <summary>
    /// The start and stop decisions of one start attempt, by its id (no Photon type; testable alone). The host decides
    /// a stop only until it itself has started play; every participant applies the host's valid stop of its current
    /// attempt in any stage of that attempt — also after it already began play a frame earlier — so the frame in which
    /// each device crossed the common start time never splits the result. A stopped attempt is never started again by a
    /// late start message or report, and a second stop of it changes nothing. A host change during an attempt stops it
    /// on every remaining device, whatever its stage or whether it received the start time (no host migration at this
    /// stage); in the plain waiting room it stops nothing. The room is reopened by whoever is the master once no start
    /// is running there and the room is still closed — also a participant that had already stopped and only then became
    /// the master.
    /// </summary>
    public static class CoopStartRules
    {
        /// <summary>The device takes part in a start (loading up to playing).</summary>
        public static bool InStart(CoopStage s) => s >= CoopStage.Loading && s <= CoopStage.Playing;

        /// <summary>The host may still stop its attempt: before the host itself started play.</summary>
        public static bool HostMayStop(CoopStage hostStage) => hostStage >= CoopStage.Loading && hostStage <= CoopStage.Counting;

        /// <summary>A stop of `incoming` applies to this device's `current` attempt (validated sender assumed).</summary>
        public static bool AppliesStop(CoopStage stage, string current, string incoming, ICollection<string> stopped) =>
            current != null && incoming == current && !stopped.Contains(incoming) && InStart(stage);

        /// <summary>The start time of `incoming` is taken (only once, after placement, never for a stopped attempt).</summary>
        public static bool AcceptsStart(CoopStage stage, string current, string incoming, ICollection<string> stopped) =>
            stage == CoopStage.Placed && current != null && incoming == current && !stopped.Contains(incoming);

        /// <summary>A host change stops the attempt in every stage of it (Loading … Playing); not in the waiting room.</summary>
        public static bool StopsOnHostChange(CoopStage stage) => InStart(stage);

        /// <summary>
        /// The master reopens the room (and voids every ready) when it is still closed and no start runs on this device:
        /// checked when a start stops here and when this device becomes the master. Done once — the reopening is seen at once.
        /// </summary>
        public static bool ReopensRoom(bool isMaster, bool roomOpen, CoopStage stage) => isMaster && !roomOpen && !InStart(stage);

        /// <summary>
        /// The one start condition of the host, for the button, the lobby's request and the session's final check: the
        /// current host in a room, 2–4 participants all ready for the current settings, the settings confirmed by the
        /// server (no change waiting or unknown), and no start running.
        /// </summary>
        public static bool HostCanStart(bool isHost, bool inRoom, int participants, int readyForCurrentSettings, SettingsState settings, CoopStage stage) =>
            isHost && inRoom && settings == SettingsState.Settled && stage == CoopStage.Waiting &&
            participants >= CoopRules.MinPlayers && participants <= CoopRules.MaxPlayers && readyForCurrentSettings == participants;
    }

    public enum SettingsState
    {
        /// <summary>The room's settings are the server's confirmed ones (nothing of this device in question).</summary>
        Settled,
        /// <summary>A difficulty change was sent; its confirmation is awaited.</summary>
        Waiting,
        /// <summary>
        /// No confirmation in time, or the server refused the change: the server's final settings are not known here.
        /// Only a later confirmed room update (or leaving the room) settles it; no start meanwhile.
        /// </summary>
        Unknown,
    }

    public enum SettingsChangeResult
    {
        /// <summary>Nothing changed.</summary>
        None,
        /// <summary>The room shows exactly the requested difficulty and revision.</summary>
        Confirmed,
        /// <summary>The room shows other confirmed values of that revision or a later one (they are the settings now).</summary>
        Superseded,
        /// <summary>No confirmation in time, or the server refused: settings unknown until confirmed values arrive.</summary>
        BecameUnknown,
    }

    /// <summary>
    /// The settings of the waiting room as this host device knows them, for one difficulty change at a time (no Photon
    /// type). Tied to one room and one entry into it (`room`, `entry`): reports for another room or entry never touch it,
    /// and only leaving that room (or entering anew) resets it — not closing a panel or a failed leave request.
    /// Begin → Waiting. A request that was not sent at all (immediate false) goes straight back to Settled (nothing
    /// changed). Waiting → Settled when the room shows the requested pair (Confirmed) or other values of that revision or
    /// later (Superseded: those are the confirmed settings now; every ready of older settings stays void). Waiting →
    /// Unknown at the time limit (the request may still succeed later) or when the server refused it (the settings it
    /// refused against are not known here). Unknown → Settled only by such a room update. While not Settled, no start, no
    /// ready change and no further difficulty change.
    /// </summary>
    public sealed class SettingsChangeGate
    {
        public const double DefaultTimeoutSeconds = 5.0;

        readonly double timeout;
        double sentAt;
        string room;
        int entry;

        public SettingsState State { get; private set; } = SettingsState.Settled;
        public bool Settled => State == SettingsState.Settled;
        public int Difficulty { get; private set; }
        public int Revision { get; private set; }

        /// <summary>The request may still be in flight (not refused, not confirmed).</summary>
        public bool InFlight { get; private set; }

        public SettingsChangeGate(double timeoutSeconds = DefaultTimeoutSeconds) => timeout = timeoutSeconds;

        /// <summary>Whether a report about `roomName` / `entryNumber` concerns the change kept here.</summary>
        public bool Applies(string roomName, int entryNumber) => State != SettingsState.Settled && roomName == room && entryNumber == entry;

        /// <summary>Starts waiting for `difficulty` at `revision` in that room entry; false unless settled (never nested).</summary>
        public bool Begin(int difficulty, int revision, string roomName, int entryNumber, double now)
        {
            if (State != SettingsState.Settled || roomName == null) return false;
            State = SettingsState.Waiting;
            InFlight = true;
            Difficulty = difficulty;
            Revision = revision;
            room = roomName;
            entry = entryNumber;
            sentAt = now;
            return true;
        }

        /// <summary>The request returned false at once: nothing was sent, the settings are the known ones.</summary>
        public void NotSent()
        {
            if (State == SettingsState.Waiting && InFlight) Reset();
        }

        /// <summary>The room's current values after a room update in that room entry.</summary>
        public SettingsChangeResult Observe(string roomName, int entryNumber, int roomDifficulty, int roomRevision)
        {
            if (!Applies(roomName, entryNumber)) return SettingsChangeResult.None;
            if (roomRevision == Revision && roomDifficulty == Difficulty)
            {
                Reset();
                return SettingsChangeResult.Confirmed;
            }
            if (roomRevision >= Revision)
            {
                Reset();
                return SettingsChangeResult.Superseded;
            }
            return SettingsChangeResult.None;                       // older values: the change may still come
        }

        /// <summary>The server refused a property change of that entry while this one was in flight.</summary>
        public SettingsChangeResult Refused(string roomName, int entryNumber)
        {
            if (!Applies(roomName, entryNumber) || !InFlight) return SettingsChangeResult.None;
            InFlight = false;
            State = SettingsState.Unknown;
            return SettingsChangeResult.BecameUnknown;
        }

        public SettingsChangeResult CheckTimeout(double now)
        {
            if (State != SettingsState.Waiting || now - sentAt <= timeout) return SettingsChangeResult.None;
            State = SettingsState.Unknown;                         // still in flight: it may succeed later
            return SettingsChangeResult.BecameUnknown;
        }

        /// <summary>Left that room or the connection ended, or a room was entered anew: the question is gone with it.</summary>
        public void Reset()
        {
            State = SettingsState.Settled;
            InFlight = false;
            room = null;
        }
    }

    /// <summary>
    /// An own, intended disconnect that a coop session asked for (leaving a game whose connection it owned), recorded
    /// apart from any object so that it is still known when the session has already gone before the disconnect callback.
    /// Consumed by the first disconnect callback that asks; forgotten when a new connection is started or reaches the
    /// master server (an older record can never describe that new connection).
    /// </summary>
    public sealed class DisconnectExpectation
    {
        public bool Pending { get; private set; }

        public void Request() => Pending = true;

        /// <summary>At a disconnect callback: whether it is the intended one (then it is used up).</summary>
        public bool Consume()
        {
            var p = Pending;
            Pending = false;
            return p;
        }

        public void Forget() => Pending = false;

        /// <summary>
        /// After a disconnect: connect again only when it was intended (by this lobby or a coop session) and the user
        /// has entered the coop screen meanwhile and still wants it. An unexpected disconnect never reconnects by itself.
        /// </summary>
        public static bool ShouldReconnect(bool intended, bool screenActive, bool userWantsConnection) => intended && screenActive && userWantsConnection;
    }
}
