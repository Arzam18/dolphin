using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Dolphin2
{
    // Our own NNUE trainer: (768 -> H) x 2 perspectives -> 8 output buckets, SCReLU activation.
    // Trained on self-play data (PackedPos). Loss: MSE between sigmoid(output) and a blend of
    // game result and sigmoid(search score / 400).
    public static class Trainer
    {
        public const int In = 768;
        public const int NB = 8;
        public const float EvalScale = 400f;

        sealed class Net
        {
            public int H;
            public float[] W1, B1, W2, B2;
            public Net(int h)
            {
                H = h;
                W1 = new float[In * h]; B1 = new float[h]; W2 = new float[NB * 2 * h]; B2 = new float[NB];
            }
            public float[][] All => new[] { W1, B1, W2, B2 };
        }

        sealed class Worker
        {
            public float[] gW1, gB1, gW2, gB2;
            public float[] accS, accN, dS, dN;
            public int[] fS = new int[32], fN = new int[32];
            public double Loss;
            public Random Rng = new Random(Guid.NewGuid().GetHashCode());
            public Worker(int h)
            {
                gW1 = new float[In * h]; gB1 = new float[h]; gW2 = new float[NB * 2 * h]; gB2 = new float[NB];
                accS = new float[h]; accN = new float[h]; dS = new float[h]; dN = new float[h];
            }
            public void Zero()
            {
                Array.Clear(gW1, 0, gW1.Length); Array.Clear(gB1, 0, gB1.Length);
                Array.Clear(gW2, 0, gW2.Length); Array.Clear(gB2, 0, gB2.Length);
                Loss = 0;
            }
        }

        // feature index for a piece seen from perspective `persp`
        public static int Feature(int persp, int pc, int sq)
        {
            int c = C.ColorOf(pc) == persp ? 0 : 1;
            int s = persp == C.White ? sq : sq ^ 56;
            return c * 384 + C.TypeOf(pc) * 64 + s;
        }

        public static int Bucket(int pieceCount) => Math.Min(NB - 1, Math.Max(0, (pieceCount - 2) / 4));

        static int Decode(in PackedPos p, int[] fS, int[] fN, out int stm, bool mirror = false)
        {
            stm = p.Stm;
            ulong b = p.Occ;
            int k = 0;
            int flip = mirror ? 7 : 0;
            while (b != 0)
            {
                int sq = BB.PopLsb(ref b) ^ flip;
                int pc = (int)((k < 16 ? p.Pc0 >> (4 * k) : p.Pc1 >> (4 * (k - 16))) & 15);
                fS[k] = Feature(stm, pc, sq);
                fN[k] = Feature(stm ^ 1, pc, sq);
                k++;
            }
            return k;
        }

        static bool Mirror;

        public static void Run(string[] tok)
        {
            var files = new List<string>();
            int epochs = 30, batch = 16384, threads = Math.Max(1, Environment.ProcessorCount - 2), h = 256;
            float lr0 = 0.001f, lambda = 0.3f;
            string outFile = "dolphin.nnue", resume = null;
            long maxPos = long.MaxValue;
            foreach (var t in tok)
            {
                int eq = t.IndexOf('=');
                if (eq < 0) continue;
                string k = t.Substring(0, eq), v = t.Substring(eq + 1);
                switch (k)
                {
                    case "data": files.AddRange(v.Split(',')); break;
                    case "epochs": epochs = int.Parse(v); break;
                    case "batch": batch = int.Parse(v); break;
                    case "threads": threads = int.Parse(v); break;
                    case "hidden": h = int.Parse(v); break;
                    case "lr": lr0 = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "lambda": lambda = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "out": outFile = v; break;
                    case "resume": resume = v; break;
                    case "max": maxPos = long.Parse(v); break;
                    case "mirror": Mirror = v == "1"; break;
                }
            }

            // ---------------- load data
            var sw = Stopwatch.StartNew();
            long total = 0;
            foreach (var f in files) total += new FileInfo(f).Length / PackedPos.Size;
            total = Math.Min(total, maxPos);
            var data = new PackedPos[total];
            long n = 0;
            foreach (var f in files)
            {
                using var br = new BinaryReader(new BufferedStream(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), 1 << 22));
                long cnt = new FileInfo(f).Length / PackedPos.Size;
                for (long i = 0; i < cnt && n < total; i++) data[n++] = PackedPos.Read(br);
            }
            Console.WriteLine($"loaded {n} positions in {sw.Elapsed.TotalSeconds:F1}s");
            // shuffle once; last 1% is validation
            var rng = new Random(12345);
            for (long i = n - 1; i > 0; i--)
            {
                long j = (long)(rng.NextDouble() * (i + 1));
                var tmp = data[i]; data[i] = data[j]; data[j] = tmp;
            }
            long nVal = Math.Min(200000, n / 100);
            long nTrain = n - nVal;

            // ---------------- init network
            var net = new Net(h);
            if (resume != null && File.Exists(resume)) { LoadFloat(net, resume); Console.WriteLine("resumed from " + resume); }
            else
            {
                var r = new Random(7);
                for (int i = 0; i < net.W1.Length; i++) net.W1[i] = (float)(Gauss(r) * 0.05);
                for (int i = 0; i < h; i++) net.B1[i] = 0.05f;
                for (int i = 0; i < net.W2.Length; i++) net.W2[i] = (float)(Gauss(r) * 0.05);
            }
            var arrays = net.All;
            var m = new float[arrays.Length][];
            var v2 = new float[arrays.Length][];
            for (int i = 0; i < arrays.Length; i++) { m[i] = new float[arrays[i].Length]; v2[i] = new float[arrays[i].Length]; }

            var workers = new Worker[threads];
            for (int i = 0; i < threads; i++) workers[i] = new Worker(h);
            long steps = 0;
            int stepsPerEpoch = (int)(nTrain / batch);
            long totalSteps = (long)stepsPerEpoch * epochs;
            var index = new int[nTrain];
            for (int i = 0; i < nTrain; i++) index[i] = i;

            Console.WriteLine($"train {nTrain} val {nVal} hidden {h} batch {batch} epochs {epochs} threads {threads} lr {lr0} lambda {lambda}");
            Console.WriteLine($"val loss (initial) {Validate(net, data, nTrain, nVal, lambda, workers):F6}");

            for (int ep = 1; ep <= epochs; ep++)
            {
                var esw = Stopwatch.StartNew();
                // shuffle index
                for (int i = index.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    int t = index[i]; index[i] = index[j]; index[j] = t;
                }
                double epochLoss = 0;
                for (int st = 0; st < stepsPerEpoch; st++)
                {
                    int start = st * batch;
                    Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, w =>
                    {
                        var wk = workers[w];
                        wk.Zero();
                        int per = (batch + threads - 1) / threads;
                        int s0 = start + w * per, s1 = Math.Min(start + batch, s0 + per);
                        for (int i = s0; i < s1; i++) TrainOne(net, wk, in data[index[i]], lambda);
                    });
                    double bl = 0;
                    foreach (var wk in workers) bl += wk.Loss;
                    epochLoss += bl / batch;

                    // reduce + AdamW
                    double progress = (double)steps / totalSteps;
                    float lr = (float)(lr0 * (0.02 + 0.98 * 0.5 * (1 + Math.Cos(Math.PI * progress))));
                    steps++;
                    Adam(net, workers, m, v2, lr, steps, batch, threads);
                }
                double val = Validate(net, data, nTrain, nVal, lambda, workers);
                Console.WriteLine($"epoch {ep}/{epochs} train {epochLoss / stepsPerEpoch:F6} val {val:F6} time {esw.Elapsed.TotalSeconds:F0}s");
                SaveFloat(net, outFile + ".f32");
                Export(net, outFile);
            }
            Console.WriteLine("saved " + outFile);

            // verification data: float evaluations of a few validation positions and accumulator headroom
            float maxAcc = 0;
            var wk0 = workers[0];
            for (long i = nTrain; i < n; i++)
            {
                ForwardVec(net, wk0, in data[i], out _, out _);
                for (int j = 0; j < h; j++) maxAcc = Math.Max(maxAcc, Math.Max(Math.Abs(wk0.accS[j]), Math.Abs(wk0.accN[j])));
            }
            Console.WriteLine($"max |accumulator| on validation set: {maxAcc * QA:F0} (int16 limit 32767)");
            for (long i = nTrain; i < Math.Min(n, nTrain + 6); i++)
            {
                float o = ForwardVec(net, wk0, in data[i], out int stm, out _);
                Console.WriteLine($"check {data[i].ToFen()} | float eval (stm) {o * EvalScale:F1}");
            }
        }

        static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

        static float ForwardVec(Net net, Worker wk, in PackedPos p, out int stm, out int b, bool mirror = false)
        {
            int h = net.H;
            int cnt = Decode(p, wk.fS, wk.fN, out stm, mirror);
            b = Bucket(cnt);
            var accS = wk.accS; var accN = wk.accN;
            Array.Copy(net.B1, accS, h);
            Array.Copy(net.B1, accN, h);
            var W1 = net.W1;
            var vS = MemoryMarshal.Cast<float, Vector<float>>(accS.AsSpan());
            var vN = MemoryMarshal.Cast<float, Vector<float>>(accN.AsSpan());
            for (int k = 0; k < cnt; k++)
            {
                var rowS = MemoryMarshal.Cast<float, Vector<float>>(W1.AsSpan(wk.fS[k] * h, h));
                var rowN = MemoryMarshal.Cast<float, Vector<float>>(W1.AsSpan(wk.fN[k] * h, h));
                for (int j = 0; j < vS.Length; j++) { vS[j] += rowS[j]; vN[j] += rowN[j]; }
            }
            var w2s = MemoryMarshal.Cast<float, Vector<float>>(net.W2.AsSpan(b * 2 * h, h));
            var w2n = MemoryMarshal.Cast<float, Vector<float>>(net.W2.AsSpan(b * 2 * h + h, h));
            var zero = Vector<float>.Zero; var one = Vector<float>.One;
            Vector<float> sum = Vector<float>.Zero;
            for (int j = 0; j < vS.Length; j++)
            {
                var a = Vector.Min(Vector.Max(vS[j], zero), one);
                var c = Vector.Min(Vector.Max(vN[j], zero), one);
                sum += a * a * w2s[j] + c * c * w2n[j];
            }
            return Vector.Dot(sum, one) + net.B2[b];
        }

        static void TrainOne(Net net, Worker wk, in PackedPos p, float lambda)
        {
            int h = net.H;
            bool mirror = Mirror && (wk.Rng.Next() & 1) != 0;
            float outv = ForwardVec(net, wk, in p, out int stm, out int b, mirror);
            float pred = Sigmoid(outv);
            int cnt = BB.PopCount(p.Occ);
            var vS = MemoryMarshal.Cast<float, Vector<float>>(wk.accS.AsSpan());
            var vN = MemoryMarshal.Cast<float, Vector<float>>(wk.accN.AsSpan());
            var w2s = MemoryMarshal.Cast<float, Vector<float>>(net.W2.AsSpan(b * 2 * h, h));
            var w2n = MemoryMarshal.Cast<float, Vector<float>>(net.W2.AsSpan(b * 2 * h + h, h));
            var zero = Vector<float>.Zero; var one = Vector<float>.One;

            float score = stm == C.White ? p.Score : -p.Score;
            float wdl = p.Result / 2f;
            if (stm == C.Black) wdl = 1f - wdl;
            float target = lambda * wdl + (1 - lambda) * Sigmoid(score / EvalScale);
            float err = pred - target;
            wk.Loss += err * err;
            float g = 2f * err * pred * (1 - pred);

            wk.gB2[b] += g;
            var gw2s = MemoryMarshal.Cast<float, Vector<float>>(wk.gW2.AsSpan(b * 2 * h, h));
            var gw2n = MemoryMarshal.Cast<float, Vector<float>>(wk.gW2.AsSpan(b * 2 * h + h, h));
            var dS = MemoryMarshal.Cast<float, Vector<float>>(wk.dS.AsSpan());
            var dN = MemoryMarshal.Cast<float, Vector<float>>(wk.dN.AsSpan());
            var gv = new Vector<float>(g);
            var two = new Vector<float>(2f);
            var gB1 = MemoryMarshal.Cast<float, Vector<float>>(wk.gB1.AsSpan());
            for (int j = 0; j < vS.Length; j++)
            {
                var xs = vS[j]; var xn = vN[j];
                var a = Vector.Min(Vector.Max(xs, zero), one);
                var c = Vector.Min(Vector.Max(xn, zero), one);
                gw2s[j] += gv * a * a;
                gw2n[j] += gv * c * c;
                var ms = Vector.BitwiseAnd(Vector.GreaterThan(xs, zero), Vector.LessThan(xs, one));
                var mn = Vector.BitwiseAnd(Vector.GreaterThan(xn, zero), Vector.LessThan(xn, one));
                var ds = Vector.ConditionalSelect(ms, gv * w2s[j] * two * xs, zero);
                var dn = Vector.ConditionalSelect(mn, gv * w2n[j] * two * xn, zero);
                dS[j] = ds; dN[j] = dn;
                gB1[j] += ds + dn;
            }
            var gW1 = wk.gW1;
            for (int k = 0; k < cnt; k++)
            {
                var rowS = MemoryMarshal.Cast<float, Vector<float>>(gW1.AsSpan(wk.fS[k] * h, h));
                for (int j = 0; j < rowS.Length; j++) rowS[j] += dS[j];
                var rowN = MemoryMarshal.Cast<float, Vector<float>>(gW1.AsSpan(wk.fN[k] * h, h));
                for (int j = 0; j < rowN.Length; j++) rowN[j] += dN[j];
            }
        }

        static double Validate(Net net, PackedPos[] data, long start, long count, float lambda, Worker[] workers)
        {
            int threads = workers.Length;
            double[] loss = new double[threads];
            Parallel.For(0, threads, w =>
            {
                var wk = workers[w];
                long per = (count + threads - 1) / threads;
                long s0 = start + w * per, s1 = Math.Min(start + count, s0 + per);
                double l = 0;
                for (long i = s0; i < s1; i++)
                {
                    float pred = Sigmoid(ForwardVec(net, wk, in data[i], out int stm, out _));
                    var p = data[i];
                    float score = stm == C.White ? p.Score : -p.Score;
                    float wdl = p.Result / 2f;
                    if (stm == C.Black) wdl = 1f - wdl;
                    float target = lambda * wdl + (1 - lambda) * Sigmoid(score / EvalScale);
                    l += (pred - target) * (pred - target);
                }
                loss[w] = l;
            });
            double sum = 0;
            foreach (var x in loss) sum += x;
            return sum / Math.Max(1, count);
        }

        static void Adam(Net net, Worker[] workers, float[][] m, float[][] v, float lr, long step, int batch, int threads)
        {
            const float b1 = 0.9f, b2 = 0.999f, eps = 1e-8f, wd = 0.0f;
            float c1 = 1f - MathF.Pow(b1, step), c2 = 1f - MathF.Pow(b2, step);
            float inv = 1f / batch;
            var p = net.All;
            for (int a = 0; a < p.Length; a++)
            {
                float[] param = p[a], ma = m[a], va = v[a];
                int len = param.Length;
                float clip = a == 2 ? 8f : a == 3 ? 1e9f : 64f;
                var srcs = new float[workers.Length][];
                for (int w = 0; w < workers.Length; w++)
                    srcs[w] = a == 0 ? workers[w].gW1 : a == 1 ? workers[w].gB1 : a == 2 ? workers[w].gW2 : workers[w].gB2;
                int chunks = Math.Min(threads, Math.Max(1, len / 4096));
                Parallel.For(0, chunks, ci =>
                {
                    int per = (len + chunks - 1) / chunks;
                    int s0 = ci * per, s1 = Math.Min(len, s0 + per);
                    for (int i = s0; i < s1; i++)
                    {
                        float g = 0;
                        for (int w = 0; w < srcs.Length; w++) g += srcs[w][i];
                        g *= inv;
                        if (g == 0 && a == 0 && ma[i] == 0) continue; // untouched sparse row
                        ma[i] = b1 * ma[i] + (1 - b1) * g;
                        va[i] = b2 * va[i] + (1 - b2) * g * g;
                        float upd = lr * (ma[i] / c1) / (MathF.Sqrt(va[i] / c2) + eps);
                        float x = param[i] - upd - lr * wd * param[i];
                        param[i] = Math.Clamp(x, -clip, clip);
                    }
                });
            }
        }

        // ---------------- persistence
        static void SaveFloat(Net net, string path)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(net.H);
            foreach (var a in net.All) { bw.Write(a.Length); foreach (var x in a) bw.Write(x); }
        }

        static void LoadFloat(Net net, string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            int h = br.ReadInt32();
            if (h != net.H) throw new Exception("hidden size mismatch");
            foreach (var a in net.All)
            {
                int len = br.ReadInt32();
                for (int i = 0; i < len; i++) a[i] = br.ReadSingle();
            }
        }

        public const int QA = 255, QB = 64;

        public static void ExportFromFloat(string f32, string path)
        {
            using var br = new BinaryReader(File.OpenRead(f32));
            var net = new Net(br.ReadInt32());
            br.BaseStream.Position = 0;
            br.Dispose();
            LoadFloat(net, f32);
            Export(net, path);
        }

        static void Export(Net net, string path)
        {
            using var bw = new BinaryWriter(File.Create(path));
            bw.Write(0x4E4E4C44); // "DLNN"
            bw.Write(2);          // version 2: float output layer
            bw.Write(net.H);
            bw.Write(NB);
            foreach (var x in net.W1) bw.Write((short)Math.Round(Math.Clamp(x * QA, -32767, 32767)));
            foreach (var x in net.B1) bw.Write((short)Math.Round(Math.Clamp(x * QA, -32767, 32767)));
            foreach (var x in net.W2) bw.Write(x);
            foreach (var x in net.B2) bw.Write(x);
        }
    }
}
