# Development tools

Python scripts used while developing Dolphin. They need Python 3 and [python-chess](https://python-chess.readthedocs.io/):

```bash
pip install python-chess
```

| File | Purpose |
|---|---|
| `perft_check.py` | Compares Dolphin's perft results with Stockfish (used only as a reference answer) on 20 tricky positions |
| `match.py` | Plays two engines against each other in parallel and reports Elo ± error, LOS and SPRT; can save a PGN |
| `make_openings.py` | Builds an EPD opening set from a PGN file (the position after the first 8 half-moves of each game) |
| `openings8.epd` | 1372 opening positions used for matches and self-play data generation |

## perft_check.py

```bash
python perft_check.py path/to/Dolphin2.exe path/to/stockfish
```

The Stockfish path can also be set with the `STOCKFISH` environment variable. Every line must print `OK`, and the last line must be `failures: 0`.

## match.py

```bash
python match.py --e1 path/to/Dolphin2.exe --e2 path/to/other_engine.exe \
    --o2 "Hash=16,OwnBook=false" --games 200 --tc 10+0.1 --conc 12 \
    --openings openings8.epd --pgn out.pgn
```

| Option | Meaning |
|---|---|
| `--e1`, `--e2` | The two engines |
| `--o1`, `--o2` | UCI options for each engine, e.g. `"Hash=16,Threads=1"` |
| `--games` | Number of games (each opening is played with both colors) |
| `--tc` | Time control `seconds+increment` |
| `--conc` | Games played at the same time |
| `--openings` | EPD file with starting positions |
| `--sprt` | SPRT bounds in Elo, e.g. `0,5` |
| `--pgn` | Save all games to this file |
| `--proto2 xboard` | Use this when the second engine is a WinBoard/xboard engine |
| `--nodes1 N` | Fixed number of nodes per move for engine 1 |

Keep the total number of threads (`--conc` × threads per engine) at or below the number of CPU cores, otherwise results become unreliable.

## Engine commands used for development

```
Dolphin2.exe bench [depth]     # regression check: total nodes + speed
Dolphin2.exe perft 6           # move generator check
Dolphin2.exe datagen threads=14 nodes=5000 games=100000 out=data/gen1.bin book=tools/openings8.epd
Dolphin2.exe train data=data/gen1.bin,data/gen2.bin epochs=20 threads=14 lambda=0.3 out=nets/net1.nnue
Dolphin2.exe export nets/net1.nnue.f32 nets/net1.nnue
```

In UCI mode the engine also understands `d` (print the board), `eval` and `nnuecheck` (checks that the incrementally updated network matches a full recomputation).
