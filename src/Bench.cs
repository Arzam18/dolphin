using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Dolphin2
{
    public static class Bench
    {
        public static readonly string[] Fens =
        {
            Position.StartFen,
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
            "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",
            "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
            "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
            "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
            "r1bq1rk1/ppp2ppp/2np1n2/2b1p3/2B1P3/2PP1N2/PP3PPP/RNBQ1RK1 w - - 0 7",
            "rnbqkb1r/pp2pppp/3p1n2/8/3NP3/8/PPP2PPP/RNBQKB1R w KQkq - 1 5",
            "r2q1rk1/pp2bppp/2n1pn2/3p4/2PP4/2N1PN2/PP3PPP/R2QKB1R w KQ - 0 9",
            "2r2rk1/pp1bqppp/3p1n2/4p3/2P1P3/2N1BP2/PP1Q2PP/R4RK1 w - - 0 15",
            "r1b2rk1/2q1bppp/p2p1n2/np2p3/3PP3/5N1P/PPBN1PP1/R1BQR1K1 b - - 0 13",
            "8/8/4k3/3p4/3P4/4K3/8/8 w - - 0 1",
            "6k1/5ppp/8/8/8/8/5PPP/3R2K1 w - - 0 1",
            "8/5pk1/6p1/7p/7P/6P1/5PK1/3r4 w - - 0 50",
            "4rrk1/pp3ppp/2pq4/3n4/3P4/P1NQ2P1/1P3P1P/R3R1K1 b - - 0 20",
            "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/3P1N2/PPP2PPP/RNBQK2R w KQkq - 1 5",
            "3r1rk1/p4ppp/1pn1p3/2q5/2P5/1P2PN2/P1Q2PPP/3R1RK1 w - - 0 18",
        };

        public static void Warmup()
        {
            bool old = SearchManager.Silent;
            SearchManager.Silent = true;
            foreach (var fen in new[] { Fens[1], Fens[5] })
            {
                SearchManager.SetPosition(fen, new List<string>());
                SearchManager.Go(new SearchLimits { Depth = 7 });
                SearchManager.Wait();
            }
            SearchManager.NewGame();
            SearchManager.SetPosition(Position.StartFen, new List<string>());
            SearchManager.Silent = old;
        }

        public static void Run(int depth)
        {
            bool old = SearchManager.Silent;
            SearchManager.Silent = true;
            long total = 0;
            var sw = Stopwatch.StartNew();
            foreach (var fen in Fens)
            {
                SearchManager.NewGame();
                SearchManager.SetPosition(fen, new List<string>());
                SearchManager.Go(new SearchLimits { Depth = depth });
                SearchManager.Wait();
                long n = SearchManager.TotalNodes();
                total += n;
                Console.WriteLine($"{fen,-80} {Mv.ToUci(SearchManager.LastBestMove),6} {SearchManager.LastScore,6} {n,10}");
            }
            double sec = Math.Max(sw.Elapsed.TotalSeconds, 1e-3);
            SearchManager.Silent = old;
            Console.WriteLine();
            Console.WriteLine("Nodes searched  : " + total);
            Console.WriteLine("Time (ms)       : " + (long)(sec * 1000));
            Console.WriteLine("Nodes/second    : " + (long)(total / sec));
            SearchManager.NewGame();
            SearchManager.SetPosition(Position.StartFen, new List<string>());
        }
    }
}
