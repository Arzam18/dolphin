using System.Runtime.CompilerServices;

namespace Dolphin2
{
    // Staged move generation: hash move, good captures, killers, counter move, quiets, bad captures.
    public sealed class MovePicker
    {
        const int MainTT = 0, CapInit = 1, GoodCaps = 2, Killer1 = 3, Killer2 = 4, Counter = 5,
                  QuietInit = 6, Quiets = 7, BadCaps = 8,
                  EvTT = 10, EvInit = 11, Evasions = 12,
                  QsTT = 20, QsInit = 21, QsCaps = 22,
                  PcTT = 30, PcInit = 31, PcCaps = 32,
                  Done = 99;

        readonly ExtMove[] moves = new ExtMove[C.MaxMoves];
        readonly ExtMove[] bad = new ExtMove[C.MaxMoves];
        int stage, cur, end, badEnd, badCur;
        int ttMove, killer1, killer2, counter;
        int threshold;
        Position pos;
        SearchThread th;
        int ply;

        public int Stage => stage;
        public bool InBadCaptures => stage == BadCaps;

        public void InitMain(SearchThread t, Position p, int ply, int tt, int k1, int k2, int cm)
        {
            th = t; pos = p; this.ply = ply;
            ttMove = tt != Mv.None && p.IsPseudoLegal(tt) ? tt : Mv.None;
            killer1 = k1; killer2 = k2; counter = cm;
            badEnd = 0; badCur = 0;
            if (p.Checkers != 0) stage = ttMove != Mv.None ? EvTT : EvInit;
            else stage = ttMove != Mv.None ? MainTT : CapInit;
        }

        public void InitQs(SearchThread t, Position p, int ply, int tt)
        {
            th = t; pos = p; this.ply = ply;
            killer1 = killer2 = counter = Mv.None;
            badEnd = 0; badCur = 0;
            if (p.Checkers != 0)
            {
                ttMove = tt != Mv.None && p.IsPseudoLegal(tt) ? tt : Mv.None;
                stage = ttMove != Mv.None ? EvTT : EvInit;
            }
            else
            {
                ttMove = tt != Mv.None && Mv.IsTactical(tt) && p.IsPseudoLegal(tt) ? tt : Mv.None;
                stage = ttMove != Mv.None ? QsTT : QsInit;
            }
        }

        public void InitProbCut(SearchThread t, Position p, int ply, int tt, int thresh)
        {
            th = t; pos = p; this.ply = ply; threshold = thresh;
            killer1 = killer2 = counter = Mv.None;
            badEnd = 0; badCur = 0;
            ttMove = tt != Mv.None && Mv.IsCapture(tt) && p.IsPseudoLegal(tt) && SEE.Ge(p, tt, thresh) ? tt : Mv.None;
            stage = ttMove != Mv.None ? PcTT : PcInit;
        }

        void ScoreCaptures()
        {
            for (int i = cur; i < end; i++)
            {
                int m = moves[i].Move;
                int to = Mv.To(m);
                int victim = Mv.IsEp(m) ? C.Pawn : C.TypeOf(pos.Board[to]);
                int vv = victim == C.NoType || victim > 5 ? 0 : Eval.SeeValue[victim];
                if (Mv.IsPromo(m)) vv += Eval.SeeValue[Mv.PromoType(m)] - Eval.SeeValue[C.Pawn];
                int piece = pos.Board[Mv.From(m)];
                moves[i].Score = vv * 16 + th.CaptureHistory(piece, to, victim == C.NoType ? 0 : victim);
            }
        }

        void ScoreQuiets()
        {
            for (int i = cur; i < end; i++)
            {
                int m = moves[i].Move;
                moves[i].Score = th.QuietScore(pos, m, ply);
            }
        }

        void ScoreEvasions()
        {
            for (int i = cur; i < end; i++)
            {
                int m = moves[i].Move;
                if (Mv.IsCapture(m))
                {
                    int to = Mv.To(m);
                    int victim = Mv.IsEp(m) ? C.Pawn : C.TypeOf(pos.Board[to]);
                    moves[i].Score = 1_000_000 + Eval.SeeValue[victim] * 16 - C.TypeOf(pos.Board[Mv.From(m)]);
                }
                else moves[i].Score = th.QuietScore(pos, m, ply);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int PickBest()
        {
            int best = cur;
            int bs = moves[cur].Score;
            for (int i = cur + 1; i < end; i++)
                if (moves[i].Score > bs) { bs = moves[i].Score; best = i; }
            ExtMove tmp = moves[best];
            moves[best] = moves[cur];
            moves[cur] = tmp;
            return moves[cur++].Move;
        }

        void SortRange()
        {
            // insertion sort, descending
            for (int i = cur + 1; i < end; i++)
            {
                ExtMove x = moves[i];
                int j = i - 1;
                while (j >= cur && moves[j].Score < x.Score) { moves[j + 1] = moves[j]; j--; }
                moves[j + 1] = x;
            }
        }

        bool ValidQuietSpecial(int m)
        {
            return m != Mv.None && m != ttMove && !Mv.IsTactical(m) && pos.IsPseudoLegal(m);
        }

        public int Next(bool skipQuiets)
        {
            while (true)
            {
                switch (stage)
                {
                    case MainTT:
                    case EvTT:
                    case QsTT:
                    case PcTT:
                        stage++;
                        return ttMove;

                    case CapInit:
                        cur = 0;
                        end = MoveGen.Generate(pos, moves, 0, MoveGen.Captures);
                        ScoreCaptures();
                        stage = GoodCaps;
                        goto case GoodCaps;

                    case GoodCaps:
                        while (cur < end)
                        {
                            int m = PickBest();
                            if (m == ttMove) continue;
                            // losing captures go to the end
                            if (!SEE.Ge(pos, m, -moves[cur - 1].Score / 64))
                            {
                                bad[badEnd++] = moves[cur - 1];
                                continue;
                            }
                            return m;
                        }
                        stage = Killer1;
                        goto case Killer1;

                    case Killer1:
                        stage = Killer2;
                        if (!skipQuiets && ValidQuietSpecial(killer1)) return killer1;
                        goto case Killer2;

                    case Killer2:
                        stage = Counter;
                        if (!skipQuiets && killer2 != killer1 && ValidQuietSpecial(killer2)) return killer2;
                        goto case Counter;

                    case Counter:
                        stage = QuietInit;
                        if (!skipQuiets && counter != killer1 && counter != killer2 && ValidQuietSpecial(counter)) return counter;
                        goto case QuietInit;

                    case QuietInit:
                        if (!skipQuiets)
                        {
                            cur = 0;
                            end = MoveGen.Generate(pos, moves, 0, MoveGen.Quiets);
                            ScoreQuiets();
                            SortRange();
                        }
                        else { cur = end = 0; }
                        stage = Quiets;
                        goto case Quiets;

                    case Quiets:
                        while (!skipQuiets && cur < end)
                        {
                            int m = moves[cur++].Move;
                            if (m == ttMove || m == killer1 || m == killer2 || m == counter) continue;
                            return m;
                        }
                        stage = BadCaps;
                        goto case BadCaps;

                    case BadCaps:
                        if (badCur < badEnd) return bad[badCur++].Move;
                        stage = Done;
                        return Mv.None;

                    case EvInit:
                        cur = 0;
                        end = MoveGen.Generate(pos, moves, 0, MoveGen.Evasions);
                        ScoreEvasions();
                        stage = Evasions;
                        goto case Evasions;

                    case Evasions:
                        while (cur < end)
                        {
                            int m = PickBest();
                            if (m == ttMove) continue;
                            return m;
                        }
                        stage = Done;
                        return Mv.None;

                    case QsInit:
                        cur = 0;
                        end = MoveGen.Generate(pos, moves, 0, MoveGen.Captures);
                        ScoreCaptures();
                        stage = QsCaps;
                        goto case QsCaps;

                    case QsCaps:
                        while (cur < end)
                        {
                            int m = PickBest();
                            if (m == ttMove) continue;
                            return m;
                        }
                        stage = Done;
                        return Mv.None;

                    case PcInit:
                        cur = 0;
                        end = MoveGen.Generate(pos, moves, 0, MoveGen.Captures);
                        ScoreCaptures();
                        stage = PcCaps;
                        goto case PcCaps;

                    case PcCaps:
                        while (cur < end)
                        {
                            int m = PickBest();
                            if (m == ttMove) continue;
                            if (!SEE.Ge(pos, m, threshold)) continue;
                            return m;
                        }
                        stage = Done;
                        return Mv.None;

                    default:
                        return Mv.None;
                }
            }
        }
    }
}
