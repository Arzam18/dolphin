/* Thin C wrapper around Fathom (MIT, see fathom/LICENSE) so that the C# engine can load it as a plain DLL.
   Bitboards use a1 = 0 ... h8 = 63, the same square numbering as Dolphin. */
#include <stdint.h>
#include <stdbool.h>
#include "fathom/tbprobe.h"

#ifdef _WIN32
#define DTB_EXPORT __declspec(dllexport)
#else
#define DTB_EXPORT __attribute__((visibility("default")))
#endif

/* Loads the tables found in path (directories separated by ';' on Windows, ':' elsewhere).
   Returns the largest number of pieces covered, 0 if nothing was loaded. */
DTB_EXPORT int dtb_init(const char *path)
{
    if (!tb_init(path)) return 0;
    return (int)TB_LARGEST;
}

DTB_EXPORT void dtb_free(void) { tb_free(); }

/* WDL from the side to move: 0 loss, 1 blessed loss, 2 draw, 3 cursed win, 4 win; 0xFFFFFFFF if the probe failed.
   Ignores the 50-move counter and needs no castling rights. Thread safe. */
DTB_EXPORT unsigned dtb_probe_wdl(uint64_t white, uint64_t black, uint64_t kings, uint64_t queens, uint64_t rooks,
                                  uint64_t bishops, uint64_t knights, uint64_t pawns, unsigned ep, int white_to_move)
{
    return tb_probe_wdl_impl(white, black, kings, queens, rooks, bishops, knights, pawns, ep, white_to_move != 0);
}

/* Root probe (DTZ). Fills results with one entry per legal move, terminated by 0xFFFFFFFF (array of at least 193).
   Not thread safe: call it once before the search threads start. */
DTB_EXPORT unsigned dtb_probe_root(uint64_t white, uint64_t black, uint64_t kings, uint64_t queens, uint64_t rooks,
                                   uint64_t bishops, uint64_t knights, uint64_t pawns, unsigned rule50, unsigned ep,
                                   int white_to_move, unsigned *results)
{
    return tb_probe_root_impl(white, black, kings, queens, rooks, bishops, knights, pawns, rule50, ep,
                              white_to_move != 0, results);
}
