using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dolphin2
{
    public struct StackEntry
    {
        public int StaticEval;
        public int Move;
        public int ContIdx;     // row index into continuation history (piece*64+to), 768 = none
        public int Killer1, Killer2;
        public int Excluded;
        public int DoubleExt;
        public bool InCheck;
        public bool TtPv;
        public int Cutoffs;
        public int RawEval;
        public int MoveCount;
    }

    public sealed partial class SearchThread
    {
        public static bool CheckExt = Environment.GetEnvironmentVariable("DOLPHIN_CHECKEXT") != null; // off: +14 Elo (SPRT, 1700 games)
        const int Off = 6; // stack offset so that ss[ply - 6] is always valid
        const int NoCont = 768;

        public readonly int Id;
        public SearchContext Ctx;
        public Position Pos = new Position();
        public readonly Eval.PawnTable PawnTable = new Eval.PawnTable();
        readonly StackEntry[] ss = new StackEntry[C.MaxPly + Off + 4];
        readonly MovePicker[] pickers = new MovePicker[C.MaxPly + 2];
        readonly MovePicker[] pickersSe = new MovePicker[C.MaxPly + 2]; // used by singular-extension searches
        readonly int[] pv = new int[(C.MaxPly + 2) * (C.MaxPly + 2)];
        readonly int[] pvLen = new int[C.MaxPly + 2];
        readonly int[][] quietsTried = new int[C.MaxPly + 2][];
        readonly int[][] capsTried = new int[C.MaxPly + 2][];

        // History tables
        readonly int[] mainHist = new int[2 * 64 * 64];
        readonly int[] contHist = new int[(NoCont + 1) * 768];
        readonly int[] capHist = new int[12 * 64 * 6];
        readonly int[] counterMoves = new int[12 * 64];
        readonly long[] rootNodes = new long[64 * 64];
        // Correction history: what searches found versus the static eval, keyed by pawn structure, by each side's
        // other pieces, and by the last two move pairs. Entries are in 1/32 cp and saturate at 64 cp.
        const int CorrSize = 16384, CorrGrain = 32, CorrMax = CorrGrain * 64;
        readonly int[] pawnCorr = new int[2 * CorrSize];
        readonly int[] npCorrW = new int[2 * CorrSize];
        readonly int[] npCorrB = new int[2 * CorrSize];
        readonly int[] contCorr = new int[(NoCont + 1) * 768];
        static readonly int CorrWPawn = (int)Tune.Get("CORRWP", 128), CorrWNp = (int)Tune.Get("CORRWNP", 80);
        static readonly int CorrWCont2 = (int)Tune.Get("CORRWC2", 80), CorrWCont4 = (int)Tune.Get("CORRWC4", 48);
        static readonly int CorrRate = (int)Tune.Get("CORRRATE", 12);

        public long Nodes;
        public int SelDepth;
        public int CompletedDepth;
        public int BestMove, PonderMove, BestScore;
        public int RootDepth;
        int nmpMinPly;
        int rootLegal;

        static readonly int[,] Reductions = new int[64, 64];
        static readonly double LmrBase = Tune.Get("LMRBASE", 0.75), LmrDiv = Tune.Get("LMRDIV", 2.0);
        static readonly int RfpMargin = (int)Tune.Get("RFP", 80), NmpBase = (int)Tune.Get("NMPBASE", 4);
        static readonly int FutBase = (int)Tune.Get("FUTBASE", 110), FutMul = (int)Tune.Get("FUTMUL", 100);
        static readonly int HistPrune = (int)Tune.Get("HISTPRUNE", 4000), SeDouble = (int)Tune.Get("SEDOUBLE", 20);
        static readonly int[,] LmpLimit = new int[2, 16];

        static SearchThread()
        {
            for (int d = 1; d < 64; d++)
                for (int m = 1; m < 64; m++)
                    Reductions[d, m] = (int)(LmrBase + Math.Log(d) * Math.Log(m) / LmrDiv);
            for (int d = 0; d < 16; d++)
            {
                LmpLimit[0, d] = (3 + d * d) / 2;
                LmpLimit[1, d] = 3 + d * d;
            }
        }

        public SearchThread(int id)
        {
            Id = id;
            for (int i = 0; i < pickers.Length; i++)
            {
                pickers[i] = new MovePicker();
                pickersSe[i] = new MovePicker();
                quietsTried[i] = new int[64];
                capsTried[i] = new int[32];
            }
        }

        public void ClearHistory()
        {
            Array.Clear(mainHist, 0, mainHist.Length);
            Array.Clear(contHist, 0, contHist.Length);
            Array.Clear(capHist, 0, capHist.Length);
            Array.Clear(counterMoves, 0, counterMoves.Length);
            Array.Clear(pawnCorr, 0, pawnCorr.Length);
            Array.Clear(npCorrW, 0, npCorrW.Length);
            Array.Clear(npCorrB, 0, npCorrB.Length);
            Array.Clear(contCorr, 0, contCorr.Length);
            PawnTable.Clear();
        }

        // ---------------------------------------------------------------- history helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CaptureHistory(int piece, int to, int victim) => capHist[(piece * 64 + to) * 6 + victim];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int QuietScore(Position p, int m, int ply)
        {
            int from = Mv.From(m), to = Mv.To(m);
            int pc = p.Board[from];
            int idx = pc * 64 + to;
            int s = ply + Off;
            return mainHist[(p.SideToMove << 12) | (from << 6) | to]
                 + 2 * contHist[ss[s - 1].ContIdx * 768 + idx]
                 + contHist[ss[s - 2].ContIdx * 768 + idx]
                 + contHist[ss[s - 4].ContIdx * 768 + idx] / 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Gravity(ref int h, int bonus, int max)
        {
            h += bonus - h * Math.Abs(bonus) / max;
        }

        void UpdateQuietHistory(int side, int m, int pc, int ply, int bonus)
        {
            int from = Mv.From(m), to = Mv.To(m);
            Gravity(ref mainHist[(side << 12) | (from << 6) | to], bonus, 16384);
            int idx = pc * 64 + to;
            int s = ply + Off;
            if (ss[s - 1].ContIdx != NoCont) Gravity(ref contHist[ss[s - 1].ContIdx * 768 + idx], bonus, 16384);
            if (ss[s - 2].ContIdx != NoCont) Gravity(ref contHist[ss[s - 2].ContIdx * 768 + idx], bonus, 16384);
            if (ss[s - 4].ContIdx != NoCont) Gravity(ref contHist[ss[s - 4].ContIdx * 768 + idx], bonus, 16384);
        }

        void UpdateCaptureHistory(int m, int bonus)
        {
            int to = Mv.To(m);
            int pc = Pos.Board[Mv.From(m)];
            int victim = Mv.IsEp(m) ? C.Pawn : Pos.Board[to] == C.NoPiece ? 0 : C.TypeOf(Pos.Board[to]);
            Gravity(ref capHist[(pc * 64 + to) * 6 + victim], bonus, 16384);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int StatBonus(int depth) => Math.Min(170 * depth - 100, 1800);

        // moves that failed are punished harder than the winner is rewarded, and saturate early
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int StatPenalty(int depth) => Math.Min(655 * depth - 106, 1267);

        void UpdateStats(int bestMove, int depth, int ply, int[] quiets, int nq, int[] caps, int nc)
        {
            int s = ply + Off;
            int bonus = StatBonus(depth);
            int penalty = StatPenalty(depth);
            int us = Pos.SideToMove;
            if (!Mv.IsTactical(bestMove))
            {
                if (ss[s].Killer1 != bestMove) { ss[s].Killer2 = ss[s].Killer1; ss[s].Killer1 = bestMove; }
                int prev = ss[s - 1].Move;
                if (prev != Mv.None && prev != Mv.Null && ss[s - 1].ContIdx != NoCont)
                    counterMoves[ss[s - 1].ContIdx] = bestMove;
                // a first-move cutoff at low depth says little: do not let it move the history tables
                if (nq == 0 && depth <= 3) return;
                UpdateQuietHistory(us, bestMove, Pos.Board[Mv.From(bestMove)], ply, bonus);
                for (int i = 0; i < nq; i++)
                    UpdateQuietHistory(us, quiets[i], Pos.Board[Mv.From(quiets[i])], ply, -penalty);
            }
            else UpdateCaptureHistory(bestMove, bonus);
            for (int i = 0; i < nc; i++) UpdateCaptureHistory(caps[i], -penalty);
        }

        // ---------------------------------------------------------------- helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int DrawScore() => 1 - (int)(Nodes & 2);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Evaluate()
        {
            // scale the evaluation down as material comes off: a given advantage is harder to convert with fewer pieces
            int v = Eval.Evaluate(Pos, PawnTable);
            int mat = Pos.NonPawnMaterial[0] + Pos.NonPawnMaterial[1] + 100 * BB.PopCount(Pos.PieceTypeBB(C.Pawn));
            return v * (MatScaleBase + mat) / (MatScaleBase + 8200);
        }
        static readonly int MatScaleBase = (int)Tune.Get("MATSCALE", 24000);

        // static evaluation corrected by what earlier searches found in similar positions
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Corrected(int raw, int ply)
        {
            int s = ply + Off, stm = Pos.SideToMove * CorrSize;
            long c = (long)CorrWPawn * pawnCorr[stm + (int)(Pos.PawnKey & (CorrSize - 1))]
                   + (long)CorrWNp * (npCorrW[stm + (int)(Pos.NonPawnKey[0] & (CorrSize - 1))]
                                     + npCorrB[stm + (int)(Pos.NonPawnKey[1] & (CorrSize - 1))]);
            int last = ss[s - 1].ContIdx;
            if (last != NoCont)
            {
                if (ss[s - 2].ContIdx != NoCont) c += (long)CorrWCont2 * contCorr[ss[s - 2].ContIdx * 768 + last];
                if (ss[s - 4].ContIdx != NoCont) c += (long)CorrWCont4 * contCorr[ss[s - 4].ContIdx * 768 + last];
            }
            return Math.Clamp(raw + (int)(c / (128 * CorrGrain)), -C.MateInMaxPly + 1, C.MateInMaxPly - 1);
        }

        // diff is measured against the corrected eval, so the tables together converge to the remaining error
        void UpdateCorrection(int staticEval, int score, int depth, int ply)
        {
            int s = ply + Off, stm = Pos.SideToMove * CorrSize;
            int bonus = Math.Clamp((score - staticEval) * depth * CorrRate / 16 * CorrGrain / 32, -CorrMax / 4, CorrMax / 4);
            Gravity(ref pawnCorr[stm + (int)(Pos.PawnKey & (CorrSize - 1))], bonus, CorrMax);
            Gravity(ref npCorrW[stm + (int)(Pos.NonPawnKey[0] & (CorrSize - 1))], bonus, CorrMax);
            Gravity(ref npCorrB[stm + (int)(Pos.NonPawnKey[1] & (CorrSize - 1))], bonus, CorrMax);
            int last = ss[s - 1].ContIdx;
            if (last != NoCont)
            {
                if (ss[s - 2].ContIdx != NoCont) Gravity(ref contCorr[ss[s - 2].ContIdx * 768 + last], bonus, CorrMax);
                if (ss[s - 4].ContIdx != NoCont) Gravity(ref contCorr[ss[s - 4].ContIdx * 768 + last], bonus, CorrMax);
            }
        }

        void UpdatePv(int ply, int m)
        {
            int b = ply * (C.MaxPly + 2);
            int c = (ply + 1) * (C.MaxPly + 2);
            pv[b + ply] = m;
            int len = pvLen[ply + 1];
            for (int i = ply + 1; i < len; i++) pv[b + i] = pv[c + i];
            pvLen[ply] = Math.Max(len, ply + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckTime()
        {
            if (Id == 0 && (Nodes & 1023) == 0) Ctx.CheckTime();
        }

        // ---------------------------------------------------------------- iterative deepening
        public void Run()
        {
            Nodes = 0;
            SelDepth = 0;
            CompletedDepth = 0;
            BestMove = Mv.None;
            PonderMove = Mv.None;
            BestScore = -C.Infinite;
            nmpMinPly = 0;
            Array.Clear(rootNodes, 0, rootNodes.Length);
            for (int i = 0; i < ss.Length; i++)
            {
                ss[i] = default;
                ss[i].ContIdx = NoCont;
                ss[i].StaticEval = C.NoScore;
            }

            var tmp = new ExtMove[C.MaxMoves];
            rootLegal = MoveGen.GenerateLegal(Pos, tmp);
            if (rootLegal == 0)
            {
                BestMove = Mv.None;
                BestScore = Pos.InCheck() ? -C.Mate : 0;
                return;
            }
            int fallback = tmp[0].Move;

            int maxDepth = Ctx.Limits.Depth > 0 ? Math.Min(Ctx.Limits.Depth, C.MaxPly - 4) : C.MaxPly - 4;
            int prevScore = 0;
            int prevBest = Mv.None;
            int stability = 0;
            double scoreTrend = 0;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                // helper threads skip some depths for diversity
                if (Id > 0 && depth > 1 && depth < maxDepth && ((depth + Id) % 4 == 0)) continue;

                RootDepth = depth;
                SelDepth = 0;
                int alpha = -C.Infinite, beta = C.Infinite, delta = 15;
                if (depth >= 4)
                {
                    delta = 12 + Math.Abs(prevScore) / 60;
                    alpha = Math.Max(prevScore - delta, -C.Infinite);
                    beta = Math.Min(prevScore + delta, C.Infinite);
                }
                int score;
                int failHighReduce = 0;
                while (true)
                {
                    int d = Math.Max(1, depth - failHighReduce);
                    score = Search(alpha, beta, d, 0, false, true);
                    if (Ctx.Stop) break;
                    if (score <= alpha)
                    {
                        beta = (alpha + beta) / 2;
                        alpha = Math.Max(score - delta, -C.Infinite);
                        failHighReduce = 0;
                        if (Id == 0 && Ctx.ElapsedMs() > 2000) Ctx.PrintInfo(this, depth, score, TT.BoundUpper);
                    }
                    else if (score >= beta)
                    {
                        beta = Math.Min(score + delta, C.Infinite);
                        if (Math.Abs(score) < C.MateInMaxPly) failHighReduce = Math.Min(failHighReduce + 1, 3);
                        // the PV of a fail-high is a good move: keep it
                        if (pvLen[0] > 0) { BestMove = pv[0]; PonderMove = pvLen[0] > 1 ? pv[1] : Mv.None; }
                        if (Id == 0 && Ctx.ElapsedMs() > 2000) Ctx.PrintInfo(this, depth, score, TT.BoundLower);
                    }
                    else break;
                    delta += delta / 2;
                    if (delta > 1000) { alpha = -C.Infinite; beta = C.Infinite; }
                }

                if (Ctx.Stop)
                {
                    // keep a partial-iteration best move only if it was found with an exact/lower bound
                    if (CompletedDepth == 0 && pvLen[0] > 0) { BestMove = pv[0]; PonderMove = pvLen[0] > 1 ? pv[1] : Mv.None; }
                    break;
                }

                CompletedDepth = depth;
                BestScore = score;
                if (pvLen[0] > 0)
                {
                    BestMove = pv[0];
                    PonderMove = pvLen[0] > 1 ? pv[1] : Mv.None;
                }

                if (Id != 0) continue;

                Ctx.PrintInfo(this, depth, score, TT.BoundExact);

                // mate found: no need to search much further
                if (Ctx.Limits.Mate > 0 && score >= C.Mate - 2 * Ctx.Limits.Mate) break;

                // time management between iterations
                if (BestMove == prevBest) stability = Math.Min(stability + 1, 10); else stability = 0;
                prevBest = BestMove;
                scoreTrend = 0.5 * scoreTrend + 0.5 * (prevScore - score);
                prevScore = score;

                if (Ctx.UseTime && !Ctx.Pondering)
                {
                    double stabFactor = 1.6 - 0.09 * stability;                    // 1.6 .. 0.7
                    long total = Math.Max(1, Nodes);
                    double bestFrac = rootNodes[(Mv.From(BestMove) << 6) | Mv.To(BestMove)] / (double)total;
                    double nodeFactor = (1.55 - bestFrac) * 1.25;                    // ~0.6 .. 1.9
                    double scoreFactor = Math.Clamp(1.0 + scoreTrend / 120.0, 0.8, 1.6);
                    double limit = Ctx.SoftMs * stabFactor * nodeFactor * scoreFactor;
                    if (rootLegal == 1) limit = Math.Min(limit, Ctx.SoftMs * 0.1);
                    if (Ctx.ElapsedMs() >= limit) break;
                }
                prevScore = score;
            }

            if (BestMove == Mv.None) BestMove = fallback;
        }

        // ---------------------------------------------------------------- main search
        int Search(int alpha, int beta, int depth, int ply, bool cutNode, bool pvNode)
        {
            if (depth <= 0) return QSearch(alpha, beta, ply, pvNode);

            int s = ply + Off;
            bool root = ply == 0;
            pvLen[ply] = ply;
            Nodes++;
            CheckTime();
            if (Ctx.Stop && RootDepth > 1) return 0;
            if (pvNode && ply > SelDepth) SelDepth = ply;

            bool inCheck = Pos.Checkers != 0;
            if (!root)
            {
                if (Pos.IsDraw(ply)) return DrawScore();
                if (ply >= C.MaxPly - 2) return inCheck ? 0 : Evaluate();
                // mate distance pruning
                alpha = Math.Max(alpha, C.MatedIn(ply));
                beta = Math.Min(beta, C.MateIn(ply + 1));
                if (alpha >= beta) return alpha;
            }

            int excluded = ss[s].Excluded;
            ulong key = Pos.Key;
            bool ttHit = false;
            TTData tte = default;
            tte.Score = C.NoScore; tte.Eval = C.NoScore;
            if (excluded == Mv.None) ttHit = TT.Probe(key, out tte);
            int ttMove = root ? (BestMove != Mv.None ? BestMove : (ttHit ? tte.Move : Mv.None)) : ttHit ? tte.Move : Mv.None;
            int ttScore = ttHit ? TT.ScoreFromTT(tte.Score, ply, Pos.Halfmove) : C.NoScore;
            bool ttPv = excluded != Mv.None ? ss[s].TtPv : pvNode || (ttHit && tte.Pv);
            ss[s].TtPv = ttPv;
            ss[s].InCheck = inCheck;
            ss[s + 1].Excluded = Mv.None;
            ss[s + 2].Killer1 = ss[s + 2].Killer2 = Mv.None;
            ss[s].Cutoffs = 0;
            ss[s + 2].Cutoffs = 0;
            if (ttMove != Mv.None && !Pos.IsPseudoLegal(ttMove)) ttMove = Mv.None;

            // TT cutoff
            if (!pvNode && ttHit && tte.Depth >= depth && ttScore != C.NoScore && Pos.Halfmove < 90
                && (tte.Bound == TT.BoundExact
                    || (tte.Bound == TT.BoundLower && ttScore >= beta)
                    || (tte.Bound == TT.BoundUpper && ttScore <= alpha)))
            {
                if (ttMove != Mv.None && ttScore >= beta && !Mv.IsTactical(ttMove))
                {
                    int bonus = StatBonus(depth);
                    UpdateQuietHistory(Pos.SideToMove, ttMove, Pos.Board[Mv.From(ttMove)], ply, bonus);
                    if (ss[s].Killer1 != ttMove) { ss[s].Killer2 = ss[s].Killer1; ss[s].Killer1 = ttMove; }
                }
                if (ttScore >= beta) PriorMoveMalus(ply, depth);
                return ttScore;
            }

            // static evaluation
            int staticEval, eval, rawEval = C.NoScore;
            bool improving = false;
            if (inCheck)
            {
                staticEval = eval = ss[s].StaticEval = C.NoScore;
                ss[s].RawEval = C.NoScore;
            }
            else
            {
                if (excluded != Mv.None) { staticEval = eval = ss[s].StaticEval; rawEval = ss[s].RawEval; }
                else if (ttHit)
                {
                    rawEval = tte.Eval != C.NoScore ? tte.Eval : Evaluate();
                    staticEval = Corrected(rawEval, ply);
                    eval = staticEval;
                    if (ttScore != C.NoScore &&
                        ((ttScore > eval && (tte.Bound & TT.BoundLower) != 0) || (ttScore < eval && (tte.Bound & TT.BoundUpper) != 0)))
                        eval = ttScore;
                }
                else
                {
                    rawEval = Evaluate();
                    staticEval = eval = Corrected(rawEval, ply);
                    TT.Store(key, Mv.None, C.NoScore, rawEval, -7, TT.BoundNone, ttPv);
                }
                ss[s].StaticEval = staticEval;
                ss[s].RawEval = rawEval;
                if (ss[s - 2].StaticEval != C.NoScore) improving = staticEval > ss[s - 2].StaticEval;
                else if (ss[s - 4].StaticEval != C.NoScore) improving = staticEval > ss[s - 4].StaticEval;
                else improving = true;

                // the opponent's last quiet move is rewarded by how much it moved the static eval in its favour
                int prevQuiet = ss[s - 1].Move;
                if (ply > 0 && prevQuiet != Mv.None && prevQuiet != Mv.Null && !ss[s - 1].InCheck
                    && Pos.Captured == C.NoPiece && ss[s - 1].StaticEval != C.NoScore)
                {
                    int bonus = Math.Clamp(-153 * (ss[s - 1].StaticEval + staticEval) / 16, -101, 192);
                    Gravity(ref mainHist[((Pos.SideToMove ^ 1) << 12) | (Mv.From(prevQuiet) << 6) | Mv.To(prevQuiet)], bonus, 16384);
                }
            }

            int us = Pos.SideToMove;

            if (!pvNode && !inCheck && excluded == Mv.None)
            {
                // razoring
                if (depth <= 3 && eval + 220 + 230 * depth < alpha)
                {
                    int v = QSearch(alpha - 1, alpha, ply, false);
                    if (v < alpha) return v;
                }

                // reverse futility pruning
                if (depth <= 8 && !ttPv && (ttMove == Mv.None || Mv.IsTactical(ttMove)) && eval - (RfpMargin * depth - (improving ? 70 : 0)) >= beta && eval < C.MateInMaxPly && beta > -C.MateInMaxPly)
                    return (eval + beta) / 2;

                // null move pruning
                if (depth >= 3 && eval >= beta && staticEval >= beta - 20 * depth + 140
                    && ss[s - 1].Move != Mv.Null && ply >= nmpMinPly
                    && Pos.NonPawnMaterial[us] > 0 && beta > -C.MateInMaxPly)
                {
                    int R = NmpBase + depth / 4 + Math.Min((eval - beta) / 200, 3);
                    ss[s].Move = Mv.Null;
                    ss[s].ContIdx = NoCont;
                    Pos.MakeNullMove();
                    TT.Prefetch(Pos.Key);
                    int v = -Search(-beta, -beta + 1, depth - R, ply + 1, !cutNode, false);
                    Pos.UnmakeNullMove();
                    if (Ctx.Stop) return 0;
                    if (v >= beta)
                    {
                        if (v >= C.MateInMaxPly) v = beta;
                        if (nmpMinPly != 0 || depth < 14) return v;
                        // verification search at high depth (zugzwang safety)
                        nmpMinPly = ply + 3 * (depth - R) / 4;
                        int v2 = Search(beta - 1, beta, depth - R, ply, false, false);
                        nmpMinPly = 0;
                        if (v2 >= beta) return v;
                    }
                }

                // ProbCut
                int pcBeta = beta + 190 - (improving ? 50 : 0);
                if (depth >= 5 && Math.Abs(beta) < C.MateInMaxPly
                    && !(ttHit && tte.Depth >= depth - 3 && ttScore != C.NoScore && ttScore < pcBeta))
                {
                    var pc = pickers[ply];
                    pc.InitProbCut(this, Pos, ply, ttMove, pcBeta - staticEval);
                    int m;
                    while ((m = pc.Next(true)) != Mv.None)
                    {
                        if (!Pos.IsLegal(m)) continue;
                        ss[s].Move = m;
                        ss[s].ContIdx = Pos.Board[Mv.From(m)] * 64 + Mv.To(m);
                        Pos.MakeMove(m);
                        int v = -QSearch(-pcBeta, -pcBeta + 1, ply + 1, false);
                        if (v >= pcBeta)
                            v = -Search(-pcBeta, -pcBeta + 1, depth - 4, ply + 1, !cutNode, false);
                        Pos.UnmakeMove(m);
                        if (Ctx.Stop) return 0;
                        if (v >= pcBeta)
                        {
                            TT.Store(key, m, TT.ScoreToTT(v, ply), rawEval, depth - 3, TT.BoundLower, ttPv);
                            return v;
                        }
                    }
                }
            }

            // internal iterative reduction
            if (depth >= 4 && ttMove == Mv.None && (pvNode || cutNode)) depth--;

            // ---------------------------------------------------- move loop
            int prevContIdx = ss[s - 1].ContIdx;
            int counter = prevContIdx != NoCont ? counterMoves[prevContIdx] : Mv.None;
            var mp = excluded != Mv.None ? pickersSe[ply] : pickers[ply];
            mp.InitMain(this, Pos, ply, ttMove, ss[s].Killer1, ss[s].Killer2, counter);

            int bestScore = -C.Infinite;
            int bestMove = Mv.None;
            int moveCount = 0;
            int nq = 0, nc = 0;
            int[] quiets = quietsTried[ply], caps = capsTried[ply];
            bool skipQuiets = false;
            int origAlpha = alpha;
            int m2;

            while ((m2 = mp.Next(skipQuiets)) != Mv.None)
            {
                int m = m2;
                if (m == excluded) continue;
                if (!Pos.IsLegal(m)) continue;

                moveCount++;
                ss[s].MoveCount = moveCount;
                bool isQuiet = !Mv.IsTactical(m);
                int from = Mv.From(m), to = Mv.To(m);
                int piece = Pos.Board[from];
                bool givesCheck = Pos.GivesCheck(m);
                int hist = isQuiet ? QuietScore(Pos, m, ply) : 0;
                long nodesBefore = Nodes;

                // ------------------------------------------------ shallow depth pruning
                if (!root && bestScore > -C.MateInMaxPly && Pos.NonPawnMaterial[us] > 0)
                {
                    int lmrDepth = Math.Max(0, depth - 1 - Reductions[Math.Min(depth, 63), Math.Min(moveCount, 63)]);
                    if (isQuiet && !givesCheck)
                    {
                        if (depth < 16 && moveCount >= LmpLimit[improving ? 1 : 0, depth]) { skipQuiets = true; continue; }
                        if (lmrDepth < 4 && hist < -HistPrune * depth) continue;
                        if (!inCheck && lmrDepth < 10 && staticEval + FutBase + FutMul * lmrDepth <= alpha) { skipQuiets = true; continue; }
                        if (!SEE.Ge(Pos, m, -26 * lmrDepth * lmrDepth)) continue;
                    }
                    else if (!isQuiet)
                    {
                        if (depth <= 8 && !givesCheck && !SEE.Ge(Pos, m, -95 * depth)) continue;
                    }
                }

                // ------------------------------------------------ extensions
                int ext = 0;
                if (!root && m == ttMove && excluded == Mv.None && depth >= 7 && ply < 2 * RootDepth
                    && ttScore != C.NoScore && Math.Abs(ttScore) < C.MateInMaxPly
                    && (tte.Bound & TT.BoundLower) != 0 && tte.Depth >= depth - 3)
                {
                    int sBeta = ttScore - 2 * depth;
                    int sDepth = (depth - 1) / 2;
                    ss[s].Excluded = m;
                    int v = Search(sBeta - 1, sBeta, sDepth, ply, cutNode, false);
                    ss[s].Excluded = Mv.None;
                    if (Ctx.Stop) return 0;
                    if (v < sBeta)
                    {
                        ext = 1;
                        if (!pvNode && v < sBeta - SeDouble && ss[s].DoubleExt <= 6) { ext = 2; }
                    }
                    else if (sBeta >= beta) return sBeta;     // multi-cut
                    else if (ttScore >= beta) ext = -1;
                    else if (cutNode) ext = -1;
                }
                else if (CheckExt && givesCheck && depth >= 2 && SEE.Ge(Pos, m, 0)) ext = 1;
                else if (pvNode && Mv.IsPromo(m) && Mv.PromoType(m) == C.Queen) ext = 1;

                int newDepth = depth - 1 + ext;
                ss[s + 1].DoubleExt = ss[s].DoubleExt + (ext >= 2 ? 1 : 0);

                ss[s].Move = m;
                ss[s].ContIdx = piece * 64 + to;
                Pos.MakeMove(m);
                TT.Prefetch(Pos.Key);

                int score;
                // ------------------------------------------------ late move reductions
                if (depth >= 2 && moveCount > 1 + (root ? 1 : 0) && (isQuiet || !ttPv || mp.InBadCaptures))
                {
                    int r = Reductions[Math.Min(depth, 63), Math.Min(moveCount, 63)];
                    if (isQuiet)
                    {
                        if (cutNode) r += 2;
                        if (!improving) r++;
                        if (ttPv) r--;
                        if (pvNode) r--;
                        if (givesCheck) r--;
                        if (m == ss[s].Killer1 || m == ss[s].Killer2 || m == counter) r--;
                        if (ttMove != Mv.None && Mv.IsCapture(ttMove)) r++;
                        if (ss[s + 1].Cutoffs > 3) r++;
                        r -= hist / 8000;
                    }
                    else
                    {
                        r = r / 2;
                        if (cutNode) r++;
                    }
                    int d = Math.Clamp(newDepth - r, 1, newDepth + 1);
                    score = -Search(-alpha - 1, -alpha, d, ply + 1, true, false);
                    if (score > alpha && d < newDepth)
                    {
                        score = -Search(-alpha - 1, -alpha, newDepth, ply + 1, !cutNode, false);
                        if (isQuiet && !Ctx.Stop)
                            UpdateQuietHistoryCont(m, piece, ply, score > alpha ? StatBonus(newDepth) : -StatBonus(newDepth));
                    }
                }
                else if (!pvNode || moveCount > 1)
                {
                    score = -Search(-alpha - 1, -alpha, newDepth, ply + 1, !cutNode, false);
                }
                else score = alpha + 1; // force full window for first move in PV node

                if (pvNode && (moveCount == 1 || (score > alpha && (root || score < beta))))
                {
                    pvLen[ply + 1] = ply + 1;
                    score = -Search(-beta, -alpha, newDepth, ply + 1, false, true);
                }

                Pos.UnmakeMove(m);

                if (Ctx.Stop && RootDepth > 1) return 0;

                if (root) rootNodes[(from << 6) | to] += Nodes - nodesBefore;

                if (score > bestScore)
                {
                    bestScore = score;
                    if (score > alpha)
                    {
                        bestMove = m;
                        if (pvNode) UpdatePv(ply, m);
                        if (score >= beta)
                        {
                            ss[s].Cutoffs++;
                            break;
                        }
                        alpha = score;
                    }
                }

                if (m != bestMove)
                {
                    if (isQuiet) { if (nq < 64) quiets[nq++] = m; }
                    else if (nc < 32) caps[nc++] = m;
                }
            }

            if (moveCount == 0)
            {
                if (excluded != Mv.None) return alpha;
                return inCheck ? C.MatedIn(ply) : 0;
            }

            if (bestScore >= beta && bestMove != Mv.None)
            {
                UpdateStats(bestMove, depth, ply, quiets, nq, caps, nc);
                if (moveCount <= 2) PriorMoveMalus(ply, depth);
            }
            else if (bestMove == Mv.None && ss[s - 1].ContIdx != NoCont && !Mv.IsTactical(ss[s - 1].Move) && ply > 0)
            {
                // the previous (quiet) move refuted us: reward it in its own context
                int prevMove = ss[s - 1].Move;
                if (prevMove != Mv.Null && prevMove != Mv.None)
                {
                    int bonus = StatBonus(depth) / 2;
                    int idx = ss[s - 1].ContIdx;
                    if (ss[s - 2].ContIdx != NoCont) Gravity(ref contHist[ss[s - 2].ContIdx * 768 + idx], bonus, 16384);
                }
            }

            // a fail-high at a non-PV node is pulled toward beta: the bigger margin is less trustworthy at low depth
            if (bestScore >= beta && !pvNode && Math.Abs(bestScore) < C.MateInMaxPly && Math.Abs(beta) < C.MateInMaxPly)
                bestScore = (bestScore * depth + beta) / (depth + 1);

            if (excluded == Mv.None && !(root && Ctx.Stop))
            {
                int bound = bestScore >= beta ? TT.BoundLower : (pvNode && bestMove != Mv.None) ? TT.BoundExact : TT.BoundUpper;
                TT.Store(key, bestMove, TT.ScoreToTT(bestScore, ply), inCheck ? C.NoScore : rawEval, depth, bound, ttPv);
                if (!inCheck && (bestMove == Mv.None || !Mv.IsTactical(bestMove))
                    && !(bound == TT.BoundLower && bestScore <= staticEval)
                    && !(bound == TT.BoundUpper && bestScore >= staticEval)
                    && Math.Abs(bestScore) < C.MateInMaxPly)
                    UpdateCorrection(staticEval, bestScore, depth, ply);
            }
            _ = origAlpha;
            return bestScore;
        }

        // we refuted the opponent's previous quiet move almost at once: it was an early choice of theirs, so it was bad
        void PriorMoveMalus(int ply, int depth)
        {
            int s = ply + Off;
            int prev = ss[s - 1].Move;
            if (ply == 0 || prev == Mv.None || prev == Mv.Null || ss[s - 1].ContIdx == NoCont || Pos.Captured != C.NoPiece
                || ss[s - 1].MoveCount > 3 || ss[s - 2].ContIdx == NoCont) return;
            Gravity(ref contHist[ss[s - 2].ContIdx * 768 + ss[s - 1].ContIdx], -StatBonus(depth) / 2, 16384);
        }

        void UpdateQuietHistoryCont(int m, int pc, int ply, int bonus)
        {
            int idx = pc * 64 + Mv.To(m);
            int s = ply + Off;
            if (ss[s - 1].ContIdx != NoCont) Gravity(ref contHist[ss[s - 1].ContIdx * 768 + idx], bonus, 16384);
            if (ss[s - 2].ContIdx != NoCont) Gravity(ref contHist[ss[s - 2].ContIdx * 768 + idx], bonus, 16384);
        }

        // ---------------------------------------------------------------- quiescence
        int QSearch(int alpha, int beta, int ply, bool pvNode)
        {
            int s = ply + Off;
            Nodes++;
            CheckTime();
            if (Ctx.Stop && RootDepth > 1) return 0;
            pvLen[ply] = ply; // also in non-PV nodes, so a parent never copies a stale PV tail
            if (pvNode && ply > SelDepth) SelDepth = ply;

            bool inCheck = Pos.Checkers != 0;
            if (Pos.IsDraw(ply)) return DrawScore();
            if (ply >= C.MaxPly - 2) return inCheck ? 0 : Evaluate();

            ulong key = Pos.Key;
            bool ttHit = TT.Probe(key, out TTData tte);
            int ttScore = ttHit ? TT.ScoreFromTT(tte.Score, ply, Pos.Halfmove) : C.NoScore;
            int ttMove = ttHit ? tte.Move : Mv.None;
            bool ttPv = pvNode || (ttHit && tte.Pv);

            if (!pvNode && ttHit && ttScore != C.NoScore
                && (tte.Bound == TT.BoundExact
                    || (tte.Bound == TT.BoundLower && ttScore >= beta)
                    || (tte.Bound == TT.BoundUpper && ttScore <= alpha)))
                return ttScore;

            int bestScore, staticEval, futilityBase, rawEval = C.NoScore;
            if (inCheck)
            {
                bestScore = -C.Infinite;
                staticEval = C.NoScore;
                futilityBase = -C.Infinite;
            }
            else
            {
                rawEval = ttHit && tte.Eval != C.NoScore ? tte.Eval : Evaluate();
                staticEval = Corrected(rawEval, ply);
                bestScore = staticEval;
                if (ttScore != C.NoScore &&
                    ((ttScore > bestScore && (tte.Bound & TT.BoundLower) != 0) || (ttScore < bestScore && (tte.Bound & TT.BoundUpper) != 0)))
                    bestScore = ttScore;

                if (bestScore >= beta)
                {
                    if (!ttHit) TT.Store(key, Mv.None, TT.ScoreToTT(bestScore, ply), rawEval, -6, TT.BoundLower, false);
                    return bestScore;
                }
                if (bestScore > alpha) alpha = bestScore;
                futilityBase = staticEval + 160;
            }
            ss[s].StaticEval = staticEval;

            var mp = pickers[ply];
            mp.InitQs(this, Pos, ply, ttMove);
            int bestMove = Mv.None;
            int moveCount = 0;
            int m;
            while ((m = mp.Next(false)) != Mv.None)
            {
                if (!Pos.IsLegal(m)) continue;
                moveCount++;
                bool isCapture = Mv.IsCapture(m);

                if (bestScore > -C.MateInMaxPly)
                {
                    if (!inCheck)
                    {
                        // delta / futility pruning
                        if (!Mv.IsPromo(m) && !Pos.GivesCheck(m))
                        {
                            int victim = Mv.IsEp(m) ? C.Pawn : C.TypeOf(Pos.Board[Mv.To(m)]);
                            int fv = futilityBase + Eval.SeeValue[victim];
                            if (fv <= alpha) { bestScore = Math.Max(bestScore, fv); continue; }
                            if (futilityBase <= alpha && !SEE.Ge(Pos, m, 1)) { bestScore = Math.Max(bestScore, futilityBase); continue; }
                        }
                        if (!SEE.Ge(Pos, m, -30)) continue;
                    }
                    else if (!isCapture && moveCount > 2 && !SEE.Ge(Pos, m, -50)) continue;
                }

                ss[s].Move = m;
                ss[s].ContIdx = Pos.Board[Mv.From(m)] * 64 + Mv.To(m);
                Pos.MakeMove(m);
                TT.Prefetch(Pos.Key);
                int score = -QSearch(-beta, -alpha, ply + 1, pvNode);
                Pos.UnmakeMove(m);
                if (Ctx.Stop && RootDepth > 1) return 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    if (score > alpha)
                    {
                        bestMove = m;
                        if (pvNode) UpdatePv(ply, m);
                        if (score >= beta) break;
                        alpha = score;
                    }
                }
            }

            if (inCheck && moveCount == 0) return C.MatedIn(ply);

            int bound = bestScore >= beta ? TT.BoundLower : TT.BoundUpper;
            TT.Store(key, bestMove, TT.ScoreToTT(bestScore, ply), rawEval, -6 + (inCheck ? 1 : 0), bound, ttPv);
            return bestScore;
        }

        public string PvString()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < pvLen[0]; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Mv.ToUci(pv[i]));
            }
            if (pvLen[0] == 0 && BestMove != Mv.None) sb.Append(Mv.ToUci(BestMove));
            return sb.ToString();
        }
    }
}
