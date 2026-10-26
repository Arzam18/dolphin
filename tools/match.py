"""Concurrent engine-vs-engine match runner (python-chess), with Elo and SPRT.

usage:
  python match.py --e1 path/to/A.exe --e2 path/to/B.exe --games 200 --tc 8+0.08 --conc 12
  options: --o1 "Hash=16,Threads=1" --o2 "..." --openings openings8.epd --sprt 0,5 --pgn out.pgn
           --proto2 xboard (for WinBoard engines) --nodes1 N (fixed nodes per move for e1)
"""
import argparse, math, os, random, sys, threading, time
from concurrent.futures import ThreadPoolExecutor
import chess, chess.engine, chess.pgn


def parse_opts(s):
    d = {}
    if s:
        for kv in s.split(","):
            k, v = kv.split("=", 1)
            d[k.strip()] = v.strip()
    return d


def open_engine(path, proto, opts, env=None):
    cwd = os.path.dirname(os.path.abspath(path))
    environ = dict(os.environ)
    if env:
        for kv in env.split(","):
            k, v = kv.split("=", 1); environ[k] = v
    if proto == "xboard":
        e = chess.engine.SimpleEngine.popen_xboard(os.path.abspath(path), cwd=cwd, env=environ)
    else:
        e = chess.engine.SimpleEngine.popen_uci(os.path.abspath(path), cwd=cwd, env=environ)
    conv = {}
    for k, v in opts.items():
        if k in e.options:
            o = e.options[k]
            if o.type == "spin": v = int(v)
            elif o.type == "check": v = v.lower() == "true"
            conv[k] = v
    if conv: e.configure(conv)
    return e


def elo_from_score(p):
    p = min(max(p, 1e-6), 1 - 1e-6)
    return -400 * math.log10(1 / p - 1)


def stats(w, d, l):
    n = w + d + l
    if n == 0: return 0, 0, 0.5
    p = (w + 0.5 * d) / n
    var = (w * (1 - p) ** 2 + d * (0.5 - p) ** 2 + l * (0 - p) ** 2) / n
    se = math.sqrt(var / n) if n > 1 else 0
    elo = elo_from_score(p)
    lo, hi = elo_from_score(p - 1.96 * se), elo_from_score(p + 1.96 * se)
    los = 0.5 * (1 + math.erf((w - l) / math.sqrt(2 * (w + l)))) if w + l > 0 else 0.5
    return elo, (hi - lo) / 2, los


def llr(w, d, l, elo0, elo1):
    # GSPRT approximation (trinomial)
    n = w + d + l
    if n == 0 or w == 0 or l == 0: return 0.0
    p = (w + 0.5 * d) / n
    var = (w * (1 - p) ** 2 + d * (0.5 - p) ** 2 + l * p ** 2) / n
    if var <= 0: return 0.0
    s0 = 1 / (1 + 10 ** (-elo0 / 400)); s1 = 1 / (1 + 10 ** (-elo1 / 400))
    return n * (s1 - s0) * (2 * p - s0 - s1) / (2 * var)


class Match:
    def __init__(self, a):
        self.a = a
        self.lock = threading.Lock()
        self.w = self.d = self.l = 0
        self.done = 0
        self.stop = False
        self.reasons = {}
        self.pgn_lock = threading.Lock()
        with open(a.openings) as f:
            self.openings = [x.strip() for x in f if x.strip()]
        rnd = random.Random(a.seed)
        rnd.shuffle(self.openings)
        base, inc = a.tc.split("+") if "+" in a.tc else (a.tc, "0")
        self.base = float(base); self.inc = float(inc)
        self.next_game = 0

    def play_game(self, e, names, fen, white_first, gid):
        # e[0] = engine1, e[1] = engine2. white_first: engine1 is white
        board = chess.Board(fen)
        clocks = {chess.WHITE: self.base, chess.BLACK: self.base}
        eng = {chess.WHITE: e[0] if white_first else e[1], chess.BLACK: e[1] if white_first else e[0]}
        nodes = {chess.WHITE: (self.a.nodes1 if white_first else self.a.nodes2),
                 chess.BLACK: (self.a.nodes2 if white_first else self.a.nodes1)}
        game = chess.pgn.Game()
        game.setup(board)
        game.headers["White"] = names[0] if white_first else names[1]
        game.headers["Black"] = names[1] if white_first else names[0]
        node = game
        result, reason = None, ""
        win_streak = {chess.WHITE: 0, chess.BLACK: 0}
        last_cp = {chess.WHITE: None, chess.BLACK: None}
        draw_streak = 0
        while True:
            if board.is_game_over(claim_draw=True):
                result = board.result(claim_draw=True)
                reason = "rules"
                break
            side = board.turn
            if nodes[side]:
                limit = chess.engine.Limit(nodes=nodes[side])
            else:
                limit = chess.engine.Limit(white_clock=max(clocks[chess.WHITE], 0.001), black_clock=max(clocks[chess.BLACK], 0.001),
                                           white_inc=self.inc, black_inc=self.inc)
            t0 = time.perf_counter()
            try:
                r = eng[side].play(board, limit, game=gid, info=chess.engine.INFO_SCORE)
            except Exception as ex:
                result = "0-1" if side == chess.WHITE else "1-0"; reason = "crash:" + str(ex)[:60]
                break
            el = time.perf_counter() - t0
            if not nodes[side]:
                clocks[side] -= el
                if clocks[side] < -0.05:
                    result = "0-1" if side == chess.WHITE else "1-0"; reason = "time"
                    break
                clocks[side] += self.inc
            if r.move is None or r.move not in board.legal_moves:
                result = "0-1" if side == chess.WHITE else "1-0"; reason = "illegal"
                break
            sc = r.info.get("score")
            board.push(r.move)
            node = node.add_variation(r.move)
            # adjudication
            if sc is not None:
                cp = sc.pov(side).score(mate_score=100000)
                if cp is not None:
                    last_cp[side] = cp
                    if cp >= 1000: win_streak[side] += 1
                    else: win_streak[side] = 0
                    if abs(cp) <= 8 and board.fullmove_number > 40: draw_streak += 1
                    else: draw_streak = 0
            # both engines must agree: the winner's last 4 scores >= +1000 and the loser's last score <= -1000
            if win_streak[side] >= 4 and last_cp[not side] is not None and last_cp[not side] <= -1000 and self.a.adjudicate:
                result = "1-0" if side == chess.WHITE else "0-1"; reason = "adj-win"
                break
            if draw_streak >= 12 and self.a.adjudicate:
                result = "1/2-1/2"; reason = "adj-draw"; break
            if board.ply() > 600:
                result = "1/2-1/2"; reason = "maxlen"; break
        game.headers["Result"] = result
        game.headers["Termination"] = reason
        return result, reason, game

    def worker(self, wid):
        a = self.a
        try:
            e1 = open_engine(a.e1, a.proto1, parse_opts(a.o1), a.env1)
            e2 = open_engine(a.e2, a.proto2, parse_opts(a.o2), a.env2)
        except Exception as ex:
            print("engine start failed:", ex); self.stop = True; return
        names = [a.n1 or os.path.basename(a.e1), a.n2 or os.path.basename(a.e2)]
        while not self.stop:
            with self.lock:
                gi = self.next_game
                if gi >= a.games: break
                self.next_game += 1
            fen = self.openings[(gi // 2) % len(self.openings)]
            if fen.count(" ") < 5: fen = fen + " 0 1"
            white_first = gi % 2 == 0
            res, reason, game = self.play_game([e1, e2], names, fen, white_first, gi)
            s1 = {"1-0": 1, "0-1": 0}.get(res, 0.5)
            if not white_first: s1 = 1 - s1 if res != "1/2-1/2" else 0.5
            with self.lock:
                if s1 == 1: self.w += 1
                elif s1 == 0: self.l += 1
                else: self.d += 1
                self.done += 1
                self.reasons[reason.split(":")[0]] = self.reasons.get(reason.split(":")[0], 0) + 1
                if reason.startswith("crash") or reason in ("time", "illegal"):
                    loser = names[0] if s1 == 0 else names[1]
                    print(f"  game {gi}: {reason} ({loser} lost)", flush=True)
                self.report()
                if a.sprt:
                    e0, e1_ = map(float, a.sprt.split(","))
                    L = llr(self.w, self.d, self.l, e0, e1_)
                    lo, hi = math.log(0.05 / 0.95), math.log(0.95 / 0.05)
                    if L >= hi or L <= lo:
                        print(f"SPRT finished: LLR {L:.2f} -> {'H1 accepted' if L >= hi else 'H0 accepted'}", flush=True)
                        self.stop = True
            if a.pgn:
                with self.pgn_lock:
                    with open(a.pgn, "a") as f:
                        print(game, file=f, end="\n\n")
        for e in (e1, e2):
            try: e.quit()
            except Exception: pass

    def report(self):
        if self.done % self.a.report and self.done != self.a.games: return
        elo, err, los = stats(self.w, self.d, self.l)
        extra = ""
        if self.a.sprt:
            e0, e1_ = map(float, self.a.sprt.split(","))
            extra = f" LLR {llr(self.w, self.d, self.l, e0, e1_):.2f}"
        print(f"[{self.done}/{self.a.games}] +{self.w} ={self.d} -{self.l}  Elo {elo:+.1f} +/- {err:.1f}  LOS {los*100:.1f}%{extra}  {self.reasons}", flush=True)

    def run(self):
        with ThreadPoolExecutor(self.a.conc) as ex:
            list(ex.map(self.worker, range(self.a.conc)))
        elo, err, los = stats(self.w, self.d, self.l)
        print(f"FINAL {self.a.n1 or os.path.basename(self.a.e1)} vs {self.a.n2 or os.path.basename(self.a.e2)}: "
              f"+{self.w} ={self.d} -{self.l}  score {(self.w + self.d / 2) / max(1, self.done) * 100:.1f}%  Elo {elo:+.1f} +/- {err:.1f}", flush=True)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--e1", required=True); p.add_argument("--e2", required=True)
    p.add_argument("--n1"); p.add_argument("--n2")
    p.add_argument("--o1", default="Hash=16"); p.add_argument("--o2", default="Hash=16")
    p.add_argument("--proto1", default="uci"); p.add_argument("--proto2", default="uci")
    p.add_argument("--env1", default=""); p.add_argument("--env2", default="")
    p.add_argument("--games", type=int, default=100)
    p.add_argument("--tc", default="8+0.08")
    p.add_argument("--nodes1", type=int, default=0); p.add_argument("--nodes2", type=int, default=0)
    p.add_argument("--conc", type=int, default=8)
    p.add_argument("--openings", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "openings8.epd"))
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--sprt", default="")
    p.add_argument("--pgn", default="")
    p.add_argument("--report", type=int, default=10)
    p.add_argument("--adjudicate", type=int, default=1)
    Match(p.parse_args()).run()
