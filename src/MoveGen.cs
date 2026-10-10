using System.Runtime.CompilerServices;

namespace Dolphin2
{
    public static class MoveGen
    {
        public const int Captures = 0, Quiets = 1, Evasions = 2, NonEvasions = 3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int AddPromos(ExtMove[] list, int n, int from, int to, bool capture, int type)
        {
            int baseFlag = capture ? Mv.PromoCapN : Mv.PromoN;
            if (type == Captures)
            {
                if (capture)
                {
                    for (int p = 3; p >= 0; p--) list[n++].Move = Mv.Make(from, to, baseFlag + p);
                }
                else list[n++].Move = Mv.Make(from, to, baseFlag + 3);
            }
            else if (type == Quiets)
            {
                if (!capture)
                    for (int p = 2; p >= 0; p--) list[n++].Move = Mv.Make(from, to, baseFlag + p);
            }
            else
            {
                for (int p = 3; p >= 0; p--) list[n++].Move = Mv.Make(from, to, baseFlag + p);
            }
            return n;
        }

        static int GenPawns(Position pos, ExtMove[] list, int n, int type, ulong target)
        {
            int us = pos.SideToMove, them = us ^ 1;
            ulong pawns = pos.PiecesOf(us, C.Pawn);
            ulong rank7 = us == C.White ? BB.Rank7 : BB.Rank2;
            ulong rank3 = us == C.White ? BB.Rank3 : BB.Rank6;
            int up = us == C.White ? 8 : -8;
            ulong empty = ~pos.Occupied;
            ulong enemies = pos.Colors[them];
            if (type == Evasions) enemies &= target;
            ulong on7 = pawns & rank7, not7 = pawns & ~rank7;

            // quiet pushes
            if (type != Captures)
            {
                ulong b1 = BB.Forward(us, not7) & empty;
                ulong b2 = BB.Forward(us, b1 & rank3) & empty;
                if (type == Evasions) { b1 &= target; b2 &= target; }
                while (b1 != 0) { int to = BB.PopLsb(ref b1); list[n++].Move = Mv.Make(to - up, to, Mv.Quiet); }
                while (b2 != 0) { int to = BB.PopLsb(ref b2); list[n++].Move = Mv.Make(to - 2 * up, to, Mv.DoublePush); }
            }

            // promotions
            if (on7 != 0)
            {
                ulong push = BB.Forward(us, on7) & empty;
                if (type == Evasions) push &= target;
                ulong capL, capR; int dL, dR;
                if (us == C.White)
                {
                    capL = ((on7 & ~BB.FileA) << 7) & enemies; dL = 7;
                    capR = ((on7 & ~BB.FileH) << 9) & enemies; dR = 9;
                }
                else
                {
                    capL = ((on7 & ~BB.FileA) >> 9) & enemies; dL = -9;
                    capR = ((on7 & ~BB.FileH) >> 7) & enemies; dR = -7;
                }
                while (push != 0) { int to = BB.PopLsb(ref push); n = AddPromos(list, n, to - up, to, false, type); }
                while (capL != 0) { int to = BB.PopLsb(ref capL); n = AddPromos(list, n, to - dL, to, true, type); }
                while (capR != 0) { int to = BB.PopLsb(ref capR); n = AddPromos(list, n, to - dR, to, true, type); }
            }

            // captures
            if (type != Quiets)
            {
                ulong capL, capR; int dL, dR;
                if (us == C.White)
                {
                    capL = ((not7 & ~BB.FileA) << 7) & enemies; dL = 7;
                    capR = ((not7 & ~BB.FileH) << 9) & enemies; dR = 9;
                }
                else
                {
                    capL = ((not7 & ~BB.FileA) >> 9) & enemies; dL = -9;
                    capR = ((not7 & ~BB.FileH) >> 7) & enemies; dR = -7;
                }
                while (capL != 0) { int to = BB.PopLsb(ref capL); list[n++].Move = Mv.Make(to - dL, to, Mv.Capture); }
                while (capR != 0) { int to = BB.PopLsb(ref capR); list[n++].Move = Mv.Make(to - dR, to, Mv.Capture); }

                int ep = pos.EpSquare;
                if (ep != C.NoSquare)
                {
                    // in check: e.p. only helps if the checker is the pawn that just double-pushed
                    if (type != Evasions || (target & (1UL << (ep ^ 8))) != 0 || (target & (1UL << ep)) != 0)
                    {
                        ulong att = not7 & BB.PawnAttacks[them * 64 + ep];
                        while (att != 0) { int from = BB.PopLsb(ref att); list[n++].Move = Mv.Make(from, ep, Mv.EnPassant); }
                    }
                }
            }
            return n;
        }

        static int GenPieces(Position pos, ExtMove[] list, int n, int pieceType, ulong targets)
        {
            int us = pos.SideToMove;
            ulong pcs = pos.PiecesOf(us, pieceType);
            ulong occ = pos.Occupied;
            ulong enemies = pos.Colors[us ^ 1];
            while (pcs != 0)
            {
                int from = BB.PopLsb(ref pcs);
                ulong att = BB.Attacks(pieceType, from, occ) & targets;
                while (att != 0)
                {
                    int to = BB.PopLsb(ref att);
                    list[n++].Move = Mv.Make(from, to, (enemies & (1UL << to)) != 0 ? Mv.Capture : Mv.Quiet);
                }
            }
            return n;
        }

        public static int Generate(Position pos, ExtMove[] list, int n, int type)
        {
            int us = pos.SideToMove, them = us ^ 1;
            int ksq = pos.KingSq[us];
            ulong own = pos.Colors[us];
            ulong enemies = pos.Colors[them];
            ulong empty = ~pos.Occupied;

            if (type == Evasions)
            {
                ulong checkers = pos.Checkers;
                // king moves first
                ulong katt = BB.KingAttacks[ksq] & ~own;
                while (katt != 0)
                {
                    int to = BB.PopLsb(ref katt);
                    list[n++].Move = Mv.Make(ksq, to, (enemies & (1UL << to)) != 0 ? Mv.Capture : Mv.Quiet);
                }
                if (BB.More1(checkers)) return n;
                int checker = BB.Lsb(checkers);
                ulong target = BB.Between[ksq * 64 + checker] | checkers;
                n = GenPawns(pos, list, n, Evasions, target);
                for (int pt = C.Knight; pt <= C.Queen; pt++) n = GenPieces(pos, list, n, pt, target);
                return n;
            }

            ulong targets = type == Captures ? enemies : type == Quiets ? empty : ~own;
            n = GenPawns(pos, list, n, type, 0);
            for (int pt = C.Knight; pt <= C.Queen; pt++) n = GenPieces(pos, list, n, pt, targets);

            ulong kt = BB.KingAttacks[ksq] & targets;
            while (kt != 0)
            {
                int to = BB.PopLsb(ref kt);
                list[n++].Move = Mv.Make(ksq, to, (enemies & (1UL << to)) != 0 ? Mv.Capture : Mv.Quiet);
            }

            if (type != Captures && pos.Checkers == 0)
            {
                int home = us == C.White ? 4 : 60;
                int ks = us == C.White ? C.WK : C.BK, qs = us == C.White ? C.WQ : C.BQ;
                ulong occ = pos.Occupied;
                if ((pos.Castling & ks) != 0 && (occ & ((1UL << (home + 1)) | (1UL << (home + 2)))) == 0
                    && !pos.IsAttacked(home + 1, them, occ) && !pos.IsAttacked(home + 2, them, occ))
                    list[n++].Move = Mv.Make(home, home + 2, Mv.KingCastle);
                if ((pos.Castling & qs) != 0 && (occ & ((1UL << (home - 1)) | (1UL << (home - 2)) | (1UL << (home - 3)))) == 0
                    && !pos.IsAttacked(home - 1, them, occ) && !pos.IsAttacked(home - 2, them, occ))
                    list[n++].Move = Mv.Make(home, home - 2, Mv.QueenCastle);
            }
            return n;
        }

        public static int GenerateLegal(Position pos, ExtMove[] list)
        {
            int n = Generate(pos, list, 0, pos.Checkers != 0 ? Evasions : NonEvasions);
            int k = 0;
            for (int i = 0; i < n; i++)
                if (pos.IsLegal(list[i].Move)) list[k++] = list[i];
            return k;
        }
    }
}
