using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Dolphin2
{
    // 32-byte training record: occupancy + 4-bit piece codes + score + result.
    public struct PackedPos
    {
        public ulong Occ;
        public ulong Pc0, Pc1;   // 32 nibbles, piece codes in LSB-first square order
        public short Score;      // white point of view, centipawns
        public byte Result;      // 0 = black win, 1 = draw, 2 = white win
        public byte Stm;
        public uint Extra;

        public static PackedPos From(Position p, int whiteScore)
        {
            var r = new PackedPos { Occ = p.Occupied, Score = (short)Math.Clamp(whiteScore, -32000, 32000), Stm = (byte)p.SideToMove };
            ulong b = p.Occupied;
            int i = 0;
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                ulong code = (ulong)p.Board[sq];
                if (i < 16) r.Pc0 |= code << (4 * i); else r.Pc1 |= code << (4 * (i - 16));
                i++;
            }
            return r;
        }

        public const int Size = 32;

        public void Write(BinaryWriter w)
        {
            w.Write(Occ); w.Write(Pc0); w.Write(Pc1); w.Write(Score); w.Write(Result); w.Write(Stm); w.Write(Extra);
        }

        public static PackedPos Read(BinaryReader r)
        {
            return new PackedPos { Occ = r.ReadUInt64(), Pc0 = r.ReadUInt64(), Pc1 = r.ReadUInt64(), Score = r.ReadInt16(), Result = r.ReadByte(), Stm = r.ReadByte(), Extra = r.ReadUInt32() };
        }

        public string ToFen()
        {
            var p = new char[64];
            for (int i = 0; i < 64; i++) p[i] = '.';
            ulong b = Occ; int k = 0;
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                int code = (int)((k < 16 ? Pc0 >> (4 * k) : Pc1 >> (4 * (k - 16))) & 15);
                p[sq] = C.PieceChars[code];
                k++;
            }
            var sb = new System.Text.StringBuilder();
            for (int r = 7; r >= 0; r--)
            {
                int e = 0;
                for (int f = 0; f < 8; f++)
                {
                    char ch = p[r * 8 + f];
                    if (ch == '.') { e++; continue; }
                    if (e > 0) { sb.Append(e); e = 0; }
                    sb.Append(ch);
                }
                if (e > 0) sb.Append(e);
                if (r > 0) sb.Append('/');
            }
            sb.Append(Stm == 0 ? " w - - 0 1" : " b - - 0 1");
            return sb.ToString();
        }
    }

    public static class DataGen
    {
        static readonly object fileLock = new object();
        static long games, positions;

        // datagen threads=15 nodes=5000 games=200000 out=data/gen1.bin [book=tools/openings8.epd] [seed=1]
        public static void Run(string[] tok)
        {
            int nThreads = Math.Max(1, Environment.ProcessorCount - 1);
            int nodes = 5000;
            long targetGames = 100000;
            string outFile = "data.bin";
            string book = null;
            int seed = Environment.TickCount;
            int hash = 256;
            foreach (var t in tok)
            {
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string k = t.Substring(0, eq), v = t.Substring(eq + 1);
                switch (k)
                {
                    case "threads": nThreads = int.Parse(v); break;
                    case "nodes": nodes = int.Parse(v); break;
                    case "games": targetGames = long.Parse(v); break;
                    case "out": outFile = v; break;
                    case "book": book = v; break;
                    case "seed": seed = int.Parse(v); break;
                    case "hash": hash = int.Parse(v); break;
                }
            }
            string[] openings = book != null && File.Exists(book) ? File.ReadAllLines(book) : Array.Empty<string>();
            TT.Resize(hash);
            Console.WriteLine($"datagen: threads={nThreads} nodes={nodes} games={targetGames} out={outFile} book={openings.Length} eval={(NNUE.Enabled ? "nnue" : "classical")}");
            var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            Directory.CreateDirectory(dir);
            games = positions = 0;
            var sw = Stopwatch.StartNew();
            using var fs = new FileStream(outFile, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            using var bw = new BinaryWriter(fs);
            var workers = new Thread[nThreads];
            for (int i = 0; i < nThreads; i++)
            {
                int id = i;
                workers[i] = new Thread(() => Worker(id, seed + id * 7919, nodes, targetGames, openings, bw), 64 * 1024 * 1024) { IsBackground = true };
                workers[i].Start();
            }
            long lastReport = 0;
            while (true)
            {
                bool alive = false;
                foreach (var w in workers) if (w.IsAlive) { alive = true; break; }
                if (!alive) break;
                Thread.Sleep(500);
                TT.NewSearch(); // age old entries so finished games get evicted
                if (sw.ElapsedMilliseconds - lastReport > 60000)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    double s = sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"[{(int)s}s] games {Interlocked.Read(ref games)} positions {Interlocked.Read(ref positions)} ({Interlocked.Read(ref positions) / s:F0} pos/s)");
                }
            }
            lock (fileLock) bw.Flush();
            Console.WriteLine($"done: games {games} positions {positions} in {sw.Elapsed.TotalSeconds:F0}s");
        }

        static void Worker(int id, int seed, int nodes, long targetGames, string[] openings, BinaryWriter bw)
        {
            var rng = new Random(seed);
            var ctx = new SearchContext { Silent = true };
            var th = new SearchThread(0) { Ctx = ctx };
            ctx.Threads = new[] { th };
            if (NNUE.Enabled) th.Pos.EnableNNUE();
            var list = new ExtMove[C.MaxMoves];
            var recs = new List<PackedPos>(512);

            while (Interlocked.Read(ref games) < targetGames)
            {
                var pos = th.Pos;
                int randomPlies;
                if (openings.Length > 0 && rng.Next(2) == 0)
                {
                    string l = openings[rng.Next(openings.Length)].Trim();
                    if (l.Split(' ').Length < 6) l += " 0 1";
                    pos.SetFen(l);
                    randomPlies = 2 + rng.Next(3);
                }
                else
                {
                    pos.SetFen(Position.StartFen);
                    randomPlies = 8 + rng.Next(2);
                }
                bool ok = true;
                for (int i = 0; i < randomPlies; i++)
                {
                    int n = MoveGen.GenerateLegal(pos, list);
                    if (n == 0) { ok = false; break; }
                    pos.MakeMove(list[rng.Next(n)].Move);
                }
                if (!ok || MoveGen.GenerateLegal(pos, list) == 0) continue;
                // re-root the game so the history array does not grow without bound
                pos.SetFen(pos.GetFen());
                th.ClearHistory();

                // reject very unbalanced starting positions
                Search(th, ctx, nodes);
                if (Math.Abs(th.BestScore) > 350) continue;

                recs.Clear();
                int result = -1;
                int winStreak = 0, drawStreak = 0, plies = 0;
                while (true)
                {
                    int n = MoveGen.GenerateLegal(pos, list);
                    if (n == 0) { result = pos.InCheck() ? (pos.SideToMove == C.White ? 0 : 2) : 1; break; }
                    if (pos.IsDraw(0) || plies >= 400) { result = 1; break; }

                    Search(th, ctx, nodes);
                    int best = th.BestMove;
                    int score = th.BestScore;
                    int ws = pos.SideToMove == C.White ? score : -score;

                    if (Math.Abs(score) >= 2500) { if (++winStreak >= 4) { result = ws > 0 ? 2 : 0; break; } }
                    else winStreak = 0;
                    if (plies >= 80 && Math.Abs(score) <= 8) { if (++drawStreak >= 12) { result = 1; break; } }
                    else drawStreak = 0;

                    if (!pos.InCheck() && !Mv.IsTactical(best) && Math.Abs(score) < 2500)
                        recs.Add(PackedPos.From(pos, ws));

                    pos.MakeMove(best);
                    plies++;
                    if (pos.GamePly > 1500) { pos.SetFen(pos.GetFen()); }
                }
                if (result < 0) continue;
                lock (fileLock)
                {
                    foreach (var r in recs)
                    {
                        var x = r; x.Result = (byte)result;
                        x.Write(bw);
                    }
                }
                Interlocked.Increment(ref games);
                Interlocked.Add(ref positions, recs.Count);
            }
        }

        static void Search(SearchThread th, SearchContext ctx, int nodes)
        {
            ctx.Limits = new SearchLimits { Nodes = nodes };
            ctx.Stop = false;
            ctx.UseTime = false;
            th.Run();
        }
    }
}
