using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dolphin2
{
    public static class Zobrist
    {
        public static readonly ulong[] Psq = new ulong[12 * 64];
        public static readonly ulong[] Castling = new ulong[16];
        public static readonly ulong[] EpFile = new ulong[8];
        public static ulong Side;

        static bool initialized;
        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            // Zobrist keys from our own PRNG with a fixed seed (keeps bench node counts reproducible).
            ulong s = 937204053988876326UL;
            for (int i = 0; i < Psq.Length; i++) Psq[i] = Next(ref s);
            for (int i = 0; i < 16; i++) Castling[i] = Next(ref s);
            for (int i = 0; i < 8; i++) EpFile[i] = Next(ref s);
            Side = Next(ref s);
        }

        static ulong Next(ref ulong s)
        {
            // splitmix64
            s += 0x9E3779B97F4A7C15UL;
            ulong z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public struct StateInfo
    {
        public ulong Key, PawnKey, Checkers, Pinned;
        public int Castling, Ep, Halfmove, PliesFromNull, Captured, Move;
    }

    public sealed partial class Position
    {
        public readonly ulong[] Pieces = new ulong[12];
        public readonly ulong[] Colors = new ulong[2];
        public ulong Occupied;
        public readonly int[] Board = new int[64];
        public readonly int[] KingSq = new int[2];

        public int SideToMove;
        public int Castling;
        public int EpSquare = C.NoSquare;
        public int Halfmove;
        public int PliesFromNull;
        public int FullmoveStart = 1;
        public ulong Key, PawnKey;
        public readonly ulong[] NonPawnKey = new ulong[2]; // pieces other than pawns, per colour (kings included)
        public ulong Checkers;  // enemy pieces giving check to side to move
        public ulong Pinned;    // our pieces pinned to our king
        public int Captured = C.NoPiece; // piece captured by the last move

        // Incremental evaluation terms (white point of view)
        public int Psq;          // packed S(mg, eg)
        public int Phase;        // 0..24 (24 = all pieces)
        public readonly int[] NonPawnMaterial = new int[2];

        public int GamePly;      // number of moves made from the root FEN
        StateInfo[] hist = new StateInfo[2048];

        public NNUE.Accumulators Acc; // null when NNUE disabled

        static readonly int[] CastleMask = new int[64];
        public static readonly int[] PhaseWeight = { 0, 1, 1, 2, 4, 0 };

        static Position()
        {
            for (int i = 0; i < 64; i++) CastleMask[i] = 15;
            CastleMask[0] &= ~C.WQ; CastleMask[7] &= ~C.WK; CastleMask[4] &= ~(C.WK | C.WQ);
            CastleMask[56] &= ~C.BQ; CastleMask[63] &= ~C.BK; CastleMask[60] &= ~(C.BK | C.BQ);
        }

        public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        public Position() { SetFen(StartFen); }
        public Position(string fen) { SetFen(fen); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong PiecesOf(int color, int type) => Pieces[color * 6 + type];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong PieceTypeBB(int type) => Pieces[type] | Pieces[6 + type];
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool InCheck() => Checkers != 0;

        public ulong LastKey(int back) => hist[GamePly - back].Key;
        public int LastMove(int back) => GamePly - back >= 0 ? hist[GamePly - back].Move : Mv.None;

        // ---------------------------------------------------------------- board edits
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddPiece(int pc, int sq)
        {
            ulong b = 1UL << sq;
            Pieces[pc] |= b;
            Colors[pc >= 6 ? 1 : 0] |= b;
            Occupied |= b;
            Board[sq] = pc;
            Psq += Eval.PsqTable[pc * 64 + sq];
            int t = C.TypeOf(pc);
            Phase += PhaseWeight[t];
            NonPawnMaterial[C.ColorOf(pc)] += Eval.NonPawnValue[t];
            if (t != C.Pawn) NonPawnKey[C.ColorOf(pc)] ^= Zobrist.Psq[pc * 64 + sq];
            Acc?.Add(pc, sq);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RemovePiece(int sq)
        {
            int pc = Board[sq];
            ulong b = 1UL << sq;
            Pieces[pc] ^= b;
            Colors[pc >= 6 ? 1 : 0] ^= b;
            Occupied ^= b;
            Board[sq] = C.NoPiece;
            Psq -= Eval.PsqTable[pc * 64 + sq];
            int t = C.TypeOf(pc);
            Phase -= PhaseWeight[t];
            NonPawnMaterial[C.ColorOf(pc)] -= Eval.NonPawnValue[t];
            if (t != C.Pawn) NonPawnKey[C.ColorOf(pc)] ^= Zobrist.Psq[pc * 64 + sq];
            Acc?.Remove(pc, sq);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void MovePiece(int from, int to)
        {
            int pc = Board[from];
            ulong b = (1UL << from) | (1UL << to);
            Pieces[pc] ^= b;
            Colors[pc >= 6 ? 1 : 0] ^= b;
            Occupied ^= b;
            Board[from] = C.NoPiece;
            Board[to] = pc;
            Psq += Eval.PsqTable[pc * 64 + to] - Eval.PsqTable[pc * 64 + from];
            if (C.TypeOf(pc) != C.Pawn) NonPawnKey[C.ColorOf(pc)] ^= Zobrist.Psq[pc * 64 + from] ^ Zobrist.Psq[pc * 64 + to];
            Acc?.Move(pc, from, to);
        }

        // ---------------------------------------------------------------- attacks
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong AttackersTo(int sq, ulong occ)
        {
            return (BB.PawnAttacks[64 + sq] & Pieces[C.Pawn])          // white pawns attacking sq
                 | (BB.PawnAttacks[sq] & Pieces[6 + C.Pawn])           // black pawns
                 | (BB.KnightAttacks[sq] & (Pieces[C.Knight] | Pieces[6 + C.Knight]))
                 | (BB.KingAttacks[sq] & (Pieces[C.King] | Pieces[6 + C.King]))
                 | (BB.BishopAttacks(sq, occ) & (Pieces[C.Bishop] | Pieces[6 + C.Bishop] | Pieces[C.Queen] | Pieces[6 + C.Queen]))
                 | (BB.RookAttacks(sq, occ) & (Pieces[C.Rook] | Pieces[6 + C.Rook] | Pieces[C.Queen] | Pieces[6 + C.Queen]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAttacked(int sq, int byColor, ulong occ)
        {
            int o = byColor * 6;
            if ((BB.PawnAttacks[(byColor ^ 1) * 64 + sq] & Pieces[o + C.Pawn]) != 0) return true;
            if ((BB.KnightAttacks[sq] & Pieces[o + C.Knight]) != 0) return true;
            if ((BB.KingAttacks[sq] & Pieces[o + C.King]) != 0) return true;
            ulong q = Pieces[o + C.Queen];
            if ((BB.BishopAttacks(sq, occ) & (Pieces[o + C.Bishop] | q)) != 0) return true;
            if ((BB.RookAttacks(sq, occ) & (Pieces[o + C.Rook] | q)) != 0) return true;
            return false;
        }

        // pieces of color `us` that block a slider attack from `them` on square ksq
        ulong ComputePinned(int us)
        {
            int them = us ^ 1;
            int ksq = KingSq[us];
            ulong pinned = 0;
            ulong snipers = ((BB.RookAttacks(ksq, 0) & (PiecesOf(them, C.Rook) | PiecesOf(them, C.Queen)))
                          | (BB.BishopAttacks(ksq, 0) & (PiecesOf(them, C.Bishop) | PiecesOf(them, C.Queen))));
            while (snipers != 0)
            {
                int s = BB.PopLsb(ref snipers);
                ulong b = BB.Between[ksq * 64 + s] & Occupied;
                if (b != 0 && !BB.More1(b) && (b & Colors[us]) != 0) pinned |= b;
            }
            return pinned;
        }

        void UpdateCheckInfo()
        {
            int us = SideToMove;
            Checkers = AttackersTo(KingSq[us], Occupied) & Colors[us ^ 1];
            Pinned = ComputePinned(us);
        }

        // ---------------------------------------------------------------- make / unmake
        public void MakeMove(int m)
        {
            ref StateInfo st = ref hist[GamePly];
            st.Key = Key; st.PawnKey = PawnKey; st.Checkers = Checkers; st.Pinned = Pinned;
            st.Castling = Castling; st.Ep = EpSquare; st.Halfmove = Halfmove; st.PliesFromNull = PliesFromNull;
            st.Captured = Captured; st.Move = m;
            GamePly++;

            int us = SideToMove, them = us ^ 1;
            int from = Mv.From(m), to = Mv.To(m), flag = Mv.Flag(m);
            int pc = Board[from];
            int captured = C.NoPiece;
            ulong key = Key ^ Zobrist.Side;

            Acc?.Push();

            if (EpSquare != C.NoSquare) { key ^= Zobrist.EpFile[EpSquare & 7]; EpSquare = C.NoSquare; }
            Halfmove++;
            PliesFromNull++;

            if (flag == Mv.EnPassant)
            {
                int capSq = to ^ 8;
                captured = Board[capSq];
                RemovePiece(capSq);
                key ^= Zobrist.Psq[captured * 64 + capSq];
                PawnKey ^= Zobrist.Psq[captured * 64 + capSq];
            }
            else if (Mv.IsCapture(m))
            {
                captured = Board[to];
                RemovePiece(to);
                key ^= Zobrist.Psq[captured * 64 + to];
                if (C.TypeOf(captured) == C.Pawn) PawnKey ^= Zobrist.Psq[captured * 64 + to];
            }
            if (captured != C.NoPiece) Halfmove = 0;

            MovePiece(from, to);
            key ^= Zobrist.Psq[pc * 64 + from] ^ Zobrist.Psq[pc * 64 + to];

            int type = C.TypeOf(pc);
            if (type == C.Pawn)
            {
                Halfmove = 0;
                PawnKey ^= Zobrist.Psq[pc * 64 + from] ^ Zobrist.Psq[pc * 64 + to];
                if (flag == Mv.DoublePush)
                {
                    int ep = from + (us == C.White ? 8 : -8);
                    // only record e.p. square when an enemy pawn can actually capture
                    if ((BB.PawnAttacks[us * 64 + ep] & PiecesOf(them, C.Pawn)) != 0)
                    {
                        EpSquare = ep;
                        key ^= Zobrist.EpFile[ep & 7];
                    }
                }
                else if (Mv.IsPromo(m))
                {
                    int promo = C.MakePiece(us, Mv.PromoType(m));
                    RemovePiece(to);
                    AddPiece(promo, to);
                    key ^= Zobrist.Psq[pc * 64 + to] ^ Zobrist.Psq[promo * 64 + to];
                    PawnKey ^= Zobrist.Psq[pc * 64 + to];
                }
            }
            else if (type == C.King)
            {
                KingSq[us] = to;
                if (flag == Mv.KingCastle)
                {
                    int rf = to + 1, rt = to - 1;
                    int rook = Board[rf];
                    MovePiece(rf, rt);
                    key ^= Zobrist.Psq[rook * 64 + rf] ^ Zobrist.Psq[rook * 64 + rt];
                }
                else if (flag == Mv.QueenCastle)
                {
                    int rf = to - 2, rt = to + 1;
                    int rook = Board[rf];
                    MovePiece(rf, rt);
                    key ^= Zobrist.Psq[rook * 64 + rf] ^ Zobrist.Psq[rook * 64 + rt];
                }
            }

            int newCastle = Castling & CastleMask[from] & CastleMask[to];
            if (newCastle != Castling)
            {
                key ^= Zobrist.Castling[Castling] ^ Zobrist.Castling[newCastle];
                Castling = newCastle;
            }

            Captured = captured;
            SideToMove = them;
            Key = key;
            UpdateCheckInfo();
            Acc?.Commit();
        }

        public void UnmakeMove(int m)
        {
            GamePly--;
            ref StateInfo st = ref hist[GamePly];
            int them = SideToMove, us = them ^ 1;
            int from = Mv.From(m), to = Mv.To(m), flag = Mv.Flag(m);
            int captured = Captured;

            if (Mv.IsPromo(m))
            {
                RemovePiece(to);
                AddPiece(C.MakePiece(us, C.Pawn), to);
            }
            MovePiece(to, from);
            if (C.TypeOf(Board[from]) == C.King)
            {
                KingSq[us] = from;
                if (flag == Mv.KingCastle) MovePiece(to - 1, to + 1);
                else if (flag == Mv.QueenCastle) MovePiece(to + 1, to - 2);
            }
            if (captured != C.NoPiece)
                AddPiece(captured, flag == Mv.EnPassant ? to ^ 8 : to);

            Acc?.Pop();

            SideToMove = us;
            Key = st.Key; PawnKey = st.PawnKey; Checkers = st.Checkers; Pinned = st.Pinned;
            Castling = st.Castling; EpSquare = st.Ep; Halfmove = st.Halfmove; PliesFromNull = st.PliesFromNull;
            Captured = st.Captured;
        }

        public void MakeNullMove()
        {
            ref StateInfo st = ref hist[GamePly];
            st.Key = Key; st.PawnKey = PawnKey; st.Checkers = Checkers; st.Pinned = Pinned;
            st.Castling = Castling; st.Ep = EpSquare; st.Halfmove = Halfmove; st.PliesFromNull = PliesFromNull;
            st.Captured = Captured; st.Move = Mv.Null;
            GamePly++;

            Key ^= Zobrist.Side;
            if (EpSquare != C.NoSquare) { Key ^= Zobrist.EpFile[EpSquare & 7]; EpSquare = C.NoSquare; }
            Halfmove++;
            PliesFromNull = 0;
            Captured = C.NoPiece;
            SideToMove ^= 1;
            Checkers = 0;
            Pinned = ComputePinned(SideToMove);
        }

        public void UnmakeNullMove()
        {
            GamePly--;
            ref StateInfo st = ref hist[GamePly];
            SideToMove ^= 1;
            Key = st.Key; PawnKey = st.PawnKey; Checkers = st.Checkers; Pinned = st.Pinned;
            Castling = st.Castling; EpSquare = st.Ep; Halfmove = st.Halfmove; PliesFromNull = st.PliesFromNull;
            Captured = st.Captured;
        }

        // ---------------------------------------------------------------- draws
        // ply = distance from search root; repetitions inside the search tree count once,
        // positions before the root need to occur twice (true threefold).
        public bool IsDraw(int ply)
        {
            if (Halfmove >= 100)
            {
                if (Checkers == 0) return true;
                // checkmate takes precedence over the 50-move rule
                return HasLegalMove();
            }
            int end = Math.Min(Math.Min(Halfmove, PliesFromNull), GamePly);
            int reps = 0;
            for (int i = 4; i <= end; i += 2)
            {
                if (hist[GamePly - i].Key == Key)
                {
                    if (i < ply) return true;   // repetition inside search tree
                    reps++;
                    if (reps >= 2) return true;
                }
            }
            return IsInsufficientMaterial();
        }

        public bool IsInsufficientMaterial()
        {
            if ((Pieces[C.Pawn] | Pieces[6 + C.Pawn] | Pieces[C.Rook] | Pieces[6 + C.Rook] | Pieces[C.Queen] | Pieces[6 + C.Queen]) != 0)
                return false;
            int minors = BB.PopCount(Occupied) - 2;
            if (minors <= 1) return true;   // K vs K, K+minor vs K
            // K+B vs K+B with bishops on same colour
            ulong bishops = Pieces[C.Bishop] | Pieces[6 + C.Bishop];
            if (minors == BB.PopCount(bishops) &&
                ((bishops & BB.LightSquares) == 0 || (bishops & BB.DarkSquares) == 0))
                return true;
            return false;
        }

        public bool HasLegalMove()
        {
            var list = new ExtMove[C.MaxMoves];
            int n = MoveGen.GenerateLegal(this, list);
            return n > 0;
        }

        // ---------------------------------------------------------------- FEN
        public void SetFen(string fen)
        {
            Array.Clear(Pieces, 0, 12);
            Colors[0] = Colors[1] = 0;
            Occupied = 0;
            for (int i = 0; i < 64; i++) Board[i] = C.NoPiece;
            Psq = 0; Phase = 0; NonPawnMaterial[0] = NonPawnMaterial[1] = 0;
            NonPawnKey[0] = NonPawnKey[1] = 0;
            GamePly = 0;
            Captured = C.NoPiece;

            string[] parts = fen.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int sq = 56;
            foreach (char ch in parts[0])
            {
                if (ch == '/') { sq -= 16; continue; }
                if (char.IsDigit(ch)) { sq += ch - '0'; continue; }
                int idx = C.PieceChars.IndexOf(ch);
                if (idx < 0 || idx >= 12) throw new ArgumentException("Bad FEN piece: " + ch);
                AddPiece(idx, sq);
                if (C.TypeOf(idx) == C.King) KingSq[C.ColorOf(idx)] = sq;
                sq++;
            }
            SideToMove = parts.Length > 1 && parts[1] == "b" ? C.Black : C.White;
            Castling = 0;
            if (parts.Length > 2)
            {
                foreach (char ch in parts[2])
                {
                    if (ch == 'K' && Board[4] == C.MakePiece(0, C.King) && Board[7] == C.MakePiece(0, C.Rook)) Castling |= C.WK;
                    if (ch == 'Q' && Board[4] == C.MakePiece(0, C.King) && Board[0] == C.MakePiece(0, C.Rook)) Castling |= C.WQ;
                    if (ch == 'k' && Board[60] == C.MakePiece(1, C.King) && Board[63] == C.MakePiece(1, C.Rook)) Castling |= C.BK;
                    if (ch == 'q' && Board[60] == C.MakePiece(1, C.King) && Board[56] == C.MakePiece(1, C.Rook)) Castling |= C.BQ;
                }
            }
            EpSquare = C.NoSquare;
            if (parts.Length > 3 && parts[3] != "-")
            {
                int ep = C.ParseSquare(parts[3]);
                int us = SideToMove;
                if (ep != C.NoSquare && (BB.PawnAttacks[(us ^ 1) * 64 + ep] & PiecesOf(us, C.Pawn)) != 0)
                    EpSquare = ep;
            }
            Halfmove = parts.Length > 4 && int.TryParse(parts[4], out int hm) ? hm : 0;
            FullmoveStart = parts.Length > 5 && int.TryParse(parts[5], out int fm) ? Math.Max(1, fm) : 1;
            PliesFromNull = Halfmove;

            Key = ComputeKey();
            PawnKey = ComputePawnKey();
            UpdateCheckInfo();
            Acc?.Refresh(this);
        }

        public ulong ComputeKey()
        {
            ulong k = 0;
            for (int s = 0; s < 64; s++) if (Board[s] != C.NoPiece) k ^= Zobrist.Psq[Board[s] * 64 + s];
            if (SideToMove == C.Black) k ^= Zobrist.Side;
            k ^= Zobrist.Castling[Castling];
            if (EpSquare != C.NoSquare) k ^= Zobrist.EpFile[EpSquare & 7];
            return k;
        }

        public ulong ComputePawnKey()
        {
            ulong k = 0;
            for (int s = 0; s < 64; s++)
                if (Board[s] != C.NoPiece && C.TypeOf(Board[s]) == C.Pawn) k ^= Zobrist.Psq[Board[s] * 64 + s];
            return k;
        }

        public ulong ComputeNonPawnKey(int color)
        {
            ulong k = 0;
            for (int s = 0; s < 64; s++)
                if (Board[s] != C.NoPiece && C.TypeOf(Board[s]) != C.Pawn && C.ColorOf(Board[s]) == color)
                    k ^= Zobrist.Psq[Board[s] * 64 + s];
            return k;
        }

        public string GetFen()
        {
            var sb = new StringBuilder();
            for (int r = 7; r >= 0; r--)
            {
                int empty = 0;
                for (int f = 0; f < 8; f++)
                {
                    int pc = Board[r * 8 + f];
                    if (pc == C.NoPiece) { empty++; continue; }
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(C.PieceChars[pc]);
                }
                if (empty > 0) sb.Append(empty);
                if (r > 0) sb.Append('/');
            }
            sb.Append(SideToMove == C.White ? " w " : " b ");
            string cs = "";
            if ((Castling & C.WK) != 0) cs += "K";
            if ((Castling & C.WQ) != 0) cs += "Q";
            if ((Castling & C.BK) != 0) cs += "k";
            if ((Castling & C.BQ) != 0) cs += "q";
            sb.Append(cs == "" ? "-" : cs);
            sb.Append(' ').Append(C.SquareName(EpSquare));
            sb.Append(' ').Append(Halfmove).Append(' ').Append(FullmoveStart + GamePly / 2);
            return sb.ToString();
        }

        public Position Clone()
        {
            var p = new Position(GetFen());
            return p;
        }

        public string Pretty()
        {
            var sb = new StringBuilder();
            for (int r = 7; r >= 0; r--)
            {
                sb.Append(" +---+---+---+---+---+---+---+---+\n");
                for (int f = 0; f < 8; f++)
                    sb.Append(" | ").Append(C.PieceChars[Board[r * 8 + f]] == '.' ? ' ' : C.PieceChars[Board[r * 8 + f]]);
                sb.Append(" | ").Append(r + 1).Append('\n');
            }
            sb.Append(" +---+---+---+---+---+---+---+---+\n");
            sb.Append("   a   b   c   d   e   f   g   h\n\n");
            sb.Append("Fen: ").Append(GetFen()).Append('\n');
            sb.Append("Key: ").Append(Key.ToString("X16")).Append('\n');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- move legality
        // Assumes m is pseudo-legal (generated by our generators or validated by IsPseudoLegal).
        public bool IsLegal(int m)
        {
            int us = SideToMove, them = us ^ 1;
            int from = Mv.From(m), to = Mv.To(m);
            int ksq = KingSq[us];

            if (Mv.IsEp(m))
            {
                int capSq = to ^ 8;
                ulong occ = (Occupied ^ (1UL << from) ^ (1UL << capSq)) | (1UL << to);
                return (BB.RookAttacks(ksq, occ) & (PiecesOf(them, C.Rook) | PiecesOf(them, C.Queen))) == 0
                    && (BB.BishopAttacks(ksq, occ) & (PiecesOf(them, C.Bishop) | PiecesOf(them, C.Queen))) == 0;
            }
            if (from == ksq)
            {
                if (Mv.IsCastle(m)) return true; // path checked during generation / validation
                return !IsAttacked(to, them, Occupied ^ (1UL << from));
            }
            return (Pinned & (1UL << from)) == 0 || (BB.Line[ksq * 64 + from] & (1UL << to)) != 0;
        }

        // Validates moves coming from TT / killers / counter-move tables.
        public bool IsPseudoLegal(int m)
        {
            if (m == Mv.None || m == Mv.Null) return false;
            int us = SideToMove, them = us ^ 1;
            int from = Mv.From(m), to = Mv.To(m), flag = Mv.Flag(m);
            if (flag == 6 || flag == 7) return false; // unused codes: never generated, can only come from a corrupted TT entry
            int pc = Board[from];
            if (pc == C.NoPiece || C.ColorOf(pc) != us) return false;
            if ((Colors[us] & (1UL << to)) != 0) return false;
            int type = C.TypeOf(pc);
            int target = Board[to];

            if (flag == Mv.KingCastle || flag == Mv.QueenCastle)
            {
                if (type != C.King || Checkers != 0) return false;
                int home = us == C.White ? 4 : 60;
                if (from != home) return false;
                if (flag == Mv.KingCastle)
                {
                    if ((Castling & (us == C.White ? C.WK : C.BK)) == 0 || to != home + 2) return false;
                    if ((Occupied & ((1UL << (home + 1)) | (1UL << (home + 2)))) != 0) return false;
                    if (IsAttacked(home + 1, them, Occupied) || IsAttacked(home + 2, them, Occupied)) return false;
                }
                else
                {
                    if ((Castling & (us == C.White ? C.WQ : C.BQ)) == 0 || to != home - 2) return false;
                    if ((Occupied & ((1UL << (home - 1)) | (1UL << (home - 2)) | (1UL << (home - 3)))) != 0) return false;
                    if (IsAttacked(home - 1, them, Occupied) || IsAttacked(home - 2, them, Occupied)) return false;
                }
                return true;
            }

            bool isCap = Mv.IsCapture(m);
            if (flag == Mv.EnPassant)
            {
                if (type != C.Pawn || to != EpSquare) return false;
                if ((BB.PawnAttacks[us * 64 + from] & (1UL << to)) == 0) return false;
            }
            else if (isCap != (target != C.NoPiece)) return false;

            if (type == C.Pawn)
            {
                int toRank = C.RelRank(us, to);
                if (Mv.IsPromo(m) != (toRank == 7)) return false;
                if (flag == Mv.EnPassant) { }
                else if (isCap)
                {
                    if ((BB.PawnAttacks[us * 64 + from] & (1UL << to)) == 0) return false;
                }
                else
                {
                    int push = us == C.White ? 8 : -8;
                    if (flag == Mv.DoublePush)
                    {
                        if (C.RelRank(us, from) != 1 || to != from + 2 * push) return false;
                        if (Board[from + push] != C.NoPiece || target != C.NoPiece) return false;
                    }
                    else
                    {
                        if (to != from + push || target != C.NoPiece) return false;
                    }
                }
            }
            else
            {
                if (Mv.IsPromo(m) || flag == Mv.DoublePush) return false;
                if ((BB.Attacks(type, from, Occupied) & (1UL << to)) == 0) return false;
            }

            // In check: non-king moves must capture the checker or block (single check only)
            if (Checkers != 0 && type != C.King)
            {
                if (BB.More1(Checkers)) return false;
                int checker = BB.Lsb(Checkers);
                ulong targets = BB.Between[KingSq[us] * 64 + checker] | Checkers;
                ulong dest = 1UL << to;
                if (flag == Mv.EnPassant) dest |= 1UL << (to ^ 8);
                if ((targets & dest) == 0) return false;
            }
            return true;
        }

        public bool GivesCheck(int m)
        {
            // Simple and exact: compute after-move occupancy and test.
            int us = SideToMove, them = us ^ 1;
            int from = Mv.From(m), to = Mv.To(m);
            int ksq = KingSq[them];
            int type = Mv.IsPromo(m) ? Mv.PromoType(m) : C.TypeOf(Board[from]);
            ulong occ = (Occupied ^ (1UL << from)) | (1UL << to);
            if (Mv.IsEp(m)) occ ^= 1UL << (to ^ 8);

            // direct check
            if (type == C.Pawn) { if ((BB.PawnAttacks[us * 64 + to] & (1UL << ksq)) != 0) return true; }
            else if (type != C.King && (BB.Attacks(type, to, occ) & (1UL << ksq)) != 0) return true;

            // castling rook
            if (Mv.IsCastle(m))
            {
                int rt = Mv.Flag(m) == Mv.KingCastle ? to - 1 : to + 1;
                int rf = Mv.Flag(m) == Mv.KingCastle ? to + 1 : to - 2;
                occ ^= (1UL << rf) | (1UL << rt);
                return (BB.RookAttacks(rt, occ) & (1UL << ksq)) != 0;
            }

            // discovered check by our sliders
            ulong ourRQ = (PiecesOf(us, C.Rook) | PiecesOf(us, C.Queen)) & ~(1UL << from);
            ulong ourBQ = (PiecesOf(us, C.Bishop) | PiecesOf(us, C.Queen)) & ~(1UL << from);
            return (BB.RookAttacks(ksq, occ) & ourRQ) != 0 || (BB.BishopAttacks(ksq, occ) & ourBQ) != 0;
        }

        public int ParseUciMove(string s)
        {
            var list = new ExtMove[C.MaxMoves];
            int n = MoveGen.GenerateLegal(this, list);
            for (int i = 0; i < n; i++)
                if (Mv.ToUci(list[i].Move) == s) return list[i].Move;
            return Mv.None;
        }
    }
}
