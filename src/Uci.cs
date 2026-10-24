using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Dolphin2
{
    public static class Uci
    {
        public const string EngineName = "Dolphin 2.2.1";
        public const string Author = "Nguyen Viet Huy";

        static int hashMb = 64;
        static readonly ManualResetEventSlim warmupDone = new ManualResetEventSlim(false);

        public static void Init()
        {
            BB.Init();
            Zobrist.Init();
            TT.Resize(hashMb);
            NNUE.TryLoadDefault();
            SearchManager.RefreshEvalMode();
        }

        public static void Loop(string[] args)
        {
            // Warm up the JIT in the background so the first real search does not lose time.
            new Thread(() =>
            {
                try { Bench.Warmup(); } catch { }
                warmupDone.Set();
            }) { IsBackground = true }.Start();

            if (args.Length > 0)
            {
                // command line mode: "Dolphin2 bench", "Dolphin2 perft 5"
                warmupDone.Wait();
                Execute(string.Join(" ", args));
                SearchManager.Wait();
                return;
            }

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (!Execute(line)) break;
            }
            SearchManager.Stop = true;
            SearchManager.Wait();
        }

        static bool Execute(string line)
        {
            line = line.Trim();
            // tolerate a byte-order mark or other junk some hosts put before the first command
            int k = 0;
            while (k < line.Length && !char.IsLetter(line[k])) k++;
            line = line.Substring(k);
            if (line.Length == 0) return true;
            string[] tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = tok[0];
            switch (cmd)
            {
                case "uci":
                    SearchManager.Send("id name " + EngineName + (NNUE.Enabled ? " NNUE" : ""));
                    SearchManager.Send("id author " + Author);
                    SearchManager.Send("option name Hash type spin default 64 min 1 max 65536");
                    SearchManager.Send("option name Threads type spin default 1 min 1 max 256");
                    SearchManager.Send("option name Move Overhead type spin default 50 min 0 max 5000");
                    SearchManager.Send("option name Ponder type check default false");
                    SearchManager.Send("option name Clear Hash type button");
                    SearchManager.Send("option name UseNNUE type check default " + (NNUE.Enabled ? "true" : "false"));
                    SearchManager.Send("option name EvalFile type string default " + NNUE.DefaultFile);
                    SearchManager.Send("uciok");
                    break;
                case "isready":
                    warmupDone.Wait();
                    SearchManager.Send("readyok");
                    break;
                case "ucinewgame":
                    warmupDone.Wait();
                    SearchManager.NewGame();
                    break;
                case "setoption":
                    warmupDone.Wait();
                    SetOption(line);
                    break;
                case "position":
                    warmupDone.Wait();
                    SearchManager.Wait();
                    ParsePosition(tok);
                    break;
                case "go":
                    warmupDone.Wait();
                    ParseGo(tok);
                    break;
                case "stop":
                    SearchManager.Pondering = false;
                    SearchManager.Limits.Infinite = false;
                    SearchManager.Stop = true;
                    break;
                case "ponderhit":
                    SearchManager.PonderHit();
                    break;
                case "quit":
                    SearchManager.Stop = true;
                    SearchManager.Pondering = false;
                    SearchManager.Limits.Infinite = false;
                    return false;
                case "d":
                    warmupDone.Wait(); // the warm-up search shares the root position
                    Console.Write(SearchManager.RootPosition.Pretty());
                    break;
                case "eval":
                    {
                        warmupDone.Wait();
                        var p = SearchManager.RootPosition;
                        var pt = new Eval.PawnTable();
                        int cl = Eval.EvaluateClassic(p, pt);
                        Console.WriteLine("Classical eval (side to move): " + cl);
                        if (NNUE.Enabled)
                        {
                            Console.WriteLine("NNUE eval (side to move): " + NNUE.EvaluateFresh(p));
                        }
                        break;
                    }
                case "perft":
                    {
                        warmupDone.Wait();
                        int d = tok.Length > 1 ? int.Parse(tok[1]) : 5;
                        Perft.Divide(SearchManager.RootPosition, d);
                        break;
                    }
                case "bench":
                    {
                        warmupDone.Wait();
                        int d = tok.Length > 1 ? int.Parse(tok[1]) : 13;
                        Bench.Run(d);
                        break;
                    }
                case "datagen":
                    DataGen.Run(tok);
                    break;
                case "train":
                    Trainer.Run(tok);
                    break;
                case "export":
                    Trainer.ExportFromFloat(tok[1], tok[2]);
                    break;
                case "nnuecheck":
                    NNUECheck(tok);
                    break;
                default:
                    if (tok.Length > 0 && cmd != "debug" && cmd != "register")
                        SearchManager.Send("info string unknown command: " + line);
                    break;
            }
            return true;
        }

        // compares incremental NNUE evaluation against a from-scratch evaluation along random games
        static void NNUECheck(string[] tok)
        {
            if (!NNUE.Enabled) { Console.WriteLine("NNUE not loaded"); return; }
            var rng = new Random(1);
            var pos = new Position();
            pos.EnableNNUE();
            var list = new ExtMove[C.MaxMoves];
            int bad = 0, checks = 0;
            for (int g = 0; g < 200; g++)
            {
                pos.SetFen(Position.StartFen);
                var made = new System.Collections.Generic.List<int>();
                for (int ply = 0; ply < 120; ply++)
                {
                    int n = MoveGen.GenerateLegal(pos, list);
                    if (n == 0) break;
                    int m = list[rng.Next(n)].Move;
                    pos.MakeMove(m); made.Add(m);
                    int a = NNUE.Evaluate(pos), b = NNUE.EvaluateFresh(pos);
                    checks++;
                    if (a != b) { bad++; if (bad < 5) Console.WriteLine($"mismatch {a} {b} {pos.GetFen()}"); }
                }
                for (int i = made.Count - 1; i >= 0; i--) pos.UnmakeMove(made[i]);
                int c = NNUE.Evaluate(pos), d = NNUE.EvaluateFresh(pos);
                if (c != d) { bad++; Console.WriteLine("unmake mismatch"); }
            }
            Console.WriteLine($"nnuecheck: {checks} checks, {bad} mismatches");
        }

        static void SetOption(string line)
        {
            // setoption name <id> [value <x>]
            int ni = line.IndexOf(" name ", StringComparison.Ordinal);
            if (ni < 0) return;
            int vi = line.IndexOf(" value ", StringComparison.Ordinal);
            string name = vi > 0 ? line.Substring(ni + 6, vi - ni - 6).Trim() : line.Substring(ni + 6).Trim();
            string value = vi > 0 ? line.Substring(vi + 7).Trim() : "";
            switch (name.ToLowerInvariant())
            {
                case "hash":
                    if (int.TryParse(value, out int mb))
                    {
                        SearchManager.Wait();
                        hashMb = Math.Clamp(mb, 1, 65536);
                        TT.Resize(hashMb);
                    }
                    break;
                case "threads":
                    if (int.TryParse(value, out int th)) SearchManager.SetThreads(th);
                    break;
                case "move overhead":
                    if (int.TryParse(value, out int mo)) SearchManager.MoveOverhead = Math.Clamp(mo, 0, 5000);
                    break;
                case "clear hash":
                    SearchManager.NewGame();
                    break;
                case "usennue":
                    SearchManager.Wait();
                    NNUE.SetEnabled(value.ToLowerInvariant() == "true");
                    SearchManager.Send("info string NNUE " + (NNUE.Enabled ? "enabled" : "disabled"));
                    break;
                case "evalfile":
                    SearchManager.Wait();
                    if (NNUE.Load(value)) { SearchManager.RefreshEvalMode(); SearchManager.Send("info string loaded network " + value); }
                    else SearchManager.Send("info string could not load network " + value + ", using classical eval");
                    break;
                case "ponder":
                    break;
            }
        }

        static void ParsePosition(string[] tok)
        {
            string fen;
            int i = 1;
            if (tok.Length > 1 && tok[1] == "startpos") { fen = Position.StartFen; i = 2; }
            else if (tok.Length > 1 && tok[1] == "fen")
            {
                var parts = new List<string>();
                i = 2;
                while (i < tok.Length && tok[i] != "moves") parts.Add(tok[i++]);
                fen = string.Join(" ", parts);
            }
            else return;
            var moves = new List<string>();
            if (i < tok.Length && tok[i] == "moves")
                for (i++; i < tok.Length; i++) moves.Add(tok[i]);
            if (!SearchManager.SetPosition(fen, moves))
                SearchManager.Send("info string illegal move in position command");
        }

        static void ParseGo(string[] tok)
        {
            var l = new SearchLimits();
            for (int i = 1; i < tok.Length; i++)
            {
                string t = tok[i];
                long Next() => i + 1 < tok.Length && long.TryParse(tok[i + 1], out long v) ? v : 0;
                switch (t)
                {
                    case "wtime": l.WTime = Next(); i++; break;
                    case "btime": l.BTime = Next(); i++; break;
                    case "winc": l.WInc = Next(); i++; break;
                    case "binc": l.BInc = Next(); i++; break;
                    case "movestogo": l.MovesToGo = (int)Next(); i++; break;
                    case "depth": l.Depth = (int)Next(); i++; break;
                    case "nodes": l.Nodes = Next(); i++; break;
                    case "movetime": l.MoveTime = Next(); i++; break;
                    case "mate": l.Mate = (int)Next(); i++; break;
                    case "infinite": l.Infinite = true; break;
                    case "ponder": l.Ponder = true; break;
                    case "perft":
                        Perft.Divide(SearchManager.RootPosition, (int)Next());
                        return;
                }
            }
            SearchManager.Go(l);
        }
    }

    public static class Program
    {
        public static void Main(string[] args)
        {
            // Own reader on the raw stdin stream: setting Console.InputEncoding calls SetConsoleCP, which throws
            // when a GUI starts the engine without a console (DETACHED_PROCESS) and kills it before "uci".
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.ASCII, false));
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
            Console.SetOut(stdout);
            Console.WriteLine(Uci.EngineName + " by " + Uci.Author + " (C# bitboard UCI engine, NNUE)");
            Uci.Init();
            Uci.Loop(args);
        }
    }
}
