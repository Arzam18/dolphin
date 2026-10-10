"""Compare Dolphin2 perft against Stockfish (used only as a reference oracle).

usage: python perft_check.py [path/to/Dolphin2.exe] [path/to/stockfish]
       (the Stockfish path can also be given in the STOCKFISH environment variable)
"""
import os, subprocess, sys, re

DOL = sys.argv[1] if len(sys.argv) > 1 else r"bin\Release\net5.0\Dolphin2.exe"
SF = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("STOCKFISH", "stockfish")

CASES = [
    ("startpos", 5),
    ("fen r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 4),
    ("fen 8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 6),
    ("fen r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 5),
    ("fen r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1", 5),
    ("fen rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 4),
    ("fen r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", 4),
    ("fen 8/8/8/8/k2Pp2Q/8/8/3K4 b - d3 0 1", 5),        # e.p. capture would expose king
    ("fen 8/8/8/2k5/2pP4/8/B7/4K3 b - d3 0 3", 5),
    ("fen r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", 4),
    ("fen r3k2r/8/3Q4/8/8/5q2/8/R3K2R b KQkq - 0 1", 4),  # castling through check
    ("fen 2K2r2/4P3/8/8/8/8/8/3k4 w - - 0 1", 6),        # promotion
    ("fen 8/P1k5/K7/8/8/8/8/8 w - - 0 1", 6),
    ("fen 8/k1P5/8/1K6/8/8/8/8 w - - 0 1", 7),
    ("fen 3k4/3p4/8/K1P4r/8/8/8/8 b - - 0 1", 6),
    ("fen 8/8/1k6/2b5/2pP4/8/5K2/8 b - d3 0 1", 6),
    ("fen 5k2/8/8/8/8/8/8/4K2R w K - 0 1", 6),
    ("fen 1k6/1b6/8/8/7R/8/8/4K2R b K - 0 1", 5),
    ("fen 8/8/2k5/5q2/5n2/8/5K2/8 b - - 0 1", 4),
    ("fen K1k5/8/P7/8/8/8/8/8 w - - 0 1", 6),
]

def run(exe, pos, depth, sf):
    p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    cmds = f"position {pos}\ngo perft {depth}\n"
    if not sf: cmds = "isready\n" + cmds
    out, _ = p.communicate(cmds + ("quit\n" if sf else "quit\n"), timeout=600)
    div = {}
    total = None
    for line in out.splitlines():
        m = re.match(r"^([a-h][1-8][a-h][1-8][nbrq]?): (\d+)$", line.strip())
        if m: div[m.group(1)] = int(m.group(2))
        m = re.match(r"^Nodes searched: (\d+)", line.strip())
        if m: total = int(m.group(1))
    return total, div

bad = 0
for pos, d in CASES:
    t1, d1 = run(SF, pos, d, True)
    t2, d2 = run(DOL, pos, d, False)
    ok = t1 == t2
    print(("OK  " if ok else "FAIL"), d, t1, t2, pos[:70])
    if not ok:
        bad += 1
        for k in sorted(set(d1) | set(d2)):
            if d1.get(k) != d2.get(k): print("   ", k, "sf", d1.get(k), "dolphin", d2.get(k))
print("failures:", bad)
