# Dolphin Chess Engine

Dolphin is a free, open-source **UCI chess engine written in C#**.
It uses bitboards, a modern alpha-beta search and an **NNUE neural-network evaluation that was trained from scratch on Dolphin's own self-play games**.

You can load it into any chess GUI that speaks UCI (Arena, Cute Chess, BanksiaGUI, ...) and play against it, analyse with it, or run engine-vs-engine matches.

- **Author:** Nguyễn Viết Huy (Vietnam)
- **Language:** C# (.NET), single project, no external dependencies
- **Protocol:** UCI
- **License:** [MIT](LICENSE)

---

## Contents

- [Download](#download)
- [Quick start](#quick-start)
- [Using Dolphin in a chess GUI](#using-dolphin-in-a-chess-gui)
- [Troubleshooting](#troubleshooting)
- [UCI options](#uci-options)
- [Command-line tools](#command-line-tools)
- [How the engine works](#how-the-engine-works)
- [Training your own network](#training-your-own-network)
- [Testing](#testing)
- [Project layout](#project-layout)
- [License](#license)

---

## Download

Ready-to-run Windows builds are on the [Releases page](https://github.com/aiagentvn/dolphin/releases/latest):

| File | What it is |
|---|---|
| `Dolphin-<version>-windows-x64.zip` | **Recommended.** Self-contained: includes its own .NET runtime, nothing to install. Keep all files of the folder together. |
| `Dolphin-<version>-windows-x64-requires-dotnet.zip` | Small package for machines that already have the [.NET Runtime](https://dotnet.microsoft.com/download/dotnet) 5.0 or newer. Keep the four files `Dolphin2.exe`, `Dolphin2.dll`, `Dolphin2.deps.json`, `Dolphin2.runtimeconfig.json` together. |

In both packages the neural network is embedded in the program, so no separate network file is needed. Unzip, then load `Dolphin2.exe` into your GUI as a UCI engine.

On Linux or macOS, build from source (see below).

---

## Quick start

### Requirements

| What | Version |
|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 5.0 or newer (to build) |
| .NET runtime | 5.0 or newer, to run a framework-dependent build (it rolls forward to newer runtimes automatically). Not needed for the self-contained release package. |
| CPU | 64-bit x86 (x64). CPUs with **AVX2** get a fast NNUE path; older x64 CPUs fall back to a slower scalar path. |
| OS | Windows (tested). Linux and macOS on x64 should also work through `dotnet`, but have not been tested yet. |

### Build

```bash
git clone https://github.com/aiagentvn/dolphin.git
cd dolphin
dotnet build -c Release
```

The engine is created at:

```
bin/Release/net5.0/Dolphin2.exe      (Windows)
bin/Release/net5.0/Dolphin2.dll      (any OS: run with "dotnet Dolphin2.dll")
```

The neural network file `dolphin.nnue` in the repository root is **embedded into the program during the build**, so the executable works on its own. You do not need to copy the network next to it.

> **Note:** the project targets .NET 5. If you build with a newer SDK (6, 7, 8, ...), you may see warning `NETSDK1138` saying .NET 5 is out of support. This is expected: the build still succeeds and the SDK downloads the .NET 5 reference pack automatically.

### Check that it works

```bash
cd bin/Release/net5.0
Dolphin2.exe perft 5     # move generator self-check, must print "Nodes searched: 4865609"
Dolphin2.exe bench       # fixed search benchmark, prints node count and speed (nodes/second)
```

### Build a ready-to-use release folder (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File build-release.ps1
```

This builds into `release/Dolphin2/`, verifies that the network is embedded correctly, and runs a short test search. Copy that folder wherever you like.

---

## Using Dolphin in a chess GUI

**Arena**

1. `Engines` → `Install New Engine...`
2. Select `Dolphin2.exe` from the unzipped release folder (or from `release/Dolphin2/` if you built it yourself) and choose the **UCI** type.
3. Optional: `Engines` → `Configure` to change Hash, Threads, etc. (see below).

**Cute Chess (GUI or cutechess-cli)**

Add a new engine, set the command to the path of `Dolphin2.exe` and the protocol to **UCI**.

Dolphin has no built-in opening book. Use your GUI's book if you want varied openings.

---

## Troubleshooting

**The engine does not move and loses on time from the first move.**
Open a command prompt in the engine folder, run `Dolphin2.exe` and type `uci`. You should see `uciok`.

- `The library 'hostpolicy.dll' ... runtimeconfig.json was not found`: you are using the package that requires .NET and one of its four files is missing. Copy the whole folder again.
- A message that the framework `Microsoft.NETCore.App` was not found: the .NET Runtime is not installed. Install it, or use the self-contained package.
- Windows reports a missing DLL: you are using the self-contained package and not all files were copied. Copy the whole folder again.

**The engine loses on time in very fast games.**
Increase the UCI option `Move Overhead` (for example to 100–200 ms).

---

## UCI options

| Option | Default | Description |
|---|---|---|
| `Hash` | 64 (MB) | Size of the transposition table. 256–1024 MB is a good choice for long games. |
| `Threads` | 1 | Number of search threads (Lazy SMP). Set it to the number of physical CPU cores. When two engines play each other on one machine, keep the total threads of both engines at or below the number of cores. |
| `Move Overhead` | 50 (ms) | Time reserved per move for GUI/communication delays. Increase to 100–200 if the engine loses on time. |
| `Ponder` | false | Think on the opponent's time (supports `go ponder` / `ponderhit`). |
| `Clear Hash` | – | Empties the transposition table. |
| `UseNNUE` | true | Use the neural network. When off, Dolphin uses its hand-written evaluation (much weaker). |
| `EvalFile` | `dolphin.nnue` | Load a different network file. The default network is embedded in the program. |

---

## Command-line tools

Besides the normal UCI mode, the executable accepts a few commands. They can be given as program arguments (`Dolphin2.exe bench`) or typed in UCI mode.

| Command | What it does |
|---|---|
| `bench [depth]` | Searches a fixed set of positions (default depth 13). Prints total nodes and speed. Used as a regression check: the node count only changes when the search changes. |
| `perft <depth>` | Counts all legal move sequences from the current position and prints a per-move breakdown. Verifies the move generator. |
| `d` | Prints the current board (UCI mode). |
| `eval` | Prints the hand-written and NNUE evaluation of the current position (UCI mode). |
| `nnuecheck` | Verifies that the incrementally updated NNUE matches a full recomputation (UCI mode). |
| `datagen ...` | Generates self-play training data (see below). |
| `train ...` | Trains an NNUE network (see below). |
| `export <in.f32> <out.nnue>` | Converts a float network checkpoint into the quantized `.nnue` format the engine loads. |

---

## How the engine works

### Board and move generation

- 64-bit **bitboards**, with **magic bitboards** for sliding pieces (the magic numbers are generated at start-up).
- Full legal move generation, including castling, en passant and promotions. Verified with `perft` against a reference engine on many tricky positions (see [Testing](#testing)).
- Zobrist hashing for the transposition table and repetition detection.

### Search

- Principal Variation Search (alpha-beta) with iterative deepening and aspiration windows
- Lock-free shared transposition table
- Quiescence search; Static Exchange Evaluation (SEE) for move ordering and pruning
- Pruning: null-move pruning, reverse futility pruning, razoring, futility pruning, late move pruning, ProbCut
- Reductions and extensions: late move reductions (LMR), singular extensions, internal iterative reduction (IIR)
- Move ordering: hash move, SEE-sorted captures, killer moves, history, countermove and continuation history
- Multi-threading with **Lazy SMP**
- Time management for all common time controls, plus pondering
- Designed to avoid memory allocation during search (the hot code uses `unsafe` C#)

### Evaluation

Dolphin has two evaluation functions:

1. **NNUE (default).** A small neural network:
   - input: 768 features (12 piece types × 64 squares), seen from both sides' perspectives
   - one hidden layer of 384 neurons per perspective, SCReLU activation
   - 8 output buckets, selected by the number of pieces on the board
   - updated incrementally as moves are made, with an AVX2 fast path

   The network was trained **only on positions from Dolphin's own self-play games**. No network or code from other engines was used.

2. **Hand-written evaluation** (used when `UseNNUE` is off, and to generate the very first training data). Tapered middlegame/endgame scores with piece-square tables, mobility, knight and bishop outposts, rooks on open/half-open files and the 7th rank, pawn structure (isolated, doubled, backward, connected and passed pawns, cached in a pawn hash table), king safety (pawn shelter, attacks near the king) and king activity in the endgame.

---

## Training your own network

The complete training pipeline is built into the engine. Nothing else needs to be installed.

**1. Generate self-play data**

```bash
Dolphin2.exe datagen threads=14 nodes=5000 games=100000 out=data/gen1.bin book=tools/openings8.epd
```

| Argument | Meaning |
|---|---|
| `threads` | Number of games played in parallel |
| `nodes` | Search nodes per move (5000 is a good speed/quality balance) |
| `games` | How many games to play |
| `out` | Output file (positions + game results) |
| `book` | Starting positions in EPD format, for variety |
| `seed`, `hash` | Random seed and hash size per game (optional) |

The first round of data for the bundled network was generated with the hand-written evaluation. Later rounds used the newest network, so each generation of data is of higher quality than the previous one.

**2. Train**

```bash
Dolphin2.exe train data=data/gen1.bin,data/gen2.bin hidden=384 epochs=25 threads=8 lambda=0.35 out=nets/my.nnue
```

| Argument | Default | Meaning |
|---|---|---|
| `data` | – | One or more data files, comma separated |
| `hidden` | 256 | Hidden layer size (the bundled network uses 384) |
| `epochs` | 30 | Training passes over the data |
| `batch` | 16384 | Batch size |
| `lr` | 0.001 | Learning rate |
| `lambda` | 0.3 | Blend between search score (0) and game result (1) as the training target |
| `threads` | CPU count − 2 | Training threads |
| `max` | all | Use at most this many positions |
| `resume` | – | Continue from a `.f32` checkpoint |
| `mirror` | 0 | `mirror=1` adds horizontally mirrored positions |
| `out` | `dolphin.nnue` | Output network. A float checkpoint `<out>.f32` is saved too. |

The bundled `dolphin.nnue` was trained this way: about 44 million self-play positions from five generations of data (searched at 5000 nodes per move, each generation played with the best network available at the time), hidden size 384, 25 epochs, `lambda=0.35`, `mirror=1`.

**3. Use the network**

Either set the UCI option `EvalFile` to your file, or copy it over `dolphin.nnue` in the repository root and rebuild, so it gets embedded in the executable:

```powershell
powershell -ExecutionPolicy Bypass -File build-release.ps1 -Net nets/my.nnue
```

---

## Testing

The `tools/` folder contains the Python scripts used during development (they need `pip install python-chess`). See [tools/README.md](tools/README.md).

- **`perft_check.py`**: compares Dolphin's perft counts with a reference engine (Stockfish) on 20 difficult positions (en passant pins, castling through check, promotions, ...).
- **`match.py`**: plays engine-vs-engine matches in parallel and reports the Elo difference with error bars, LOS and an SPRT result. Search and evaluation changes were tested with matches like this before they were kept.
- **`bench`**: the node count of `Dolphin2.exe bench` is a quick check that a change did not alter the search by accident.

Stockfish was used only as an external tool (reference perft results and a sparring partner). Dolphin does not contain any Stockfish code or networks.

---

## Project layout

```
Dolphin2.csproj        project file (builds the engine, embeds dolphin.nnue)
dolphin.nnue           the default neural network
build-release.ps1      builds a ready-to-use release folder
src/
  Uci.cs               UCI protocol and command-line commands
  Types.cs             basic types and constants
  Bitboards.cs         bitboard helpers, magic bitboard attacks
  Position.cs          board state, make/unmake move, Zobrist keys
  MoveGen.cs           legal move generation
  MovePicker.cs        staged move ordering
  SEE.cs               static exchange evaluation
  Search.cs            the alpha-beta search
  SearchManager.cs     threads (Lazy SMP), time management, pondering
  TT.cs                transposition table
  Eval.cs              hand-written evaluation
  NNUE.cs              neural network evaluation
  DataGen.cs           self-play data generation
  Trainer.cs           network training and export
  Bench.cs, Perft.cs   benchmark and perft
  Tune.cs              lets search parameters be overridden by DOLPHIN_P_<NAME>
                       environment variables, for A/B testing without rebuilding
tools/                 development scripts (perft check, matches, opening set)
```

---

## License

Dolphin is released under the [MIT License](LICENSE). You are free to use, modify and redistribute it, including in your own projects, as long as the copyright notice is kept.

Contributions, bug reports and match results are welcome. Please open an issue or a pull request.
