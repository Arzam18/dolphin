using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Dolphin2
{
    // Efficiently updatable neural network evaluation (our own network, trained by Trainer.cs).
    // Architecture: (768 -> H) x 2 perspectives, SCReLU, 8 output buckets by piece count.
    public static unsafe partial class NNUE
    {
        public static bool Enabled;
        public static bool Loaded;
        public const string DefaultFile = "dolphin.nnue";
        public static string LoadedName = "";

        public static int H;
        static int NB;
        static short* W1;   // [768][H]
        static short* B1;   // [H]
        static float* W2;   // [NB][2H] (float output layer)
        static float[] B2;  // [NB]
        const int QA = Trainer.QA, QB = Trainer.QB;
        const int Scale = 400;

        // feature row offsets (in shorts) for [perspective][piece*64+sq]
        static readonly int[] FeatOff = new int[2 * 12 * 64];

        static short* Alloc(long count)
        {
            IntPtr p = Marshal.AllocHGlobal((IntPtr)(count * 2 + 64));
            long a = ((long)p + 63) & ~63L;
            return (short*)a;
        }

        public static void TryLoadDefault()
        {
            string ext = Path.Combine(AppContext.BaseDirectory, DefaultFile);
            if (File.Exists(ext) && Load(ext)) return;
            // embedded network
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(".nnue", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = asm.GetManifestResourceStream(name);
                if (s != null && Load(s)) { LoadedName = "embedded"; return; }
            }
        }

        public static bool Load(string path)
        {
            try
            {
                if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, path);
                using var fs = File.OpenRead(path);
                if (Load(fs)) { LoadedName = path; return true; }
            }
            catch { }
            return false;
        }

        static bool Load(Stream s)
        {
            try
            {
                using var br = new BinaryReader(s);
                if (br.ReadInt32() != 0x4E4E4C44 || br.ReadInt32() != 2) return false;
                int h = br.ReadInt32();
                int nb = br.ReadInt32();
                if (h <= 0 || h % 16 != 0 || nb != Trainer.NB) return false;
                short* w1 = Alloc(768L * h), b1 = Alloc(h);
                float* w2 = (float*)Alloc((long)nb * 2 * h * 2);
                for (long i = 0; i < 768L * h; i++) w1[i] = br.ReadInt16();
                for (int i = 0; i < h; i++) b1[i] = br.ReadInt16();
                for (long i = 0; i < (long)nb * 2 * h; i++) w2[i] = br.ReadSingle();
                var b2 = new float[nb];
                for (int i = 0; i < nb; i++) b2[i] = br.ReadSingle();
                H = h; NB = nb; W1 = w1; B1 = b1; W2 = w2; B2 = b2;
                for (int persp = 0; persp < 2; persp++)
                    for (int pc = 0; pc < 12; pc++)
                        for (int sq = 0; sq < 64; sq++)
                            FeatOff[persp * 768 + pc * 64 + sq] = Trainer.Feature(persp, pc, sq) * h;
                Loaded = true;
                Enabled = true;
                return true;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool on)
        {
            Enabled = on && Loaded;
            SearchManager.RefreshEvalMode();
        }

        // ------------------------------------------------------------------ accumulators
        public sealed class Accumulators
        {
            short* stack;
            int capacity;
            int top;
            bool recording;
            int nAdd, nSub;
            readonly int[] adds = new int[4], subs = new int[4]; // piece*64+sq

            public Accumulators()
            {
                capacity = 256;
                stack = Alloc((long)capacity * 2 * H);
            }

            public short* Current => stack + (long)top * 2 * H;

            public void Add(int pc, int sq) { if (recording) adds[nAdd++] = pc * 64 + sq; }
            public void Remove(int pc, int sq) { if (recording) subs[nSub++] = pc * 64 + sq; }
            public void Move(int pc, int from, int to)
            {
                if (!recording) return;
                subs[nSub++] = pc * 64 + from;
                adds[nAdd++] = pc * 64 + to;
            }

            public void Push()
            {
                recording = true;
                nAdd = nSub = 0;
            }

            public void Commit()
            {
                recording = false;
                if (top + 1 >= capacity)
                {
                    // rare: very long game without a root reset
                    var ns = Alloc((long)capacity * 2 * 2 * H);
                    Buffer.MemoryCopy(stack, ns, (long)capacity * 2 * H * 2, (long)capacity * 2 * H * 2);
                    stack = ns;
                    capacity *= 2;
                }
                short* src = stack + (long)top * 2 * H;
                short* dst = src + 2 * H;
                top++;
                for (int persp = 0; persp < 2; persp++)
                {
                    short* s = src + persp * H, d = dst + persp * H;
                    int po = persp * 768;
                    if (nAdd == 1 && nSub == 1) Apply11(s, d, W1 + FeatOff[po + adds[0]], W1 + FeatOff[po + subs[0]]);
                    else if (nAdd == 1 && nSub == 2) Apply12(s, d, W1 + FeatOff[po + adds[0]], W1 + FeatOff[po + subs[0]], W1 + FeatOff[po + subs[1]]);
                    else if (nAdd == 2 && nSub == 2) Apply22(s, d, W1 + FeatOff[po + adds[0]], W1 + FeatOff[po + adds[1]], W1 + FeatOff[po + subs[0]], W1 + FeatOff[po + subs[1]]);
                    else
                    {
                        Buffer.MemoryCopy(s, d, H * 2, H * 2);
                        for (int i = 0; i < nAdd; i++) AddRow(d, W1 + FeatOff[po + adds[i]]);
                        for (int i = 0; i < nSub; i++) SubRow(d, W1 + FeatOff[po + subs[i]]);
                    }
                }
            }

            public void Pop() { top--; }

            public void Refresh(Position pos)
            {
                top = 0;
                recording = false;
                short* acc = stack;
                for (int persp = 0; persp < 2; persp++)
                {
                    short* d = acc + persp * H;
                    Buffer.MemoryCopy(B1, d, H * 2, H * 2);
                    ulong b = pos.Occupied;
                    while (b != 0)
                    {
                        int sq = BB.PopLsb(ref b);
                        AddRow(d, W1 + FeatOff[persp * 768 + pos.Board[sq] * 64 + sq]);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void AddRow(short* d, short* w)
        {
            int vs = Vector<short>.Count;
            for (int i = 0; i < H; i += vs)
                Unsafe.Write(d + i, Unsafe.Read<Vector<short>>(d + i) + Unsafe.Read<Vector<short>>(w + i));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SubRow(short* d, short* w)
        {
            int vs = Vector<short>.Count;
            for (int i = 0; i < H; i += vs)
                Unsafe.Write(d + i, Unsafe.Read<Vector<short>>(d + i) - Unsafe.Read<Vector<short>>(w + i));
        }

        static void Apply11(short* s, short* d, short* a, short* r)
        {
            int vs = Vector<short>.Count;
            for (int i = 0; i < H; i += vs)
                Unsafe.Write(d + i, Unsafe.Read<Vector<short>>(s + i) + Unsafe.Read<Vector<short>>(a + i) - Unsafe.Read<Vector<short>>(r + i));
        }

        static void Apply12(short* s, short* d, short* a, short* r1, short* r2)
        {
            int vs = Vector<short>.Count;
            for (int i = 0; i < H; i += vs)
                Unsafe.Write(d + i, Unsafe.Read<Vector<short>>(s + i) + Unsafe.Read<Vector<short>>(a + i)
                    - Unsafe.Read<Vector<short>>(r1 + i) - Unsafe.Read<Vector<short>>(r2 + i));
        }

        static void Apply22(short* s, short* d, short* a1, short* a2, short* r1, short* r2)
        {
            int vs = Vector<short>.Count;
            for (int i = 0; i < H; i += vs)
                Unsafe.Write(d + i, Unsafe.Read<Vector<short>>(s + i) + Unsafe.Read<Vector<short>>(a1 + i) + Unsafe.Read<Vector<short>>(a2 + i)
                    - Unsafe.Read<Vector<short>>(r1 + i) - Unsafe.Read<Vector<short>>(r2 + i));
        }

        // ------------------------------------------------------------------ output
        public static int Evaluate(Position pos)
        {
            short* acc = pos.Acc.Current;
            int stm = pos.SideToMove;
            int bucket = Trainer.Bucket(BB.PopCount(pos.Occupied));
            short* us = acc + stm * H, them = acc + (stm ^ 1) * H;
            float* w = W2 + bucket * 2 * H;
            float sum = (Dot(us, w) + Dot(them, w + H)) * (1f / (QA * QA)) + B2[bucket];
            int v = (int)(sum * Scale);
            // damp towards zero as the 50-move counter grows
            v = v * (200 - pos.Halfmove) / 200;
            return Math.Clamp(v, -C.TbWinInMaxPly + 1, C.TbWinInMaxPly - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Dot(short* a, float* w)
        {
            if (Avx2.IsSupported)
            {
                var zero = Vector256<short>.Zero;
                var qa = Vector256.Create((short)QA);
                var sum0 = Vector256<float>.Zero;
                var sum1 = Vector256<float>.Zero;
                for (int i = 0; i < H; i += 16)
                {
                    var x = Avx2.Min(Avx2.Max(Avx.LoadVector256(a + i), zero), qa);
                    var lo = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(x.GetLower()));
                    var hi = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(x.GetUpper()));
                    sum0 = Avx.Add(sum0, Avx.Multiply(Avx.Multiply(lo, lo), Avx.LoadVector256(w + i)));
                    sum1 = Avx.Add(sum1, Avx.Multiply(Avx.Multiply(hi, hi), Avx.LoadVector256(w + i + 8)));
                }
                var s = Avx.Add(sum0, sum1);
                var s4 = Sse.Add(s.GetLower(), s.GetUpper());
                return s4.GetElement(0) + s4.GetElement(1) + s4.GetElement(2) + s4.GetElement(3);
            }
            float t = 0;
            for (int i = 0; i < H; i++)
            {
                float x = Math.Clamp((int)a[i], 0, QA);
                t += x * x * w[i];
            }
            return t;
        }

        // evaluation from scratch (debug / verification)
        public static int EvaluateFresh(Position pos)
        {
            var acc = new Accumulators();
            acc.Refresh(pos);
            var saved = pos.Acc;
            pos.Acc = acc;
            int v = Evaluate(pos);
            pos.Acc = saved;
            return v;
        }
    }

    public sealed partial class Position
    {
        public void EnableNNUE()
        {
            if (!NNUE.Loaded) { Acc = null; return; }
            Acc = new NNUE.Accumulators();
            Acc.Refresh(this);
        }

        public void DisableNNUE() { Acc = null; }
    }
}
