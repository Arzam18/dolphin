using System.Runtime.CompilerServices;

namespace Dolphin2
{
    // Square: a1 = 0 ... h8 = 63. File = sq & 7, Rank = sq >> 3.
    // Piece  : color * 6 + type, NoPiece = 12.
    public static class C
    {
        public const int White = 0, Black = 1;

        public const int Pawn = 0, Knight = 1, Bishop = 2, Rook = 3, Queen = 4, King = 5, NoType = 6;
        public const int NoPiece = 12;
        public const int NoSquare = 64;

        public const int MaxPly = 128;
        public const int MaxMoves = 256;

        public const int Infinite = 32000;
        public const int Mate = 31000;
        public const int MateInMaxPly = Mate - MaxPly;
        public const int NoScore = 32001;

        // Castling rights bits
        public const int WK = 1, WQ = 2, BK = 4, BQ = 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MakePiece(int color, int type) => color * 6 + type;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int TypeOf(int piece) => piece >= 6 ? piece - 6 : piece;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ColorOf(int piece) => piece >= 6 ? 1 : 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FileOf(int sq) => sq & 7;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RankOf(int sq) => sq >> 3;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RelRank(int color, int sq) => color == White ? sq >> 3 : 7 - (sq >> 3);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Flip(int sq) => sq ^ 56;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MateIn(int ply) => Mate - ply;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MatedIn(int ply) => -Mate + ply;

        public static string SquareName(int sq) =>
            sq >= 64 ? "-" : ((char)('a' + (sq & 7))).ToString() + (char)('1' + (sq >> 3));

        public static int ParseSquare(string s)
        {
            if (s == null || s.Length < 2) return NoSquare;
            int f = s[0] - 'a', r = s[1] - '1';
            if (f < 0 || f > 7 || r < 0 || r > 7) return NoSquare;
            return r * 8 + f;
        }

        public const string PieceChars = "PNBRQKpnbrqk.";
    }

    // Move encoding (16 bits): from | to << 6 | flag << 12
    // flag: 0 quiet, 1 double push, 2 O-O, 3 O-O-O, 4 capture, 5 en passant,
    //       8..11 promotion N,B,R,Q ; 12..15 promotion-capture N,B,R,Q
    public static class Mv
    {
        public const int None = 0;
        public const int Null = 65; // b1b1 style impossible move, used for null move bookkeeping

        public const int Quiet = 0, DoublePush = 1, KingCastle = 2, QueenCastle = 3,
                         Capture = 4, EnPassant = 5, PromoN = 8, PromoB = 9, PromoR = 10, PromoQ = 11,
                         PromoCapN = 12, PromoCapB = 13, PromoCapR = 14, PromoCapQ = 15;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Make(int from, int to, int flag) => from | (to << 6) | (flag << 12);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int From(int m) => m & 63;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int To(int m) => (m >> 6) & 63;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Flag(int m) => (m >> 12) & 15;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCapture(int m) => (m & (4 << 12)) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPromo(int m) => (m & (8 << 12)) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsTactical(int m) => (m & (12 << 12)) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PromoType(int m) => ((m >> 12) & 3) + 1;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCastle(int m) { int f = Flag(m); return f == KingCastle || f == QueenCastle; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEp(int m) => Flag(m) == EnPassant;

        public static string ToUci(int m)
        {
            if (m == None) return "0000";
            string s = C.SquareName(From(m)) + C.SquareName(To(m));
            if (IsPromo(m)) s += "nbrq"[PromoType(m) - 1];
            return s;
        }
    }

    public struct ExtMove
    {
        public int Move;
        public int Score;
    }
}
