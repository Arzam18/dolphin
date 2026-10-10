using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Dolphin2
{
    public struct TTData
    {
        public int Move, Score, Eval, Depth, Bound;
        public bool Pv;
    }

    // Lock-less shared hash table. Each entry is two 64-bit words: (key ^ data, data).
    // data = move(16) | score(16) | eval(16) | depth(8) | gen(5) pv(1) bound(2)
    public static unsafe class TT
    {
        public const int BoundNone = 0, BoundUpper = 1, BoundLower = 2, BoundExact = 3;
        const int EntriesPerBucket = 4;
        const int DepthOffset = 8;

        static ulong* table;
        static ulong bucketCount;
        static IntPtr raw;
        public static int Generation; // 0..31

        public static void Resize(int mb)
        {
            if (raw != IntPtr.Zero) Marshal.FreeHGlobal(raw);
            mb = Math.Max(1, mb);
            ulong bytes = (ulong)mb * 1024 * 1024;
            bucketCount = bytes / (EntriesPerBucket * 16);
            raw = Marshal.AllocHGlobal((IntPtr)(long)(bucketCount * EntriesPerBucket * 16 + 64));
            // align to 64 bytes
            long addr = (long)raw;
            addr = (addr + 63) & ~63L;
            table = (ulong*)addr;
            Clear(1);
        }

        public static void Clear(int threads)
        {
            ulong words = bucketCount * EntriesPerBucket * 2;
            int parts = Math.Max(1, Math.Min(threads, 16));
            ulong chunk = words / (ulong)parts;
            Parallel.For(0, parts, i =>
            {
                ulong start = chunk * (ulong)i;
                ulong end = i == parts - 1 ? words : start + chunk;
                new Span<ulong>(table + start, (int)(end - start)).Clear();
            });
            Generation = 0;
        }

        public static void NewSearch() => Generation = (Generation + 1) & 31;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong* Bucket(ulong key)
        {
            ulong idx = (ulong)(((UInt128Hi(key, bucketCount))));
            return table + idx * EntriesPerBucket * 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong UInt128Hi(ulong a, ulong b)
        {
            return Math.BigMul(a, b, out _);
        }

        public static void Prefetch(ulong key)
        {
            if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Bucket(key));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Probe(ulong key, out TTData d)
        {
            ulong* b = Bucket(key);
            for (int i = 0; i < EntriesPerBucket; i++)
            {
                ulong k = b[2 * i], data = b[2 * i + 1];
                if ((k ^ data) == key && data != 0)
                {
                    d.Move = (int)(data & 0xFFFF);
                    d.Score = (short)((data >> 16) & 0xFFFF);
                    d.Eval = (short)((data >> 32) & 0xFFFF);
                    d.Depth = (int)((data >> 48) & 0xFF) - DepthOffset;
                    d.Bound = (int)((data >> 56) & 3);
                    d.Pv = ((data >> 58) & 1) != 0;
                    return true;
                }
            }
            d = default;
            d.Score = C.NoScore; d.Eval = C.NoScore; d.Depth = -DepthOffset;
            return false;
        }

        public static void Store(ulong key, int move, int score, int eval, int depth, int bound, bool pv)
        {
            ulong* b = Bucket(key);
            int replace = 0;
            int worst = int.MaxValue;
            for (int i = 0; i < EntriesPerBucket; i++)
            {
                ulong k = b[2 * i], data = b[2 * i + 1];
                if ((k ^ data) == key || data == 0)
                {
                    replace = i;
                    // keep old move if we have none
                    if (move == Mv.None && (k ^ data) == key) move = (int)(data & 0xFFFF);
                    // don't overwrite a deeper entry of the same position with a shallow non-exact result
                    if ((k ^ data) == key && bound != BoundExact)
                    {
                        int oldDepth = (int)((data >> 48) & 0xFF) - DepthOffset;
                        int oldGen = (int)((data >> 59) & 31);
                        if (oldGen == Generation && oldDepth > depth + 3) return;
                    }
                    goto write;
                }
                int d = (int)((data >> 48) & 0xFF) - DepthOffset;
                int g = (int)((data >> 59) & 31);
                int age = (Generation - g) & 31;
                int value = d - 8 * age;
                if (value < worst) { worst = value; replace = i; }
            }
        write:
            ulong nd = (ulong)(uint)(move & 0xFFFF)
                     | ((ulong)(ushort)(short)score << 16)
                     | ((ulong)(ushort)(short)eval << 32)
                     | ((ulong)(byte)(depth + DepthOffset) << 48)
                     | ((ulong)(bound & 3) << 56)
                     | ((pv ? 1UL : 0UL) << 58)
                     | ((ulong)Generation << 59);
            b[2 * replace] = key ^ nd;
            b[2 * replace + 1] = nd;
        }

        public static int HashFull()
        {
            int cnt = 0;
            for (int i = 0; i < 250 && (ulong)i < bucketCount; i++)
            {
                ulong* b = table + (ulong)i * EntriesPerBucket * 2;
                for (int j = 0; j < EntriesPerBucket; j++)
                {
                    ulong data = b[2 * j + 1];
                    if (data != 0 && (int)((data >> 59) & 31) == Generation) cnt++;
                }
            }
            return cnt;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ScoreToTT(int s, int ply)
        {
            if (s == C.NoScore) return s;
            if (s >= C.MateInMaxPly) return s + ply;
            if (s <= -C.MateInMaxPly) return s - ply;
            return s;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ScoreFromTT(int s, int ply, int halfmove)
        {
            if (s == C.NoScore) return s;
            if (s >= C.MateInMaxPly)
            {
                // a mate score that cannot be reached before the 50-move rule is unreliable
                if (C.Mate - s > 99 - halfmove) return C.MateInMaxPly - 1;
                return s - ply;
            }
            if (s <= -C.MateInMaxPly)
            {
                if (C.Mate + s > 99 - halfmove) return -C.MateInMaxPly + 1;
                return s + ply;
            }
            return s;
        }
    }
}
