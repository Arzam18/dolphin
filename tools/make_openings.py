"""Build a balanced opening set (EPD) from a PGN database: first N plies of each game."""
import chess.pgn, sys, random
src = sys.argv[1]; out = sys.argv[2]; plies = int(sys.argv[3]) if len(sys.argv) > 3 else 8
limit = int(sys.argv[4]) if len(sys.argv) > 4 else 4000
seen = set(); res = []
with open(src, encoding="latin-1") as f:
    while len(res) < limit * 3:
        g = chess.pgn.read_game(f)
        if g is None: break
        b = g.board(); n = 0
        for mv in g.mainline_moves():
            b.push(mv); n += 1
            if n == plies: break
        if n < plies or b.is_game_over(): continue
        key = b.epd()
        if key in seen: continue
        seen.add(key); res.append(key)
random.seed(2026)
random.shuffle(res)
res = res[:limit]
open(out, "w").write("\n".join(res) + "\n")
print(len(res), "openings written")
