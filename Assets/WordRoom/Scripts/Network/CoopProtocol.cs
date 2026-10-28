using System;
using System.Collections.Generic;
using System.Linq;
using WordRoom.Generation;

namespace WordRoom.Network
{
    /// <summary>
    /// The coop start messages as plain values (object[] of int, long, bool, string and their arrays — types PUN
    /// serializes), with their encoding and the checks a receiver applies before using anything: exact length and
    /// types, protocol version, string and array sizes, ids, participants, settings and coordinates, word ids within
    /// the receiver's own vocabulary. No Photon type here: the sender check (real host / real participant) is done by
    /// CoopSession with the event's sender, never with a value in the body. GenerationUpdate, SessionCopy and
    /// FillReport objects are never sent; the initial world is sent as its coordinates, words, preparation, doors,
    /// team visits and players only (no relation data, vectors, recent words or generation reports).
    /// </summary>
    public static class CoopProtocol
    {
        /// <summary>2: the initial world carries the whole fixed generation settings (EncodeConfig) instead of seed and vocabulary only.</summary>
        public const int Version = 2;

        public const byte BeginCode = 101;
        public const byte DataCode = 102;
        public const byte WorldCode = 103;
        public const byte PlacedCode = 104;
        public const byte GoCode = 105;
        public const byte AbortCode = 106;

        public const int MaxIdLength = 64;
        public const int MaxPackageIdLength = 128;
        public const int MaxTextLength = 200;
        public const int MaxStatusLength = 32;
        public const int MaxRooms = 4096;
        public const int MaxCoordinate = 100000;
        public const int MaxVisitsPerPlayer = MaxRooms;
        public const int MaxPathPerPlayer = MaxRooms;

        public static bool IsCoopCode(byte code) => code >= BeginCode && code <= AbortCode;

        // ---- messages ------------------------------------------------------------------------------------------
        public sealed class Begin
        {
            public string Attempt;
            public int Host;
            public int[] Actors;
            public int Difficulty;
            public int SettingsRevision;
        }

        public sealed class DataReport
        {
            public string Attempt;
            public bool Ok;
            public string PackageId;
            public string Error;
        }

        public sealed class World
        {
            public string Attempt;
            public int Host;
            public int[] Actors;
            public string[] PlayerIds;
            public int Difficulty;
            public string PackageId;
            /// <summary>
            /// The host's fixed generation settings, decoded into a new instance of this message (the one source of the
            /// seed, vocabulary range and lengths; checked by GenerationConfig.Validate). Use Settings() for a copy.
            /// </summary>
            public GenerationConfig Config;
            /// <summary>The initial state restored as an independent, read-only copy (SessionId, revision, rooms, players…).</summary>
            public SessionCopy Copy;

            /// <summary>An independent copy of the fixed settings (changing it never changes this message).</summary>
            public GenerationConfig Settings() => Config.Clone();
        }

        public sealed class Placed
        {
            public string Attempt;
            public string SessionId;
            public long Revision;
            public string PlayerId;
            public bool Ok;
            public string Error;
        }

        public sealed class Go
        {
            public string Attempt;
            public string SessionId;
            public long Revision;
            /// <summary>Photon server time (int milliseconds, wrapping) of the common start.</summary>
            public int StartServerTime;
        }

        public sealed class Abort
        {
            public string Attempt;
            public string Reason;
        }

        // ---- encode --------------------------------------------------------------------------------------------
        public static object[] Encode(Begin m) => new object[] { Version, m.Attempt, m.Host, m.Actors, m.Difficulty, m.SettingsRevision };

        public static object[] Encode(DataReport m) => new object[] { Version, m.Attempt, m.Ok, m.PackageId ?? "", Cut(m.Error) };

        public static object[] Encode(Placed m) => new object[] { Version, m.Attempt, m.SessionId, m.Revision, m.PlayerId, m.Ok, Cut(m.Error) };

        public static object[] Encode(Go m) => new object[] { Version, m.Attempt, m.SessionId, m.Revision, m.StartServerTime };

        public static object[] Encode(Abort m) => new object[] { Version, m.Attempt, Cut(m.Reason) };

        /// <summary>
        /// The fixed generation settings as plain values, every field that changes generation or its rules, in this
        /// order: seed, vocabulary limit, min/max word length, repeat avoidance (on, scope, radius), candidate choice
        /// (selection top, temperature), recent window, diversity, neighbor weight, relation threshold/step/floor,
        /// placement budget, fastText supplement (on, preferred minimum), target guidance (on, strength, start, ramp,
        /// max, basis), test move limit (on, max moves). Not sent: Source (a log text with no effect on generation).
        /// </summary>
        public static object[] EncodeConfig(GenerationConfig c) => new object[]
        {
            c.Seed, c.VocabularyLimit, c.MinWordLength, c.MaxWordLength,
            c.AvoidAdjacentRepeat, c.RepeatScope ?? "", c.RepeatRadius,
            c.SelectionTop, c.Temperature, c.RecentWindow, c.Diversity, c.NeighborWeight,
            c.RelationThreshold, c.RelationStep, c.RelationFloor, c.PlacementBudget,
            c.FastTextSupplement, c.FastTextPreferredMin,
            c.TargetGuidance, c.GuidanceStrength, c.GuidanceStart, c.GuidanceRamp, c.GuidanceMax, c.GuidanceBasis ?? "",
            c.UseMoveLimit, c.MaxMoves,
        };

        public const int ConfigLength = 26;

        /// <summary>
        /// Fixed settings sent by the host, checked (types, finite numbers, ranges, the receiver's vocabulary size) and
        /// then by GenerationConfig.Validate, into a new instance (never filled from this device's own defaults).
        /// </summary>
        public static bool TryDecodeConfig(object content, int wordCount, out GenerationConfig config, out string error)
        {
            config = null;
            error = null;
            var a = content as object[];
            if (a == null || a.Length != ConfigLength) return Bad($"생성 설정 형식(길이 {a?.Length ?? -1}, 기대 {ConfigLength})", out error);
            if (!Long(a, 0, out var seed, ref error) || !Int(a, 1, out var vocabulary, ref error) || !Int(a, 2, out var minLength, ref error) ||
                !Int(a, 3, out var maxLength, ref error) || !Bool(a, 4, out var avoid, ref error) || !Str(a, 5, 16, false, out var scope, ref error) ||
                !Int(a, 6, out var radius, ref error) || !Int(a, 7, out var top, ref error) || !Dbl(a, 8, out var temperature, ref error) ||
                !Int(a, 9, out var recent, ref error) || !Dbl(a, 10, out var diversity, ref error) || !Dbl(a, 11, out var neighbor, ref error) ||
                !Dbl(a, 12, out var threshold, ref error) || !Dbl(a, 13, out var step, ref error) || !Dbl(a, 14, out var floor, ref error) ||
                !Int(a, 15, out var budget, ref error) || !Bool(a, 16, out var fastText, ref error) || !Dbl(a, 17, out var preferred, ref error) ||
                !Bool(a, 18, out var guidance, ref error) || !Dbl(a, 19, out var strength, ref error) || !Int(a, 20, out var start, ref error) ||
                !Int(a, 21, out var ramp, ref error) || !Dbl(a, 22, out var max, ref error) || !Str(a, 23, 16, false, out var basis, ref error) ||
                !Bool(a, 24, out var moveLimit, ref error) || !Int(a, 25, out var maxMoves, ref error))
                return false;
            if (vocabulary > wordCount || maxLength > 64) return Bad("생성 설정의 어휘 범위", out error);
            if (radius > 1000 || top > 10000 || recent > 100000 || budget > 100000000 || start > 10000000 || ramp > 10000000 || maxMoves < 0 || maxMoves > 10000000)
                return Bad("생성 설정의 정수 범위", out error);
            var c = new GenerationConfig
            {
                Seed = seed, VocabularyLimit = vocabulary, MinWordLength = minLength, MaxWordLength = maxLength,
                AvoidAdjacentRepeat = avoid, RepeatScope = scope, RepeatRadius = radius,
                SelectionTop = top, Temperature = temperature, RecentWindow = recent, Diversity = diversity, NeighborWeight = neighbor,
                RelationThreshold = threshold, RelationStep = step, RelationFloor = floor, PlacementBudget = budget,
                FastTextSupplement = fastText, FastTextPreferredMin = preferred,
                TargetGuidance = guidance, GuidanceStrength = strength, GuidanceStart = start, GuidanceRamp = ramp, GuidanceMax = max, GuidanceBasis = basis,
                UseMoveLimit = moveLimit, MaxMoves = maxMoves,
                Source = "협동 호스트의 고정 생성 설정(전송 수신)",
            };
            try
            {
                c.Validate();                                          // the same rules as for a local experiment
            }
            catch (ArgumentException e)
            {
                return Bad($"생성 설정 — {e.Message}", out error);
            }
            config = c;
            return true;
        }

        /// <summary>
        /// The initial world of `copy` (a copy of one revision) with the start values and the fixed generation settings
        /// `config` — the one source of the seed and vocabulary range (the copy's seed and package must agree with them).
        /// </summary>
        public static object[] EncodeWorld(string attempt, int host, int[] actors, int difficulty, string packageId, GenerationConfig config, SessionCopy copy)
        {
            if (copy.Seed != config.Seed || copy.PackageId != packageId) throw new ArgumentException("초기 사본과 고정 설정의 시드·패키지가 다릅니다.");
            var rooms = copy.Rooms;
            var doors = copy.OpenDoors;
            var players = copy.Players;
            var vOwner = new List<int>();
            var vx = new List<int>();
            var vy = new List<int>();
            var vc = new List<int>();
            var pOwner = new List<int>();
            var px = new List<int>();
            var py = new List<int>();
            for (var i = 0; i < players.Count; i++)
            {
                foreach (var v in players[i].Visits)
                {
                    vOwner.Add(i);
                    vx.Add(v.Key.X);
                    vy.Add(v.Key.Y);
                    vc.Add(v.Value);
                }
                foreach (var p in players[i].Path)
                {
                    pOwner.Add(i);
                    px.Add(p.X);
                    py.Add(p.Y);
                }
            }
            return new object[]
            {
                Version, attempt, host, actors, players.Select(p => p.Id).ToArray(), difficulty, packageId, EncodeConfig(config),
                copy.SessionId, copy.Revision, copy.Status, copy.StartWord, copy.TargetWord,
                rooms.Select(r => r.Position.X).ToArray(), rooms.Select(r => r.Position.Y).ToArray(), rooms.Select(r => r.Word).ToArray(),
                copy.Prepared.Select(p => p.X).ToArray(), copy.Prepared.Select(p => p.Y).ToArray(),
                doors.Select(d => d.A.X).ToArray(), doors.Select(d => d.A.Y).ToArray(), doors.Select(d => d.B.X).ToArray(), doors.Select(d => d.B.Y).ToArray(),
                copy.TeamVisited.Select(p => p.X).ToArray(), copy.TeamVisited.Select(p => p.Y).ToArray(),
                players.Select(p => p.Position.X).ToArray(), players.Select(p => p.Position.Y).ToArray(), players.Select(p => p.Moves).ToArray(),
                vOwner.ToArray(), vx.ToArray(), vy.ToArray(), vc.ToArray(),
                pOwner.ToArray(), px.ToArray(), py.ToArray(),
            };
        }

        /// <summary>Index of each part of the initial world message (version 2).</summary>
        public static class WorldIndex
        {
            public const int Attempt = 1, Host = 2, Actors = 3, PlayerIds = 4, Difficulty = 5, PackageId = 6, Config = 7, SessionId = 8, Revision = 9,
                Status = 10, StartWord = 11, TargetWord = 12, RoomX = 13, RoomY = 14, RoomWord = 15, PreparedX = 16, PreparedY = 17,
                DoorAX = 18, DoorAY = 19, DoorBX = 20, DoorBY = 21, TeamX = 22, TeamY = 23, PlayerX = 24, PlayerY = 25, Moves = 26,
                VisitOwner = 27, VisitX = 28, VisitY = 29, VisitCount = 30, PathOwner = 31, PathX = 32, PathY = 33, Length = 34;
        }

        const int WorldLength = WorldIndex.Length;

        static string Cut(string s) => s == null ? "" : s.Length <= MaxTextLength ? s : s.Substring(0, MaxTextLength);

        // ---- decode --------------------------------------------------------------------------------------------
        public static bool TryDecode(object content, out Begin m, out string error)
        {
            m = null;
            if (!Head(content, 6, out var a, out error)) return false;
            if (!Id(a, 1, out var attempt, ref error) || !Int(a, 2, out var host, ref error) || !Ints(a, 3, CoopRules.MaxPlayers, out var actors, ref error) ||
                !Int(a, 4, out var difficulty, ref error) || !Int(a, 5, out var revision, ref error)) return false;
            if (!Participants(actors, host, ref error)) return false;
            if (!CoopRules.IsValidDifficulty(difficulty)) return Bad($"난이도 값 {difficulty}", out error);
            m = new Begin { Attempt = attempt, Host = host, Actors = actors, Difficulty = difficulty, SettingsRevision = revision };
            return true;
        }

        public static bool TryDecode(object content, out DataReport m, out string error)
        {
            m = null;
            if (!Head(content, 5, out var a, out error)) return false;
            if (!Id(a, 1, out var attempt, ref error) || !Bool(a, 2, out var ok, ref error) || !Str(a, 3, MaxPackageIdLength, true, out var package, ref error) ||
                !Str(a, 4, MaxTextLength, true, out var err, ref error)) return false;
            if (ok && package.Length == 0) return Bad("준비 완료인데 패키지 ID가 없음", out error);
            m = new DataReport { Attempt = attempt, Ok = ok, PackageId = package, Error = err };
            return true;
        }

        public static bool TryDecode(object content, out Placed m, out string error)
        {
            m = null;
            if (!Head(content, 7, out var a, out error)) return false;
            if (!Id(a, 1, out var attempt, ref error) || !Id(a, 2, out var session, ref error) || !Long(a, 3, out var revision, ref error) ||
                !Id(a, 4, out var player, ref error) || !Bool(a, 5, out var ok, ref error) || !Str(a, 6, MaxTextLength, true, out var err, ref error)) return false;
            if (revision < 1) return Bad($"변경 순번 {revision}", out error);
            m = new Placed { Attempt = attempt, SessionId = session, Revision = revision, PlayerId = player, Ok = ok, Error = err };
            return true;
        }

        public static bool TryDecode(object content, out Go m, out string error)
        {
            m = null;
            if (!Head(content, 5, out var a, out error)) return false;
            if (!Id(a, 1, out var attempt, ref error) || !Id(a, 2, out var session, ref error) || !Long(a, 3, out var revision, ref error) ||
                !Int(a, 4, out var start, ref error)) return false;
            if (revision < 1) return Bad($"변경 순번 {revision}", out error);
            m = new Go { Attempt = attempt, SessionId = session, Revision = revision, StartServerTime = start };
            return true;
        }

        public static bool TryDecode(object content, out Abort m, out string error)
        {
            m = null;
            if (!Head(content, 3, out var a, out error)) return false;
            if (!Id(a, 1, out var attempt, ref error) || !Str(a, 2, MaxTextLength, true, out var reason, ref error)) return false;
            m = new Abort { Attempt = attempt, Reason = reason };
            return true;
        }

        /// <summary>
        /// The initial world, checked against the receiver's vocabulary size (`wordCount`) — the fixed settings with
        /// TryDecodeConfig — and then restored into an independent SessionCopy (which checks its own consistency: start
        /// room, duplicates, players and visits). The copy's seed is the settings' seed (one source).
        /// </summary>
        public static bool TryDecodeWorld(object content, int wordCount, out World m, out string error)
        {
            m = null;
            if (!Head(content, WorldLength, out var a, out error)) return false;
            if (!Id(a, WorldIndex.Attempt, out var attempt, ref error) || !Int(a, WorldIndex.Host, out var host, ref error) ||
                !Ints(a, WorldIndex.Actors, CoopRules.MaxPlayers, out var actors, ref error) ||
                !Strs(a, WorldIndex.PlayerIds, CoopRules.MaxPlayers, MaxIdLength, out var ids, ref error) || !Int(a, WorldIndex.Difficulty, out var difficulty, ref error) ||
                !Str(a, WorldIndex.PackageId, MaxPackageIdLength, false, out var package, ref error) || !Id(a, WorldIndex.SessionId, out var session, ref error) ||
                !Long(a, WorldIndex.Revision, out var revision, ref error) || !Str(a, WorldIndex.Status, MaxStatusLength, false, out var status, ref error) ||
                !Int(a, WorldIndex.StartWord, out var startWord, ref error) || !Int(a, WorldIndex.TargetWord, out var targetWord, ref error))
                return false;
            if (!TryDecodeConfig(a[WorldIndex.Config], wordCount, out var config, out error)) return false;
            if (!Participants(actors, host, ref error)) return false;
            if (ids.Length != actors.Length) return Bad("플레이어 ID 수가 참가자 수와 다름", out error);
            for (var i = 0; i < ids.Length; i++)
                if (ids[i] != CoopRules.PlayerIdOf(actors[i])) return Bad($"플레이어 ID {ids[i]}가 참가자 {actors[i]}와 맞지 않음", out error);
            if (!CoopRules.IsValidDifficulty(difficulty)) return Bad($"난이도 값 {difficulty}", out error);
            if (!Word(startWord, wordCount) || !Word(targetWord, wordCount) || startWord == targetWord) return Bad("시작·목표 단어 번호", out error);
            if (revision < 1) return Bad($"변경 순번 {revision}", out error);

            var setMax = MaxVisitsPerPlayer * CoopRules.MaxPlayers;
            var pathMax = MaxPathPerPlayer * CoopRules.MaxPlayers;
            if (!Ints(a, WorldIndex.RoomX, MaxRooms, out var rx, ref error) || !Ints(a, WorldIndex.RoomY, MaxRooms, out var ry, ref error) ||
                !Ints(a, WorldIndex.RoomWord, MaxRooms, out var rw, ref error) ||
                !Ints(a, WorldIndex.PreparedX, MaxRooms, out var prx, ref error) || !Ints(a, WorldIndex.PreparedY, MaxRooms, out var pry, ref error) ||
                !Ints(a, WorldIndex.DoorAX, MaxRooms * 2, out var dax, ref error) || !Ints(a, WorldIndex.DoorAY, MaxRooms * 2, out var day, ref error) ||
                !Ints(a, WorldIndex.DoorBX, MaxRooms * 2, out var dbx, ref error) || !Ints(a, WorldIndex.DoorBY, MaxRooms * 2, out var dby, ref error) ||
                !Ints(a, WorldIndex.TeamX, MaxRooms, out var tx, ref error) || !Ints(a, WorldIndex.TeamY, MaxRooms, out var ty, ref error) ||
                !Ints(a, WorldIndex.PlayerX, CoopRules.MaxPlayers, out var posX, ref error) || !Ints(a, WorldIndex.PlayerY, CoopRules.MaxPlayers, out var posY, ref error) ||
                !Ints(a, WorldIndex.Moves, CoopRules.MaxPlayers, out var moves, ref error) ||
                !Ints(a, WorldIndex.VisitOwner, setMax, out var vOwner, ref error) || !Ints(a, WorldIndex.VisitX, setMax, out var vx, ref error) ||
                !Ints(a, WorldIndex.VisitY, setMax, out var vy, ref error) || !Ints(a, WorldIndex.VisitCount, setMax, out var vc, ref error) ||
                !Ints(a, WorldIndex.PathOwner, pathMax, out var pOwner, ref error) || !Ints(a, WorldIndex.PathX, pathMax, out var px, ref error) ||
                !Ints(a, WorldIndex.PathY, pathMax, out var py, ref error))
                return false;
            if (rx.Length != ry.Length || rx.Length != rw.Length || prx.Length != pry.Length || dax.Length != day.Length || dax.Length != dbx.Length ||
                dax.Length != dby.Length || tx.Length != ty.Length || posX.Length != ids.Length || posY.Length != ids.Length || moves.Length != ids.Length ||
                vOwner.Length != vx.Length || vOwner.Length != vy.Length || vOwner.Length != vc.Length || pOwner.Length != px.Length || pOwner.Length != py.Length)
                return Bad("배열 길이가 서로 맞지 않음", out error);
            if (!Coords(rx) || !Coords(ry) || !Coords(prx) || !Coords(pry) || !Coords(dax) || !Coords(day) || !Coords(dbx) || !Coords(dby) ||
                !Coords(tx) || !Coords(ty) || !Coords(posX) || !Coords(posY) || !Coords(vx) || !Coords(vy) || !Coords(px) || !Coords(py))
                return Bad("좌표 범위", out error);
            if (rw.Any(w => !Word(w, wordCount))) return Bad("방 단어 번호", out error);
            if (vOwner.Any(o => o < 0 || o >= ids.Length) || pOwner.Any(o => o < 0 || o >= ids.Length)) return Bad("플레이어 번호", out error);

            var parts = new SessionCopyParts
            {
                SessionId = session, Mode = SessionMode.Coop, Revision = revision, StartWord = startWord, TargetWord = targetWord, Seed = config.Seed, PackageId = package, Status = status,
            };
            for (var i = 0; i < rx.Length; i++) parts.Rooms.Add(new KeyValuePair<GridPos, int>(new GridPos(rx[i], ry[i]), rw[i]));
            for (var i = 0; i < prx.Length; i++) parts.Prepared.Add(new GridPos(prx[i], pry[i]));
            for (var i = 0; i < tx.Length; i++) parts.TeamVisited.Add(new GridPos(tx[i], ty[i]));
            for (var i = 0; i < dax.Length; i++)
            {
                var p = new GridPos(dax[i], day[i]);
                var q = new GridPos(dbx[i], dby[i]);
                if (GridPos.Distance(p, q) != 1) return Bad($"문 연결 {p}-{q}", out error);
                parts.OpenDoors.Add(RoomConnection.Between(p, q));
            }
            for (var i = 0; i < ids.Length; i++)
                parts.Players.Add(new SessionCopyParts.Player { Id = ids[i], Position = new GridPos(posX[i], posY[i]), Moves = moves[i] });
            for (var i = 0; i < vOwner.Length; i++)
                parts.Players[vOwner[i]].Visits.Add(new KeyValuePair<GridPos, int>(new GridPos(vx[i], vy[i]), vc[i]));
            for (var i = 0; i < pOwner.Length; i++)
                parts.Players[pOwner[i]].Path.Add(new GridPos(px[i], py[i]));
            SessionCopy copy;
            try
            {
                copy = SessionCopy.Restore(parts);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return false;
            }
            m = new World { Attempt = attempt, Host = host, Actors = actors, PlayerIds = ids, Difficulty = difficulty, PackageId = package, Config = config, Copy = copy };
            error = null;
            return true;
        }

        // ---- checks --------------------------------------------------------------------------------------------
        static bool Head(object content, int length, out object[] a, out string error)
        {
            a = content as object[];
            error = null;
            if (a == null || a.Length != length) return Bad($"메시지 형식(길이 {a?.Length ?? -1}, 기대 {length})", out error);
            if (!(a[0] is int v) || v != Version) return Bad($"프로토콜 버전({a[0]})", out error);
            return true;
        }

        static bool Participants(int[] actors, int host, ref string error)
        {
            if (actors.Length < CoopRules.MinPlayers || actors.Length > CoopRules.MaxPlayers) return Bad($"참가자 수 {actors.Length}", out error);
            if (actors.Any(x => x <= 0) || actors.Distinct().Count() != actors.Length) return Bad("참가자 번호", out error);
            for (var i = 1; i < actors.Length; i++)
                if (actors[i] < actors[i - 1]) return Bad("참가자 순서", out error);
            if (Array.IndexOf(actors, host) < 0) return Bad("호스트가 참가자에 없음", out error);
            return true;
        }

        static bool Word(int w, int wordCount) => w >= 0 && w < wordCount;

        static bool Coords(int[] v) => v.All(c => c >= -MaxCoordinate && c <= MaxCoordinate);

        static bool Bad(string what, out string error)
        {
            error = $"잘못된 메시지: {what}";
            return false;
        }

        static bool Int(object[] a, int i, out int v, ref string error)
        {
            if (a[i] is int x)
            {
                v = x;
                return true;
            }
            v = 0;
            return Bad($"{i}번 값(정수)", out error);
        }

        /// <summary>A finite double (NaN and infinities are refused).</summary>
        static bool Dbl(object[] a, int i, out double v, ref string error)
        {
            if (a[i] is double x && !double.IsNaN(x) && !double.IsInfinity(x))
            {
                v = x;
                return true;
            }
            v = 0;
            return Bad($"{i}번 값(유한한 실수)", out error);
        }

        static bool Long(object[] a, int i, out long v, ref string error)
        {
            if (a[i] is long x)
            {
                v = x;
                return true;
            }
            v = 0;
            return Bad($"{i}번 값(긴 정수)", out error);
        }

        static bool Bool(object[] a, int i, out bool v, ref string error)
        {
            if (a[i] is bool x)
            {
                v = x;
                return true;
            }
            v = false;
            return Bad($"{i}번 값(참·거짓)", out error);
        }

        static bool Str(object[] a, int i, int max, bool allowEmpty, out string v, ref string error)
        {
            v = a[i] as string;
            if (v != null && v.Length <= max && (allowEmpty || v.Length > 0)) return true;
            v = null;
            return Bad($"{i}번 값(문자열)", out error);
        }

        static bool Id(object[] a, int i, out string v, ref string error) => Str(a, i, MaxIdLength, false, out v, ref error);

        static bool Ints(object[] a, int i, int max, out int[] v, ref string error)
        {
            v = a[i] as int[];
            if (v != null && v.Length <= max) return true;
            v = null;
            return Bad($"{i}번 값(정수 배열)", out error);
        }

        static bool Strs(object[] a, int i, int max, int maxLength, out string[] v, ref string error)
        {
            v = a[i] as string[];
            if (v != null && v.Length <= max && v.All(s => !string.IsNullOrEmpty(s) && s.Length <= maxLength)) return true;
            v = null;
            return Bad($"{i}번 값(문자열 배열)", out error);
        }
    }
}
