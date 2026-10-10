using System;
using System.Runtime.CompilerServices;

namespace Dolphin2
{
    // Hand-crafted evaluation, tapered between middlegame and endgame.
    // Terms: minor pieces developed and centralised,
    // rooks on open files / 7th rank, connected pawns, weak pawns punished, active king in the endgame,
    // finer passed pawn scoring.
    public static class Eval
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int S(int mg, int eg) => (int)((uint)eg << 16) + mg;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MG(int s) => (short)(s & 0xFFFF);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EG(int s) => (short)((s + 0x8000) >> 16);

        // ------------------------------------------------------------------ material
        public static readonly int[] PieceValue = { S(82, 110), S(330, 300), S(350, 320), S(470, 560), S(1000, 1020), 0 };
        public static readonly int[] NonPawnValue = { 0, 330, 350, 470, 1000, 0 };
        // simple values used for SEE / move ordering / pruning margins
        public static readonly int[] SeeValue = { 100, 320, 330, 500, 950, 20000, 0 };

        // ------------------------------------------------------------------ piece-square tables
        // Written visually: first row = rank 8, from White's point of view.
        static readonly int[] PawnMg = {
              0,   0,   0,   0,   0,   0,   0,   0,
             50,  60,  55,  65,  65,  55,  60,  50,
             10,  15,  25,  30,  30,  25,  15,  10,
              0,   5,  10,  22,  22,  10,   5,   0,
             -5,   0,   5,  18,  18,   5,   0,  -5,
             -5,  -2,   2,   5,   5,  -2,   2,  -3,
             -6,  -2,  -4, -12, -12,   6,  10,  -6,
              0,   0,   0,   0,   0,   0,   0,   0 };
        static readonly int[] PawnEg = {
              0,   0,   0,   0,   0,   0,   0,   0,
             90,  85,  80,  70,  70,  80,  85,  90,
             50,  48,  40,  32,  32,  40,  48,  50,
             22,  18,  12,   8,   8,  12,  18,  22,
              8,   6,   0,  -4,  -4,   0,   6,   8,
              0,   0,  -4,   0,   0,  -4,   0,   0,
              2,   2,   4,   4,   4,   4,   2,   2,
              0,   0,   0,   0,   0,   0,   0,   0 };
        static readonly int[] KnightMg = {
            -90, -40, -30, -20, -20, -30, -40, -90,
            -35, -20,   5,  10,  10,   5, -20, -35,
            -20,  10,  20,  30,  30,  20,  10, -20,
            -12,   8,  20,  28,  28,  20,   8, -12,
            -15,   2,  15,  18,  18,  15,   2, -15,
            -25,  -5,   8,  10,  10,   8,  -5, -25,
            -35, -20,  -8,   0,   0,  -8, -20, -35,
            -60, -30, -30, -25, -25, -30, -30, -60 };
        static readonly int[] KnightEg = {
            -60, -40, -25, -20, -20, -25, -40, -60,
            -35, -18,  -8,   0,   0,  -8, -18, -35,
            -25,  -5,  10,  15,  15,  10,  -5, -25,
            -20,   0,  15,  22,  22,  15,   0, -20,
            -20,   0,  12,  20,  20,  12,   0, -20,
            -25,  -8,   2,  10,  10,   2,  -8, -25,
            -35, -20, -10,  -5,  -5, -10, -20, -35,
            -55, -40, -28, -22, -22, -28, -40, -55 };
        static readonly int[] BishopMg = {
            -25, -10, -15, -15, -15, -15, -10, -25,
            -15,   0,   0,   0,   0,   0,   0, -15,
             -8,   5,  10,  10,  10,  10,   5,  -8,
             -8,  10,  10,  15,  15,  10,  10,  -8,
             -5,   5,  12,  15,  15,  12,   5,  -5,
             -5,  10,  10,  10,  10,  10,  10,  -5,
             -5,  15,   5,   5,   5,   5,  15,  -5,
            -20, -10, -12, -12, -12, -12, -10, -20 };
        static readonly int[] BishopEg = {
            -20, -12, -10,  -8,  -8, -10, -12, -20,
            -12,  -4,   0,   2,   2,   0,  -4, -12,
             -8,   2,   6,   6,   6,   6,   2,  -8,
             -6,   4,   8,  10,  10,   8,   4,  -6,
             -6,   4,   8,  10,  10,   8,   4,  -6,
             -8,   0,   5,   6,   6,   5,   0,  -8,
            -12,  -6,  -2,   0,   0,  -2,  -6, -12,
            -20, -12, -10,  -8,  -8, -10, -12, -20 };
        static readonly int[] RookMg = {
             10,  12,  15,  18,  18,  15,  12,  10,
             20,  22,  25,  28,  28,  25,  22,  20,
              0,   5,   5,   8,   8,   5,   5,   0,
             -5,   0,   0,   2,   2,   0,   0,  -5,
            -10,  -5,  -5,   0,   0,  -5,  -5, -10,
            -15, -10,  -5,  -2,  -2,  -5, -10, -15,
            -20, -10,  -5,   0,   0,  -5, -10, -25,
             -8,  -5,   2,   8,   8,   5,  -5,  -8 };
        static readonly int[] RookEg = {
             12,  12,  12,  12,  12,  12,  12,  12,
             15,  15,  15,  15,  15,  15,  15,  15,
              6,   6,   6,   6,   6,   6,   6,   6,
              3,   3,   3,   3,   3,   3,   3,   3,
              0,   0,   0,   0,   0,   0,   0,   0,
             -3,  -3,  -3,  -3,  -3,  -3,  -3,  -3,
             -6,  -6,  -4,  -4,  -4,  -4,  -6,  -6,
             -6,  -2,   0,   0,   0,   0,  -2,  -8 };
        static readonly int[] QueenMg = {
            -20, -10, -10,  -5,  -5, -10, -10, -20,
            -15, -20,  -5,   0,   0,  -5, -20, -15,
            -10,  -5,   5,   5,   5,   5,  -5, -10,
             -5,  -5,   5,   5,   5,   5,  -5,  -5,
             -5,   0,   5,   5,   5,   5,   0,  -5,
            -10,   5,   5,   5,   5,   5,   0, -10,
            -10,   0,   8,   5,   5,   0,   0, -10,
            -20, -15, -10,   5,  -5, -10, -15, -20 };
        static readonly int[] QueenEg = {
            -15,  -5,   0,   5,   5,   0,  -5, -15,
             -5,   5,  10,  15,  15,  10,   5,  -5,
              0,  10,  15,  20,  20,  15,  10,   0,
              5,  15,  20,  25,  25,  20,  15,   5,
              5,  15,  20,  25,  25,  20,  15,   5,
              0,  10,  15,  15,  15,  15,  10,   0,
            -10,  -5,   0,   0,   0,   0,  -5, -10,
            -20, -15, -10, -10, -10, -10, -15, -20 };
        static readonly int[] KingMg = {
            -60, -60, -60, -70, -70, -60, -60, -60,
            -50, -50, -55, -65, -65, -55, -50, -50,
            -45, -45, -50, -60, -60, -50, -45, -45,
            -40, -45, -50, -60, -60, -50, -45, -40,
            -35, -35, -40, -50, -50, -40, -35, -35,
            -15, -20, -25, -35, -35, -25, -20, -15,
             15,  12,  -5, -25, -25, -10,  15,  18,
             20,  35,  15, -15,   0, -10,  35,  25 };
        static readonly int[] KingEg = {
            -70, -40, -30, -25, -25, -30, -40, -70,
            -30,  -5,   5,  10,  10,   5,  -5, -30,
            -20,  10,  25,  30,  30,  25,  10, -20,
            -20,  10,  28,  35,  35,  28,  10, -20,
            -25,   0,  20,  28,  28,  20,   0, -25,
            -30,  -8,   8,  15,  15,   8,  -8, -30,
            -40, -18,  -5,   2,   2,  -5, -18, -40,
            -65, -45, -32, -25, -25, -32, -45, -65 };

        // PsqTable[piece*64 + sq] = material + PST, packed, signed for colour (white +, black -)
        public static readonly int[] PsqTable = new int[12 * 64];

        // ------------------------------------------------------------------ evaluation weights
        static readonly int[][] Mobility = new int[6][];

        static readonly int[] PassedRank = {
            S(0, 0), S(-5, 10), S(-8, 15), S(-4, 28), S(18, 50), S(45, 95), S(80, 160), S(0, 0) };
        static readonly int[] ConnectedRank = {
            S(0, 0), S(2, 0), S(3, 1), S(5, 3), S(9, 7), S(18, 15), S(30, 28), S(0, 0) };
        static readonly int Isolated = S(-10, -14);
        static readonly int Doubled = S(-8, -22);
        static readonly int Backward = S(-9, -10);
        static readonly int BishopPair = S(30, 55);
        static readonly int RookOpen = S(32, 12);
        static readonly int RookSemiOpen = S(14, 10);
        static readonly int RookOn7th = S(8, 25);
        static readonly int KnightOutpost = S(28, 16);
        static readonly int BishopOutpost = S(14, 6);
        static readonly int MinorBehindPawn = S(10, 2);
        static readonly int BadBishopPawn = S(-3, -6);
        static readonly int ThreatByPawn = S(55, 35);
        static readonly int ThreatMinorOnMajor = S(32, 25);
        static readonly int ThreatRookOnQueen = S(40, 15);
        static readonly int Hanging = S(30, 15);
        static readonly int KingProtector = S(-4, -3);
        static readonly int Tempo = S(18, 8);
        static readonly int[] ShelterRank = { -32, 22, 14, -2, -12, -20, -25, -32 }; // by rel. rank of own pawn (0 = none)
        static readonly int[] StormRank = { 0, 0, -30, -18, -8, 0, 0, 0 };          // by rel. rank (from our side) of enemy pawn
        static readonly int[] KingAttackWeight = { 0, 2, 2, 3, 5, 0 };
        static readonly int[] SafeCheckWeight = { 0, 7, 5, 8, 7, 0 };

        static Eval()
        {
            int[][] mg = { PawnMg, KnightMg, BishopMg, RookMg, QueenMg, KingMg };
            int[][] eg = { PawnEg, KnightEg, BishopEg, RookEg, QueenEg, KingEg };
            for (int t = 0; t < 6; t++)
            {
                for (int sq = 0; sq < 64; sq++)
                {
                    // table index for white: visual layout -> rank 8 first
                    int vis = (7 - (sq >> 3)) * 8 + (sq & 7);
                    int v = PieceValue[t] + S(mg[t][vis], eg[t][vis]);
                    PsqTable[t * 64 + sq] = v;
                    PsqTable[(6 + t) * 64 + (sq ^ 56)] = -v;
                }
            }

            Mobility[C.Knight] = new int[9];
            Mobility[C.Bishop] = new int[14];
            Mobility[C.Rook] = new int[15];
            Mobility[C.Queen] = new int[28];
            int[] nMg = { -45, -25, -10, -2, 5, 12, 18, 23, 27 };
            int[] nEg = { -60, -35, -18, -6, 4, 12, 18, 22, 24 };
            for (int i = 0; i < 9; i++) Mobility[C.Knight][i] = S(nMg[i], nEg[i]);
            int[] bMg = { -40, -22, -8, 0, 8, 15, 20, 24, 28, 31, 34, 36, 38, 40 };
            int[] bEg = { -55, -35, -18, -6, 4, 12, 19, 24, 28, 32, 35, 37, 39, 40 };
            for (int i = 0; i < 14; i++) Mobility[C.Bishop][i] = S(bMg[i], bEg[i]);
            int[] rMg = { -30, -18, -10, -5, -1, 2, 5, 8, 11, 14, 16, 18, 20, 22, 23 };
            int[] rEg = { -60, -35, -15, -2, 8, 16, 23, 30, 36, 41, 45, 49, 52, 54, 55 };
            for (int i = 0; i < 15; i++) Mobility[C.Rook][i] = S(rMg[i], rEg[i]);
            for (int i = 0; i < 28; i++)
                Mobility[C.Queen][i] = S((int)(-25 + 10 * Math.Sqrt(i)), (int)(-45 + 18 * Math.Sqrt(i)));
        }

        // ------------------------------------------------------------------ pawn hash
        public struct PawnEntry
        {
            public ulong Key;
            public int Score;
            public ulong PassedW, PassedB;
        }

        public sealed class PawnTable
        {
            public const int Size = 1 << 16;
            public readonly PawnEntry[] Entries = new PawnEntry[Size];
            public void Clear() => Array.Clear(Entries, 0, Size);
        }

        // ------------------------------------------------------------------ main entry
        // Returns score from the side to move's point of view.
        public static int Evaluate(Position pos, PawnTable pt)
        {
            if (NNUE.Enabled && pos.Acc != null)
                return NNUE.Evaluate(pos);
            return EvaluateClassic(pos, pt);
        }

        public static int EvaluateClassic(Position pos, PawnTable pt)
        {
            int score = pos.Psq;

            // pawn structure (hashed)
            ref PawnEntry pe = ref pt.Entries[(int)(pos.PawnKey & (PawnTable.Size - 1))];
            if (pe.Key != pos.PawnKey || pe.Key == 0)
            {
                EvalPawns(pos, ref pe);
                pe.Key = pos.PawnKey;
            }
            score += pe.Score;

            ulong wPawns = pos.PiecesOf(C.White, C.Pawn), bPawns = pos.PiecesOf(C.Black, C.Pawn);
            ulong wPawnAtt = BB.PawnAttacksBB(C.White, wPawns), bPawnAtt = BB.PawnAttacksBB(C.Black, bPawns);

            var ei = new EvalInfo();
            ei.PawnAtt0 = wPawnAtt; ei.PawnAtt1 = bPawnAtt;
            ei.Att0 = wPawnAtt | BB.KingAttacks[pos.KingSq[0]];
            ei.Att1 = bPawnAtt | BB.KingAttacks[pos.KingSq[1]];
            ei.Att2_0 = wPawnAtt & BB.KingAttacks[pos.KingSq[0]];
            ei.Att2_1 = bPawnAtt & BB.KingAttacks[pos.KingSq[1]];

            score += EvalPieces(pos, C.White, ref ei) - EvalPieces(pos, C.Black, ref ei);
            score += EvalKing(pos, C.White, ref ei) - EvalKing(pos, C.Black, ref ei);
            score += EvalThreats(pos, C.White, ref ei) - EvalThreats(pos, C.Black, ref ei);
            score += EvalPassed(pos, C.White, pe.PassedW, ref ei) - EvalPassed(pos, C.Black, pe.PassedB, ref ei);

            if (pos.PiecesOf(C.White, C.Bishop) != 0 && BB.More1(pos.PiecesOf(C.White, C.Bishop))) score += BishopPair;
            if (pos.PiecesOf(C.Black, C.Bishop) != 0 && BB.More1(pos.PiecesOf(C.Black, C.Bishop))) score -= BishopPair;

            score += pos.SideToMove == C.White ? Tempo : -Tempo;

            int mg = MG(score), eg = EG(score);
            int phase = Math.Min(pos.Phase, 24);
            int scale = ScaleFactor(pos, eg);
            int v = (mg * phase + eg * (24 - phase) * scale / 64) / 24;
            return pos.SideToMove == C.White ? v : -v;
        }

        struct EvalInfo
        {
            public ulong PawnAtt0, PawnAtt1;
            public ulong Att0, Att1;       // all squares attacked by side
            public ulong Att2_0, Att2_1;   // squares attacked twice
            public ulong MinorAtt0, MinorAtt1, RookAtt0, RookAtt1, QueenAtt0, QueenAtt1;
            public int KingAttUnits0, KingAttUnits1; // units accumulated against the *enemy* king
            public int KingAttackers0, KingAttackers1;
        }

        static void EvalPawns(Position pos, ref PawnEntry pe)
        {
            int total = 0;
            pe.PassedW = pe.PassedB = 0;
            for (int us = 0; us < 2; us++)
            {
                int them = us ^ 1;
                ulong ours = pos.PiecesOf(us, C.Pawn), theirs = pos.PiecesOf(them, C.Pawn);
                ulong ourAtt = BB.PawnAttacksBB(us, ours), theirAtt = BB.PawnAttacksBB(them, theirs);
                int s = 0;
                ulong b = ours;
                while (b != 0)
                {
                    int sq = BB.PopLsb(ref b);
                    int f = sq & 7;
                    int rr = C.RelRank(us, sq);
                    ulong bit = 1UL << sq;
                    ulong adj = BB.AdjacentFiles[f];
                    bool isolated = (ours & adj) == 0;
                    bool doubled = (BB.ForwardFile[us * 64 + sq] & ours) != 0;
                    ulong phalanx = ours & adj & BB.Ranks[sq >> 3];
                    ulong support = ours & BB.PawnAttacks[them * 64 + sq];
                    bool passed = (BB.PassedMask[us * 64 + sq] & theirs) == 0 && !doubled;

                    if (isolated) s += Isolated;
                    if (doubled) s += Doubled;
                    if (phalanx != 0 || support != 0)
                        s += ConnectedRank[rr] * ((phalanx != 0 ? 2 : 0) + BB.PopCount(support));
                    else if (!isolated)
                    {
                        // backward: no friendly pawn behind/beside on adjacent files, and the stop square is controlled by enemy pawns
                        ulong behind = BB.PassedMask[them * 64 + sq] & adj;
                        behind |= adj & BB.Ranks[sq >> 3];
                        int stop = us == C.White ? sq + 8 : sq - 8;
                        if ((ours & behind) == 0 && (theirAtt & (1UL << stop)) != 0)
                            s += Backward;
                    }
                    if (passed)
                    {
                        if (us == C.White) pe.PassedW |= bit; else pe.PassedB |= bit;
                    }
                }
                total += us == C.White ? s : -s;
            }
            pe.Score = total;
        }

        static int EvalPieces(Position pos, int us, ref EvalInfo ei)
        {
            int them = us ^ 1;
            int s = 0;
            ulong occ = pos.Occupied;
            ulong ourPawns = pos.PiecesOf(us, C.Pawn), theirPawns = pos.PiecesOf(them, C.Pawn);
            ulong theirPawnAtt = us == C.White ? ei.PawnAtt1 : ei.PawnAtt0;
            ulong ourPawnAtt = us == C.White ? ei.PawnAtt0 : ei.PawnAtt1;
            int theirKing = pos.KingSq[them];
            ulong theirZone = BB.KingZone[theirKing];
            // mobility area: not occupied by our pawns/king, not attacked by enemy pawns
            ulong mobArea = ~(ourPawns | pos.PiecesOf(us, C.King) | theirPawnAtt);
            ulong outpostRanks = us == C.White ? (BB.Rank4 | BB.Rank5 | BB.Rank6) : (BB.Rank5 | BB.Rank4 | BB.Rank3);
            ulong att = 0, att2 = 0, minorAtt = 0, rookAtt = 0, queenAtt = 0;
            int units = 0, attackers = 0;

            // Knights
            ulong b = pos.PiecesOf(us, C.Knight);
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                ulong a = BB.KnightAttacks[sq];
                att2 |= att & a; att |= a; minorAtt |= a;
                s += Mobility[C.Knight][BB.PopCount(a & mobArea)];
                if ((a & theirZone) != 0) { attackers++; units += KingAttackWeight[C.Knight] * BB.PopCount(a & theirZone); }
                ulong bit = 1UL << sq;
                if ((bit & outpostRanks & ourPawnAtt) != 0 && (BB.PassedMask[us * 64 + sq] & ~BB.ForwardFile[us * 64 + sq] & theirPawns) == 0)
                    s += KnightOutpost;
                if ((BB.Forward(us, bit) & ourPawns) != 0) s += MinorBehindPawn;
                s += KingProtector * BB.Distance[sq * 64 + pos.KingSq[us]];
            }
            // Bishops
            b = pos.PiecesOf(us, C.Bishop);
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                ulong a = BB.BishopAttacks(sq, occ ^ pos.PiecesOf(us, C.Queen));
                att2 |= att & a; att |= a; minorAtt |= a;
                s += Mobility[C.Bishop][BB.PopCount(a & mobArea)];
                if ((a & theirZone) != 0) { attackers++; units += KingAttackWeight[C.Bishop] * BB.PopCount(a & theirZone); }
                ulong bit = 1UL << sq;
                if ((bit & outpostRanks & ourPawnAtt) != 0 && (BB.PassedMask[us * 64 + sq] & ~BB.ForwardFile[us * 64 + sq] & theirPawns) == 0)
                    s += BishopOutpost;
                if ((BB.Forward(us, bit) & ourPawns) != 0) s += MinorBehindPawn;
                ulong sameColor = (bit & BB.DarkSquares) != 0 ? BB.DarkSquares : BB.LightSquares;
                s += BadBishopPawn * BB.PopCount(ourPawns & sameColor);
                s += KingProtector * BB.Distance[sq * 64 + pos.KingSq[us]];
            }
            // Rooks
            b = pos.PiecesOf(us, C.Rook);
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                ulong a = BB.RookAttacks(sq, occ ^ pos.PiecesOf(us, C.Queen) ^ pos.PiecesOf(us, C.Rook));
                att2 |= att & a; att |= a; rookAtt |= a;
                s += Mobility[C.Rook][BB.PopCount(a & mobArea)];
                if ((a & theirZone) != 0) { attackers++; units += KingAttackWeight[C.Rook] * BB.PopCount(a & theirZone); }
                ulong file = BB.Files[sq & 7];
                if ((file & ourPawns) == 0)
                    s += (file & theirPawns) == 0 ? RookOpen : RookSemiOpen;
                if (C.RelRank(us, sq) == 6 && (C.RelRank(us, theirKing) == 7 || (theirPawns & (us == C.White ? BB.Rank7 : BB.Rank2)) != 0))
                    s += RookOn7th;
            }
            // Queens
            b = pos.PiecesOf(us, C.Queen);
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b);
                ulong a = BB.QueenAttacks(sq, occ);
                att2 |= att & a; att |= a; queenAtt |= a;
                s += Mobility[C.Queen][BB.PopCount(a & mobArea)];
                if ((a & theirZone) != 0) { attackers++; units += KingAttackWeight[C.Queen] * BB.PopCount(a & theirZone); }
            }

            if (us == C.White)
            {
                ei.Att2_0 |= att2 | (ei.Att0 & att); ei.Att0 |= att;
                ei.MinorAtt0 = minorAtt; ei.RookAtt0 = rookAtt; ei.QueenAtt0 = queenAtt;
                ei.KingAttUnits0 = units; ei.KingAttackers0 = attackers;
            }
            else
            {
                ei.Att2_1 |= att2 | (ei.Att1 & att); ei.Att1 |= att;
                ei.MinorAtt1 = minorAtt; ei.RookAtt1 = rookAtt; ei.QueenAtt1 = queenAtt;
                ei.KingAttUnits1 = units; ei.KingAttackers1 = attackers;
            }
            return s;
        }

        // Safety of `us` king against attacks by `them`.
        static int EvalKing(Position pos, int us, ref EvalInfo ei)
        {
            int them = us ^ 1;
            int ksq = pos.KingSq[us];
            int s = 0;

            // pawn shelter and storm on the king file and its neighbours
            ulong ourPawns = pos.PiecesOf(us, C.Pawn), theirPawns = pos.PiecesOf(them, C.Pawn);
            int kf = Math.Clamp(ksq & 7, 1, 6);
            int shelter = 0;
            for (int f = kf - 1; f <= kf + 1; f++)
            {
                ulong fileMask = BB.Files[f];
                ulong mine = ourPawns & fileMask & ~BB.ForwardFile[them * 64 + ((ksq & 56) | f)];
                ulong theirsF = theirPawns & fileMask & ~BB.ForwardFile[them * 64 + ((ksq & 56) | f)];
                int rr = 0;
                if (mine != 0)
                {
                    int psq = us == C.White ? BB.Lsb(mine) : BB.Msb(mine);
                    rr = C.RelRank(us, psq);
                }
                shelter += ShelterRank[rr] * (f == (ksq & 7) ? 2 : 1);
                if (theirsF != 0)
                {
                    int esq = us == C.White ? BB.Lsb(theirsF) : BB.Msb(theirsF);
                    int er = C.RelRank(us, esq);
                    bool blocked = mine != 0 && C.RelRank(us, us == C.White ? BB.Lsb(mine) : BB.Msb(mine)) == er - 1;
                    s += S(blocked ? StormRank[er] / 2 : StormRank[er], 0);
                }
            }
            s += S(shelter, 0);

            // king attack
            int units = us == C.White ? ei.KingAttUnits1 : ei.KingAttUnits0;
            int attackers = us == C.White ? ei.KingAttackers1 : ei.KingAttackers0;
            if (attackers >= 2 || (attackers >= 1 && pos.PiecesOf(them, C.Queen) != 0))
            {
                ulong theirAtt = us == C.White ? ei.Att1 : ei.Att0;
                ulong ourAtt = us == C.White ? ei.Att0 : ei.Att1;
                ulong ourAtt2 = us == C.White ? ei.Att2_0 : ei.Att2_1;
                ulong occ = pos.Occupied;
                // squares where enemy can safely land: not occupied by them, not defended by us (or defended once but attacked twice)
                ulong safe = ~pos.Colors[them] & (~ourAtt | ((us == C.White ? ei.Att2_1 : ei.Att2_0) & ~ourAtt2 & BB.KingAttacks[ksq]));
                ulong rookChecks = BB.RookAttacks(ksq, occ) & safe;
                ulong bishopChecks = BB.BishopAttacks(ksq, occ) & safe;
                ulong knightChecks = BB.KnightAttacks[ksq] & safe;
                ulong minorAtt = us == C.White ? ei.MinorAtt1 : ei.MinorAtt0;
                ulong rookAtt = us == C.White ? ei.RookAtt1 : ei.RookAtt0;
                ulong queenAtt = us == C.White ? ei.QueenAtt1 : ei.QueenAtt0;
                if ((rookChecks & queenAtt) != 0) units += SafeCheckWeight[C.Queen];
                else if ((bishopChecks & queenAtt) != 0) units += SafeCheckWeight[C.Queen];
                if ((rookChecks & rookAtt) != 0) units += SafeCheckWeight[C.Rook];
                if ((bishopChecks & minorAtt & BB.BishopAttacks(ksq, occ)) != 0 && pos.PiecesOf(them, C.Bishop) != 0)
                    units += SafeCheckWeight[C.Bishop];
                if ((knightChecks & minorAtt) != 0 && pos.PiecesOf(them, C.Knight) != 0)
                    units += SafeCheckWeight[C.Knight];

                // weak squares around king: attacked by enemy, defended at most by our king/queen
                ulong weak = theirAtt & BB.KingAttacks[ksq] & ~ourAtt2;
                units += 2 * BB.PopCount(weak);
                units -= shelter / 8;
                if (pos.PiecesOf(them, C.Queen) == 0) units = units * 2 / 3;
                if (units > 0)
                {
                    int pen = Math.Min(units * units / 3, 700);
                    s -= S(pen, pen / 8);
                }
            }

            // endgame: king proximity to pawns handled by passed pawn evaluation
            return s;
        }

        static int EvalThreats(Position pos, int us, ref EvalInfo ei)
        {
            int them = us ^ 1;
            int s = 0;
            ulong ourPawnAtt = us == C.White ? ei.PawnAtt0 : ei.PawnAtt1;
            ulong ourAtt = us == C.White ? ei.Att0 : ei.Att1;
            ulong theirAtt = us == C.White ? ei.Att1 : ei.Att0;
            ulong ourMinorAtt = us == C.White ? ei.MinorAtt0 : ei.MinorAtt1;
            ulong ourRookAtt = us == C.White ? ei.RookAtt0 : ei.RookAtt1;
            ulong theirNonPawn = pos.Colors[them] & ~pos.PiecesOf(them, C.Pawn) & ~pos.PiecesOf(them, C.King);
            ulong theirMajors = pos.PiecesOf(them, C.Rook) | pos.PiecesOf(them, C.Queen);

            s += ThreatByPawn * BB.PopCount(ourPawnAtt & theirNonPawn);
            s += ThreatMinorOnMajor * BB.PopCount(ourMinorAtt & theirMajors);
            s += ThreatRookOnQueen * BB.PopCount(ourRookAtt & pos.PiecesOf(them, C.Queen));
            // hanging: attacked by us, not defended by them
            s += Hanging * BB.PopCount(theirNonPawn & ourAtt & ~theirAtt);
            return s;
        }

        static int EvalPassed(Position pos, int us, ulong passed, ref EvalInfo ei)
        {
            int them = us ^ 1;
            int s = 0;
            ulong theirAtt = us == C.White ? ei.Att1 : ei.Att0;
            ulong ourAtt = us == C.White ? ei.Att0 : ei.Att1;
            while (passed != 0)
            {
                int sq = BB.PopLsb(ref passed);
                int rr = C.RelRank(us, sq);
                s += PassedRank[rr];
                if (rr < 3) continue;
                int w = (rr - 2) * (rr - 1) / 2;   // 1,3,6,10 for ranks 4..7
                int stop = us == C.White ? sq + 8 : sq - 8;
                // king proximity (endgame): enemy king far from stop square is good
                int bonus = (BB.Distance[pos.KingSq[them] * 64 + stop] * 5 - BB.Distance[pos.KingSq[us] * 64 + stop] * 2) * w;
                if (pos.Board[stop] == C.NoPiece)
                {
                    ulong path = BB.ForwardFile[us * 64 + sq];
                    ulong unsafeSq = path & (theirAtt | pos.Colors[them]);
                    int k = unsafeSq == 0 ? 20 : (unsafeSq & (1UL << stop)) == 0 ? 10 : 0;
                    if ((ourAtt & (1UL << stop)) != 0) k += 4;
                    bonus += k * w;
                }
                else if (C.ColorOf(pos.Board[stop]) == them) bonus -= 3 * w;
                s += S(bonus / 4, bonus);
            }
            return s;
        }

        // Scale factor (0..64) applied to the endgame part. eg is from white's POV.
        static int ScaleFactor(Position pos, int eg)
        {
            int strong = eg > 0 ? C.White : C.Black;
            int weak = strong ^ 1;
            int strongPawns = BB.PopCount(pos.PiecesOf(strong, C.Pawn));
            int npmStrong = pos.NonPawnMaterial[strong], npmWeak = pos.NonPawnMaterial[weak];

            if (strongPawns == 0 && npmStrong - npmWeak <= NonPawnValue[C.Bishop])
                return npmStrong < NonPawnValue[C.Rook] ? 0 : 12;

            // opposite coloured bishops
            if (npmStrong == NonPawnValue[C.Bishop] && npmWeak == NonPawnValue[C.Bishop])
            {
                ulong wb = pos.PiecesOf(C.White, C.Bishop), bb = pos.PiecesOf(C.Black, C.Bishop);
                if (wb != 0 && bb != 0 && ((wb & BB.LightSquares) != 0) != ((bb & BB.LightSquares) != 0))
                    return 22 + 4 * Math.Max(0, strongPawns - BB.PopCount(pos.PiecesOf(weak, C.Pawn)));
            }
            if (strongPawns == 1 && npmStrong <= npmWeak && npmWeak > 0) return 32;
            return 64;
        }
    }
}
