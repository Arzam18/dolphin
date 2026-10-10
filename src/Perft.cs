using System;
using System.Diagnostics;

namespace Dolphin2
{
    public static class Perft
    {
        static ExtMove[][] lists;

        public static long Run(Position pos, int depth)
        {
            if (lists == null || lists.Length <= depth)
            {
                lists = new ExtMove[Math.Max(depth + 1, 32)][];
                for (int i = 0; i < lists.Length; i++) lists[i] = new ExtMove[C.MaxMoves];
            }
            return Recurse(pos, depth);
        }

        static long Recurse(Position pos, int depth)
        {
            var list = lists[depth];
            int n = MoveGen.Generate(pos, list, 0, pos.Checkers != 0 ? MoveGen.Evasions : MoveGen.NonEvasions);
            long nodes = 0;
            for (int i = 0; i < n; i++)
            {
                int m = list[i].Move;
                if (!pos.IsLegal(m)) continue;
                if (depth == 1) { nodes++; continue; }
                pos.MakeMove(m);
                nodes += Recurse(pos, depth - 1);
                pos.UnmakeMove(m);
            }
            return nodes;
        }

        public static void Divide(Position pos, int depth)
        {
            var sw = Stopwatch.StartNew();
            var list = new ExtMove[C.MaxMoves];
            int n = MoveGen.GenerateLegal(pos, list);
            long total = 0;
            for (int i = 0; i < n; i++)
            {
                int m = list[i].Move;
                long c = 1;
                if (depth > 1)
                {
                    pos.MakeMove(m);
                    c = Run(pos, depth - 1);
                    pos.UnmakeMove(m);
                }
                total += c;
                Console.WriteLine(Mv.ToUci(m) + ": " + c);
            }
            double sec = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
            Console.WriteLine();
            Console.WriteLine("Nodes searched: " + total);
            Console.WriteLine("Time: " + (int)(sec * 1000) + " ms, nps " + (long)(total / sec));
        }
    }
}
