using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Dolphin2
{
    public sealed class SearchLimits
    {
        public long WTime = -1, BTime = -1, WInc, BInc, MoveTime = -1, Nodes;
        public int MovesToGo, Depth, Mate;
        public bool Infinite, Ponder;
    }

    // State shared by the threads cooperating on one search (time, stop flag, limits).
    public sealed class SearchContext
    {
        public volatile bool Stop;
        public volatile bool Pondering;
        public SearchLimits Limits = new SearchLimits();
        public bool UseTime;
        public double SoftMs, HardMs;
        public bool Silent;
        public SearchThread[] Threads;
        readonly Stopwatch clock = new Stopwatch();

        public long ElapsedMs() => clock.ElapsedMilliseconds;
        public void RestartClock() => clock.Restart();

        public long TotalNodes()
        {
            long n = 0;
            foreach (var t in Threads) n += t.Nodes;
            return n;
        }

        public void CheckTime()
        {
            if (Limits.Nodes > 0 && TotalNodes() >= Limits.Nodes) { Stop = true; return; }
            if (Pondering || !UseTime) return;
            if (clock.ElapsedMilliseconds >= HardMs) Stop = true;
        }

        static readonly int TmMovesLeft = (int)Tune.Get("MTG", 26);
        static readonly double TmHardMul = Tune.Get("HARDMUL", 3.5);

        public void ComputeTime(int side, int moveOverhead)
        {
            UseTime = false;
            SoftMs = HardMs = double.MaxValue;
            if (Limits.Infinite) return;
            if (Limits.MoveTime > 0)
            {
                UseTime = true;
                SoftMs = HardMs = Math.Max(1, Limits.MoveTime - moveOverhead);
                return;
            }
            long time = side == C.White ? Limits.WTime : Limits.BTime;
            long inc = side == C.White ? Limits.WInc : Limits.BInc;
            if (time < 0) return;
            UseTime = true;
            double t = Math.Max(1, time - moveOverhead);
            int mtg = Limits.MovesToGo > 0 ? Math.Min(Limits.MovesToGo, 40) : TmMovesLeft;
            double soft = t / mtg + inc * 0.75;
            double hard = soft * TmHardMul;
            if (Limits.MovesToGo == 1) { soft = t * 0.5; hard = t * 0.7; }
            SoftMs = Math.Max(1, Math.Min(soft, t * 0.45));
            HardMs = Math.Max(1, Math.Min(hard, t * 0.7));
        }

        public void PrintInfo(SearchThread t, int depth, int score, int bound)
        {
            if (Silent) return;
            long ms = Math.Max(1, clock.ElapsedMilliseconds);
            long nodes = TotalNodes();
            string b = bound == TT.BoundLower ? " lowerbound" : bound == TT.BoundUpper ? " upperbound" : "";
            SearchManager.Send($"info depth {depth} seldepth {Math.Max(depth, t.SelDepth)} multipv 1 score {SearchManager.ScoreString(score)}{b} nodes {nodes} nps {nodes * 1000 / ms} hashfull {TT.HashFull()} time {ms} pv {t.PvString()}");
        }
    }

    public static class SearchManager
    {
        public static readonly SearchContext Ctx = new SearchContext();
        public static int MoveOverhead = 50;

        static SearchThread[] threads = { new SearchThread(0) { Ctx = Ctx } };
        static Thread[] osThreads = Array.Empty<Thread>();
        static Thread mainRunner;
        static readonly object outLock = new object();
        static string rootFen = Position.StartFen;
        static List<string> rootMoves = new List<string>();

        static SearchManager() { Ctx.Threads = threads; }

        public static bool Stop { get => Ctx.Stop; set => Ctx.Stop = value; }
        public static bool Pondering { get => Ctx.Pondering; set => Ctx.Pondering = value; }
        public static bool Silent { get => Ctx.Silent; set => Ctx.Silent = value; }
        public static SearchLimits Limits => Ctx.Limits;
        public static int ThreadCount => threads.Length;
        public static long TotalNodes() => Ctx.TotalNodes();

        public static void SetThreads(int n)
        {
            Wait();
            n = Math.Clamp(n, 1, 256);
            if (n == threads.Length) return;
            var t = new SearchThread[n];
            for (int i = 0; i < n; i++) t[i] = i < threads.Length ? threads[i] : new SearchThread(i) { Ctx = Ctx };
            threads = t;
            Ctx.Threads = threads;
            RefreshEvalMode();
        }

        public static void RefreshEvalMode()
        {
            Wait();
            foreach (var t in threads)
            {
                if (NNUE.Enabled) t.Pos.EnableNNUE(); else t.Pos.DisableNNUE();
            }
            SetPosition(rootFen, rootMoves);
        }

        public static void NewGame()
        {
            Wait();
            foreach (var t in threads) t.ClearHistory();
            TT.Clear(threads.Length);
        }

        public static bool SetPosition(string fen, List<string> moves)
        {
            rootFen = fen;
            rootMoves = new List<string>(moves);
            bool ok = true;
            foreach (var t in threads)
            {
                t.Pos.SetFen(fen);
                foreach (var mv in moves)
                {
                    int m = t.Pos.ParseUciMove(mv);
                    if (m == Mv.None) { ok = false; break; }
                    t.Pos.MakeMove(m);
                }
                t.Pos.Acc?.Refresh(t.Pos); // search root = accumulator stack bottom
            }
            return ok;
        }

        public static Position RootPosition => threads[0].Pos;

        public static void Go(SearchLimits limits)
        {
            Wait();
            Ctx.Limits = limits;
            Ctx.Stop = false;
            Ctx.Pondering = limits.Ponder;
            Ctx.RestartClock();
            Ctx.ComputeTime(threads[0].Pos.SideToMove, MoveOverhead);
            TT.NewSearch();

            mainRunner = new Thread(RunSearch, 64 * 1024 * 1024) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            mainRunner.Start();
        }

        public static void PonderHit()
        {
            // our clock starts now
            Ctx.RestartClock();
            Ctx.Pondering = false;
        }

        static void RunSearch()
        {
            osThreads = new Thread[threads.Length - 1];
            for (int i = 1; i < threads.Length; i++)
            {
                var t = threads[i];
                osThreads[i - 1] = new Thread(() => t.Run(), 64 * 1024 * 1024) { IsBackground = true };
                osThreads[i - 1].Start();
            }
            threads[0].Run();

            // In ponder / infinite mode we may not answer before the GUI says so
            while (!Ctx.Stop && (Ctx.Pondering || Ctx.Limits.Infinite)) Thread.Sleep(1);
            Ctx.Stop = true;
            foreach (var th in osThreads) th.Join();

            var main = threads[0];
            int best = main.BestMove;
            int ponder = main.PonderMove;
            int bestScore = main.BestScore;
            // a helper that got clearly deeper with a better score may override the main thread
            for (int i = 1; i < threads.Length; i++)
            {
                var t = threads[i];
                if (t.CompletedDepth > main.CompletedDepth + 1 && t.BestScore > bestScore && t.BestMove != Mv.None)
                {
                    best = t.BestMove; ponder = t.PonderMove; bestScore = t.BestScore;
                }
            }
            if (!Ctx.Silent)
            {
                string ps = "";
                if (ponder != Mv.None && best != Mv.None)
                {
                    var p = main.Pos;
                    p.MakeMove(best);
                    if (p.IsPseudoLegal(ponder) && p.IsLegal(ponder)) ps = " ponder " + Mv.ToUci(ponder);
                    p.UnmakeMove(best);
                }
                Send("bestmove " + Mv.ToUci(best) + ps);
            }
            LastBestMove = best;
            LastScore = bestScore;
        }

        public static int LastBestMove, LastScore;

        public static void Wait()
        {
            mainRunner?.Join();
        }

        public static void Send(string s)
        {
            lock (outLock)
            {
                Console.Out.Write(s + "\n");
                Console.Out.Flush();
            }
        }

        public static string ScoreString(int score)
        {
            if (score >= C.MateInMaxPly) return "mate " + ((C.Mate - score + 1) / 2);
            if (score <= -C.MateInMaxPly) return "mate " + (-(C.Mate + score) / 2);
            return "cp " + score;
        }
    }
}
