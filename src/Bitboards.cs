using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Dolphin2
{
    public static unsafe class BB
    {
        public const ulong FileA = 0x0101010101010101UL;
        public const ulong FileH = FileA << 7;
        public const ulong Rank1 = 0xFFUL;
        public const ulong Rank2 = Rank1 << 8;
        public const ulong Rank3 = Rank1 << 16;
        public const ulong Rank4 = Rank1 << 24;
        public const ulong Rank5 = Rank1 << 32;
        public const ulong Rank6 = Rank1 << 40;
        public const ulong Rank7 = Rank1 << 48;
        public const ulong Rank8 = Rank1 << 56;
        public const ulong LightSquares = 0x55AA55AA55AA55AAUL;
        public const ulong DarkSquares = ~LightSquares;

        public static readonly ulong[] Files = new ulong[8];
        public static readonly ulong[] Ranks = new ulong[8];
        public static readonly ulong[] AdjacentFiles = new ulong[8];

        public static readonly ulong[] KnightAttacks = new ulong[64];
        public static readonly ulong[] KingAttacks = new ulong[64];
        public static readonly ulong[] PawnAttacks = new ulong[128]; // [color * 64 + sq]

        public static readonly ulong[] Between = new ulong[64 * 64]; // squares strictly between
        public static readonly ulong[] Line = new ulong[64 * 64];    // full line through both (0 if not aligned)

        public static readonly ulong[] PassedMask = new ulong[128];   // [color*64+sq] squares in front on same+adjacent files
        public static readonly ulong[] ForwardFile = new ulong[128];  // [color*64+sq] squares in front on same file
        public static readonly ulong[] KingZone = new ulong[64];
        public static readonly int[] Distance = new int[64 * 64];

        // Magic bitboards (magics found at startup by our own search)
        static readonly ulong[] RookMask = new ulong[64], BishopMask = new ulong[64];
        static readonly ulong[] RookMagic = new ulong[64], BishopMagic = new ulong[64];
        static readonly int[] RookShift = new int[64], BishopShift = new int[64];
        static readonly int[] RookOffset = new int[64], BishopOffset = new int[64];
        static readonly ulong[] RookTable = new ulong[102400];
        static readonly ulong[] BishopTable = new ulong[5248];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong b) => BitOperations.PopCount(b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Lsb(ulong b) => BitOperations.TrailingZeroCount(b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Msb(ulong b) => 63 - BitOperations.LeadingZeroCount(b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopLsb(ref ulong b) { int s = BitOperations.TrailingZeroCount(b); b &= b - 1; return s; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Sq(int s) => 1UL << s;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool More1(ulong b) => (b & (b - 1)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftN(ulong b) => b << 8;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftS(ulong b) => b >> 8;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftE(ulong b) => (b & ~FileH) << 1;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ShiftW(ulong b) => (b & ~FileA) >> 1;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Forward(int color, ulong b) => color == C.White ? b << 8 : b >> 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PawnAttacksBB(int color, ulong pawns) =>
            color == C.White ? ShiftE(pawns << 8) | ShiftW(pawns << 8) : ShiftE(pawns >> 8) | ShiftW(pawns >> 8);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong RookAttacks(int sq, ulong occ)
        {
            return RookTable[RookOffset[sq] + (int)(((occ & RookMask[sq]) * RookMagic[sq]) >> RookShift[sq])];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong BishopAttacks(int sq, ulong occ)
        {
            return BishopTable[BishopOffset[sq] + (int)(((occ & BishopMask[sq]) * BishopMagic[sq]) >> BishopShift[sq])];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong QueenAttacks(int sq, ulong occ) => RookAttacks(sq, occ) | BishopAttacks(sq, occ);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Attacks(int type, int sq, ulong occ)
        {
            switch (type)
            {
                case C.Knight: return KnightAttacks[sq];
                case C.Bishop: return BishopAttacks(sq, occ);
                case C.Rook: return RookAttacks(sq, occ);
                case C.Queen: return QueenAttacks(sq, occ);
                case C.King: return KingAttacks[sq];
                default: return 0;
            }
        }

        static bool initialized;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            for (int i = 0; i < 8; i++)
            {
                Files[i] = FileA << i;
                Ranks[i] = Rank1 << (8 * i);
            }
            for (int i = 0; i < 8; i++)
                AdjacentFiles[i] = (i > 0 ? Files[i - 1] : 0) | (i < 7 ? Files[i + 1] : 0);

            for (int sq = 0; sq < 64; sq++)
            {
                int f = sq & 7, r = sq >> 3;
                KnightAttacks[sq] = StepAttacks(f, r, new[] { 1, 2, 2, 1, -1, -2, -2, -1 }, new[] { 2, 1, -1, -2, -2, -1, 1, 2 });
                KingAttacks[sq] = StepAttacks(f, r, new[] { 1, 1, 1, 0, 0, -1, -1, -1 }, new[] { 1, 0, -1, 1, -1, 1, 0, -1 });
                PawnAttacks[sq] = StepAttacks(f, r, new[] { -1, 1 }, new[] { 1, 1 });
                PawnAttacks[64 + sq] = StepAttacks(f, r, new[] { -1, 1 }, new[] { -1, -1 });

                ulong fwdW = 0, fwdB = 0;
                for (int rr = r + 1; rr < 8; rr++) fwdW |= Sq(rr * 8 + f);
                for (int rr = r - 1; rr >= 0; rr--) fwdB |= Sq(rr * 8 + f);
                ForwardFile[sq] = fwdW;
                ForwardFile[64 + sq] = fwdB;
                ulong pw = fwdW, pb = fwdB;
                if (f > 0) { pw |= fwdW >> 1; pb |= fwdB >> 1; }
                if (f < 7) { pw |= fwdW << 1; pb |= fwdB << 1; }
                PassedMask[sq] = pw;
                PassedMask[64 + sq] = pb;

                for (int s2 = 0; s2 < 64; s2++)
                    Distance[sq * 64 + s2] = Math.Max(Math.Abs(f - (s2 & 7)), Math.Abs(r - (s2 >> 3)));
            }

            InitMagics();

            for (int s1 = 0; s1 < 64; s1++)
            {
                for (int s2 = 0; s2 < 64; s2++)
                {
                    if (s1 == s2) continue;
                    ulong b1 = Sq(s1), b2 = Sq(s2);
                    if ((SlowRook(s1, 0) & b2) != 0)
                    {
                        Line[s1 * 64 + s2] = (SlowRook(s1, 0) & SlowRook(s2, 0)) | b1 | b2;
                        Between[s1 * 64 + s2] = SlowRook(s1, b2) & SlowRook(s2, b1);
                    }
                    else if ((SlowBishop(s1, 0) & b2) != 0)
                    {
                        Line[s1 * 64 + s2] = (SlowBishop(s1, 0) & SlowBishop(s2, 0)) | b1 | b2;
                        Between[s1 * 64 + s2] = SlowBishop(s1, b2) & SlowBishop(s2, b1);
                    }
                }
            }

            for (int sq = 0; sq < 64; sq++)
            {
                ulong z = KingAttacks[sq] | Sq(sq);
                // extend the zone one rank further in both directions (shelter area)
                z |= ShiftN(z) | ShiftS(z);
                KingZone[sq] = z;
            }
        }

        static ulong StepAttacks(int f, int r, int[] df, int[] dr)
        {
            ulong b = 0;
            for (int i = 0; i < df.Length; i++)
            {
                int nf = f + df[i], nr = r + dr[i];
                if (nf >= 0 && nf < 8 && nr >= 0 && nr < 8) b |= Sq(nr * 8 + nf);
            }
            return b;
        }

        static ulong SlideAttacks(int sq, ulong occ, int[] df, int[] dr)
        {
            ulong b = 0;
            int f0 = sq & 7, r0 = sq >> 3;
            for (int d = 0; d < 4; d++)
            {
                int f = f0 + df[d], r = r0 + dr[d];
                while (f >= 0 && f < 8 && r >= 0 && r < 8)
                {
                    ulong s = Sq(r * 8 + f);
                    b |= s;
                    if ((occ & s) != 0) break;
                    f += df[d]; r += dr[d];
                }
            }
            return b;
        }

        static readonly int[] RookDf = { 1, -1, 0, 0 }, RookDr = { 0, 0, 1, -1 };
        static readonly int[] BishopDf = { 1, 1, -1, -1 }, BishopDr = { 1, -1, 1, -1 };

        public static ulong SlowRook(int sq, ulong occ) => SlideAttacks(sq, occ, RookDf, RookDr);
        public static ulong SlowBishop(int sq, ulong occ) => SlideAttacks(sq, occ, BishopDf, BishopDr);

        static ulong RelevantMask(int sq, bool rook)
        {
            int f = sq & 7, r = sq >> 3;
            ulong edges = ((Rank1 | Rank8) & ~Ranks[r]) | ((FileA | FileH) & ~Files[f]);
            return (rook ? SlowRook(sq, 0) : SlowBishop(sq, 0)) & ~edges;
        }

        static void InitMagics()
        {
            ulong seed = 0x9E3779B97F4A7C15UL; // fixed seed -> deterministic magics
            int rookOff = 0, bishopOff = 0;
            ulong[] occs = new ulong[4096];
            ulong[] atts = new ulong[4096];
            int[] epoch = new int[4096];
            for (int pass = 0; pass < 2; pass++)
            {
                bool rook = pass == 0;
                for (int sq = 0; sq < 64; sq++)
                {
                    ulong mask = RelevantMask(sq, rook);
                    int bits = PopCount(mask);
                    int size = 1 << bits;
                    int shift = 64 - bits;
                    // enumerate subsets (carry-rippler)
                    ulong sub = 0; int n = 0;
                    do
                    {
                        occs[n] = sub;
                        atts[n] = rook ? SlowRook(sq, sub) : SlowBishop(sq, sub);
                        n++;
                        sub = (sub - mask) & mask;
                    } while (sub != 0);

                    ulong[] table = rook ? RookTable : BishopTable;
                    int offset = rook ? rookOff : bishopOff;
                    ulong magic;
                    int attempt = 0;
                    Array.Clear(epoch, 0, epoch.Length);
                    while (true)
                    {
                        attempt++;
                        magic = Rand(ref seed) & Rand(ref seed) & Rand(ref seed);
                        if (PopCount((mask * magic) & 0xFF00000000000000UL) < 6) continue;
                        bool ok = true;
                        for (int i = 0; i < n; i++)
                        {
                            int idx = (int)((occs[i] * magic) >> shift);
                            if (epoch[idx] < attempt)
                            {
                                epoch[idx] = attempt;
                                table[offset + idx] = atts[i];
                            }
                            else if (table[offset + idx] != atts[i]) { ok = false; break; }
                        }
                        if (ok) break;
                    }
                    if (rook)
                    {
                        RookMask[sq] = mask; RookMagic[sq] = magic; RookShift[sq] = shift; RookOffset[sq] = offset; rookOff += size;
                    }
                    else
                    {
                        BishopMask[sq] = mask; BishopMagic[sq] = magic; BishopShift[sq] = shift; BishopOffset[sq] = offset; bishopOff += size;
                    }
                }
            }
        }

        static ulong Rand(ref ulong s)
        {
            // xorshift64*
            s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
            return s * 2685821657736338717UL;
        }
    }
}
