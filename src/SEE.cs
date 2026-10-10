namespace Dolphin2
{
    // Static exchange evaluation: does move m win at least `threshold` material?
    public static class SEE
    {
        public static bool Ge(Position pos, int m, int threshold)
        {
            if (Mv.IsCastle(m)) return threshold <= 0;
            int from = Mv.From(m), to = Mv.To(m);
            int[] val = Eval.SeeValue;

            int captured = Mv.IsEp(m) ? C.Pawn : pos.Board[to] == C.NoPiece ? C.NoType : C.TypeOf(pos.Board[to]);
            int swap = (captured == C.NoType ? 0 : val[captured]) - threshold;
            if (Mv.IsPromo(m)) swap += val[Mv.PromoType(m)] - val[C.Pawn];
            if (swap < 0) return false;

            int moving = Mv.IsPromo(m) ? Mv.PromoType(m) : C.TypeOf(pos.Board[from]);
            swap = val[moving] - swap;
            if (swap <= 0) return true;

            ulong occ = pos.Occupied ^ (1UL << from) ^ (1UL << to);
            if (Mv.IsEp(m)) occ ^= 1UL << (to ^ 8);
            int stm = pos.SideToMove;
            ulong attackers = pos.AttackersTo(to, occ) & occ;
            ulong bishops = pos.PieceTypeBB(C.Bishop) | pos.PieceTypeBB(C.Queen);
            ulong rooks = pos.PieceTypeBB(C.Rook) | pos.PieceTypeBB(C.Queen);
            int res = 1;

            while (true)
            {
                stm ^= 1;
                attackers &= occ;
                ulong stmAtt = attackers & pos.Colors[stm];
                if (stmAtt == 0) break;

                // pinned pieces cannot take part while their pinner is still on the board
                if (stm == pos.SideToMove)
                {
                    stmAtt &= ~pos.Pinned;
                    if (stmAtt == 0) break;
                }

                res ^= 1;
                ulong b;
                if ((b = stmAtt & pos.PiecesOf(stm, C.Pawn)) != 0)
                {
                    if ((swap = val[C.Pawn] - swap) < res) break;
                    occ ^= b & (0 - b);
                    attackers |= BB.BishopAttacks(to, occ) & bishops;
                }
                else if ((b = stmAtt & pos.PiecesOf(stm, C.Knight)) != 0)
                {
                    if ((swap = val[C.Knight] - swap) < res) break;
                    occ ^= b & (0 - b);
                }
                else if ((b = stmAtt & pos.PiecesOf(stm, C.Bishop)) != 0)
                {
                    if ((swap = val[C.Bishop] - swap) < res) break;
                    occ ^= b & (0 - b);
                    attackers |= BB.BishopAttacks(to, occ) & bishops;
                }
                else if ((b = stmAtt & pos.PiecesOf(stm, C.Rook)) != 0)
                {
                    if ((swap = val[C.Rook] - swap) < res) break;
                    occ ^= b & (0 - b);
                    attackers |= BB.RookAttacks(to, occ) & rooks;
                }
                else if ((b = stmAtt & pos.PiecesOf(stm, C.Queen)) != 0)
                {
                    if ((swap = val[C.Queen] - swap) < res) break;
                    occ ^= b & (0 - b);
                    attackers |= (BB.BishopAttacks(to, occ) & bishops) | (BB.RookAttacks(to, occ) & rooks);
                }
                else
                {
                    // king: can only capture if the opponent has no more attackers
                    return (attackers & ~pos.Colors[stm]) != 0 ? (res ^ 1) != 0 : res != 0;
                }
            }
            return res != 0;
        }
    }
}
