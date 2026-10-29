using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Dolphin2
{
    // Syzygy endgame tablebases through the Fathom library (fathom.dll / libfathom.so, MIT, built from native/).
    // Everything here is optional: without the library or without tables, Largest stays 0 and nothing is probed.
    public static class Syzygy
    {
        public const uint Failed = 0xFFFFFFFF;
        public const int Loss = 0, BlessedLoss = 1, Draw = 2, CursedWin = 3, Win = 4;

        public static int Largest;          // most pieces covered by the loaded tables (0 = no tablebases)
        public static int ProbeDepth = 1;   // in search, probe only at this depth or more (UCI SyzygyProbeDepth)
        public static string LoadedPath = "";

        [DllImport("fathom", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int dtb_init(string path);

        [DllImport("fathom", CallingConvention = CallingConvention.Cdecl)]
        static extern uint dtb_probe_wdl(ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
                                         ulong bishops, ulong knights, ulong pawns, uint ep, int whiteToMove);

        [DllImport("fathom", CallingConvention = CallingConvention.Cdecl)]
        static extern uint dtb_probe_root(ulong white, ulong black, ulong kings, ulong queens, ulong rooks,
                                          ulong bishops, ulong knights, ulong pawns, uint rule50, uint ep,
                                          int whiteToMove, [Out] uint[] results);

        // Returns a message for "info string". An empty path or "<empty>" unloads the tables.
        public static string Init(string path)
        {
            Largest = 0;
            LoadedPath = "";
            if (string.IsNullOrWhiteSpace(path) || path == "<empty>") return "Syzygy tablebases disabled";
            try
            {
                int n = dtb_init(path);
                if (n <= 0) return "no Syzygy tablebases found in " + path;
                Largest = n;
                LoadedPath = path;
                return "Syzygy tablebases loaded: up to " + n + " pieces";
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                return "Syzygy support not available (fathom library missing or not loadable)";
            }
        }

        static ulong Type(Position p, int t) => p.Pieces[t] | p.Pieces[6 + t];

        // WDL from the side to move (0..4), or -1 when the position is not in the tables.
        // Ignores the 50-move counter: callers decide whether that is acceptable.
        public static int ProbeWdl(Position p)
        {
            if (Largest == 0 || p.Castling != 0 || BB.PopCount(p.Occupied) > Largest) return -1;
            uint r = dtb_probe_wdl(p.Colors[0], p.Colors[1], Type(p, C.King), Type(p, C.Queen), Type(p, C.Rook),
                                   Type(p, C.Bishop), Type(p, C.Knight), Type(p, C.Pawn),
                                   p.EpSquare == C.NoSquare ? 0u : (uint)p.EpSquare, p.SideToMove == C.White ? 1 : 0);
            return r == Failed ? -1 : (int)r;
        }

        // Root probe with DTZ. On success returns the moves to keep: when winning, the winning moves that reach a zeroing
        // move soonest; when drawing, the drawing moves; when losing, the losing moves that resist longest.
        // Returns null when the root is not in the tables or nothing could be matched (then the root is not filtered).
        public static List<int> RootMoves(Position p, out int wdl)
        {
            wdl = -1;
            if (Largest == 0 || p.Castling != 0 || BB.PopCount(p.Occupied) > Largest) return null;
            var results = new uint[256];
            uint r = dtb_probe_root(p.Colors[0], p.Colors[1], Type(p, C.King), Type(p, C.Queen), Type(p, C.Rook),
                                    Type(p, C.Bishop), Type(p, C.Knight), Type(p, C.Pawn), (uint)p.Halfmove,
                                    p.EpSquare == C.NoSquare ? 0u : (uint)p.EpSquare, p.SideToMove == C.White ? 1 : 0, results);
            if (r == Failed) return null;

            var legal = new ExtMove[C.MaxMoves];
            int n = MoveGen.GenerateLegal(p, legal);
            var moves = new List<(int move, int wdl, int dtz)>();
            for (int i = 0; i < results.Length && results[i] != Failed; i++)
            {
                uint e = results[i];
                int from = (int)((e >> 10) & 63), to = (int)((e >> 4) & 63), promo = (int)((e >> 16) & 7);
                int w = (int)(e & 15), dtz = (int)(e >> 20);
                for (int k = 0; k < n; k++)
                {
                    int m = legal[k].Move;
                    if (Mv.From(m) != from || Mv.To(m) != to) continue;
                    // Fathom promotions: 1 queen, 2 rook, 3 bishop, 4 knight; Dolphin PromoType: 1 knight .. 4 queen
                    if (Mv.IsPromo(m) ? promo == 0 || Mv.PromoType(m) != 5 - promo : promo != 0) continue;
                    moves.Add((m, w, dtz));
                    break;
                }
            }
            if (moves.Count == 0) return null;

            int best = -1;
            foreach (var x in moves) best = Math.Max(best, x.wdl);
            int bestDtz = best >= CursedWin ? int.MaxValue : -1;
            foreach (var x in moves)
            {
                if (x.wdl != best) continue;
                if (best >= CursedWin) bestDtz = Math.Min(bestDtz, x.dtz);
                else if (best <= BlessedLoss) bestDtz = Math.Max(bestDtz, x.dtz);
            }
            var keep = new List<int>();
            foreach (var x in moves)
                if (x.wdl == best && (best == Draw || x.dtz == bestDtz)) keep.Add(x.move);
            wdl = best;
            return keep;
        }

        // Search score for a WDL result at the given ply, from the side to move.
        public static int Score(int wdl, int ply) => wdl switch
        {
            Win => C.TbWin - ply,
            Loss => -C.TbWin + ply,
            CursedWin => 1,
            BlessedLoss => -1,
            _ => 0,
        };

        // Development check ("tbcheck"): the theoretical value of a position (cursed wins counted as wins, blessed losses
        // as losses) must equal the best value over its moves. Random positions are reached by random play from the FENs.
        public static void Check(int positions)
        {
            if (Largest == 0) { Console.WriteLine("tbcheck: no tablebases loaded (setoption name SyzygyPath value ...)"); return; }
            string[] starts =
            {
                "4k3/8/8/8/8/8/8/4KQ2 w - - 0 1", "4k3/8/8/8/8/8/8/4K3 w - - 0 1", "4k3/3r4/8/8/8/8/3R4/4K3 w - - 0 1",
                "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1", "4k3/4p3/8/8/8/8/4P3/4K3 b - - 0 1", "1k6/8/8/8/8/8/1q6/RR2K3 w - - 0 1",
                "4k3/8/3n4/8/8/8/2B5/4K1N1 w - - 0 1", "4k3/pp6/8/8/8/8/PP6/4K3 w - - 0 1", "8/5k2/8/3pP3/8/8/8/4K3 w - d6 0 1",
                "4k3/8/8/8/8/8/1b6/R3K3 b - - 0 1",
            };
            var known = new (string fen, int wdl)[]
            {
                ("4k3/8/8/8/8/8/8/4KQ2 w - - 0 1", Win), ("4k3/8/8/8/8/8/8/4KQ2 b - - 0 1", Loss),
                ("4k3/8/8/8/8/8/8/4K3 w - - 0 1", Draw), ("4k3/3r4/8/8/8/8/3R4/4K3 w - - 0 1", Draw),
                ("8/8/8/8/8/k7/8/K1N1B3 w - - 0 1", Win), ("8/8/8/8/8/k7/8/K1N1N3 w - - 0 1", Draw),
            };
            int bad = 0;
            var pos = new Position();
            foreach (var (fen, w) in known)
            {
                pos.SetFen(fen);
                int got = ProbeWdl(pos);
                if (Collapse(got) != Collapse(w)) { bad++; Console.WriteLine($"known position wrong: {fen} expected {w} got {got}"); }
            }
            var rng = new Random(7);
            var list = new ExtMove[C.MaxMoves];
            int checks = 0, skipped = 0;
            for (int i = 0; i < positions; i++)
            {
                pos.SetFen(starts[i % starts.Length]);
                int plies = rng.Next(0, 12);
                for (int k = 0; k < plies; k++)
                {
                    int cnt = MoveGen.GenerateLegal(pos, list);
                    if (cnt == 0) break;
                    pos.MakeMove(list[rng.Next(cnt)].Move);
                }
                if (BB.PopCount(pos.Occupied) > Largest) { skipped++; continue; }
                int v = ProbeWdl(pos);
                if (v < 0) { skipped++; continue; }
                int n = MoveGen.GenerateLegal(pos, list);
                int best;
                if (n == 0) best = pos.InCheck() ? -1 : 0;
                else
                {
                    best = -1;
                    bool ok = true;
                    for (int k = 0; k < n && ok; k++)
                    {
                        pos.MakeMove(list[k].Move);
                        int c = ProbeWdl(pos);
                        if (c < 0) ok = false; else best = Math.Max(best, -Collapse(c));
                        pos.UnmakeMove(list[k].Move);
                    }
                    if (!ok) { skipped++; continue; }
                }
                checks++;
                if (best != Collapse(v)) { bad++; if (bad < 10) Console.WriteLine($"mismatch {pos.GetFen()} probe {v} children {best}"); }
            }
            Console.WriteLine($"tbcheck: {checks} positions checked, {skipped} skipped, {bad} mismatches");
        }

        static int Collapse(int wdl) => wdl >= CursedWin ? 1 : wdl <= BlessedLoss ? -1 : 0;
    }
}
