namespace FSO.TAALab
{
    /// <summary>
    /// Per-eval score bundle from the auto-tuner's 240-frame scripted sequence: the weighted total the
    /// optimizer minimizes, plus per-phase subscores (rest/motion/reveal/slow — each spatial + weighted
    /// temporal, averaged over that phase's 60 frames) so results stay diagnosable, and the raw
    /// spatial/temporal means for reference.
    /// </summary>
    public struct EvalScores
    {
        public double Total;
        public double Rest, Motion, Reveal, Slow;
        public double SpatialMean, TemporalMean;
        // artifact-class terms (2026-07-10 strict metric): sustained same-signed error (ghost/trail
        // persistence), change-where-truth-is-static (fizzle/noise), and bad-pixel fractions (strict
        // worst-case floor — a badly wrong region cannot hide in the mean).
        public double GhostMean, FizzleMean, StrictMean;
    }

    /// <summary>
    /// Ask/tell optimizer contract shared by the lab's search algorithms (Nelder-Mead, CMA-ES).
    /// The driver strictly alternates Ask()/Tell(score); Ask() returns null when the algorithm has
    /// converged on its own terms (the caller applies its eval budget on top). Implementations must
    /// be deterministic: identical score sequences produce identical candidate sequences.
    /// </summary>
    public interface IOptimizer
    {
        /// <summary>Next candidate to evaluate (a private copy), or null when converged.</summary>
        float[] Ask();
        /// <summary>Report the score of the last Ask()ed candidate.</summary>
        void Tell(double score);
        double BestScore { get; }
        float[] BestPoint { get; }
        int Evals { get; }
        bool Finished { get; }
    }

    /// <summary>
    /// Bounded Nelder-Mead in ask/tell form, dependency-free. The classic simplex method (reflect 1,
    /// expand 2, contract 0.5, shrink 0.5) driven as a coroutine so the caller can evaluate each asked
    /// candidate across many Draw calls (the lab's GPU evals must run on the render thread):
    ///
    ///     var opt = new NelderMeadOptimizer(start, lo, hi);
    ///     for (var x = opt.Ask(); x != null; x = opt.Ask()) { ... evaluate x ... opt.Tell(score); }
    ///
    /// Candidates are kept in bounds by reflecting at the walls then clamping (deep violations).
    /// Fully deterministic: no randomness, and ties in the sort resolve the same way for identical
    /// score sequences. Ask() returns null once the simplex has collapsed (relative f-spread ~ 0);
    /// the caller applies its own max-eval budget on top.
    /// </summary>
    public sealed class NelderMeadOptimizer : IOptimizer
    {
        private readonly float[] Lo, Hi;
        private readonly IEnumerator<float[]> Steps;
        private double LastScore;
        private bool AwaitingTell;
        private float[] LastAsked;

        public double BestScore { get; private set; } = double.MaxValue;
        public float[] BestPoint { get; private set; }
        public int Evals { get; private set; }
        public bool Finished { get; private set; }

        public NelderMeadOptimizer(float[] start, float[] lo, float[] hi, float initStepFrac = 0.08f)
        {
            Lo = lo; Hi = hi;
            Steps = Iterate((float[])start.Clone(), initStepFrac).GetEnumerator();
        }

        /// <summary>Next candidate to evaluate (a private copy), or null when converged.</summary>
        public float[] Ask()
        {
            if (AwaitingTell) throw new InvalidOperationException("Tell() the previous candidate's score first.");
            if (Finished || !Steps.MoveNext()) { Finished = true; return null; }
            AwaitingTell = true;
            LastAsked = Steps.Current;
            return (float[])Steps.Current.Clone();
        }

        /// <summary>Report the score of the last Ask()ed candidate.</summary>
        public void Tell(double score)
        {
            if (!AwaitingTell) throw new InvalidOperationException("Ask() a candidate first.");
            AwaitingTell = false;
            LastScore = score;
            Evals++;
            if (score < BestScore)
            {
                BestScore = score;
                BestPoint = (float[])LastAsked.Clone();
            }
        }

        /// <summary>Reflect at the bounds, then clamp (handles violations deeper than one range-width).</summary>
        private float[] Sanitize(float[] p)
        {
            for (int i = 0; i < p.Length; i++)
            {
                float lo = Lo[i], hi = Hi[i], v = p[i];
                if (v > hi) v = hi - (v - hi);
                if (v < lo) v = lo + (lo - v);
                p[i] = Math.Clamp(v, lo, hi);
            }
            return p;
        }

        private IEnumerable<float[]> Iterate(float[] x0, float frac)
        {
            int n = x0.Length;
            var pts = new float[n + 1][];
            var f = new double[n + 1];

            // Initial simplex: the start point + one vertex per axis, stepped by frac of the range
            // (stepping inward when the default sits against its upper bound).
            pts[0] = Sanitize(x0);
            yield return pts[0]; f[0] = LastScore;
            for (int i = 0; i < n; i++)
            {
                var p = (float[])pts[0].Clone();
                float step = (Hi[i] - Lo[i]) * frac;
                p[i] = p[i] + step <= Hi[i] ? p[i] + step : p[i] - step;
                pts[i + 1] = Sanitize(p);
                yield return pts[i + 1]; f[i + 1] = LastScore;
            }

            while (true)
            {
                // order ascending by score
                var order = new int[n + 1];
                for (int i = 0; i <= n; i++) order[i] = i;
                var keys = (double[])f.Clone();
                Array.Sort(keys, order);
                var sp = new float[n + 1][];
                var sf = new double[n + 1];
                for (int i = 0; i <= n; i++) { sp[i] = pts[order[i]]; sf[i] = f[order[i]]; }
                pts = sp; f = sf;

                // converged: the simplex is flat to machine precision
                if (f[n] - f[0] <= 1e-12 * (Math.Abs(f[0]) + 1e-12)) yield break;

                // centroid of all but the worst
                var cen = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double s = 0;
                    for (int j = 0; j < n; j++) s += pts[j][i];
                    cen[i] = (float)(s / n);
                }
                float[] Toward(float w)
                {
                    var p = new float[n];
                    for (int i = 0; i < n; i++) p[i] = cen[i] + w * (cen[i] - pts[n][i]);
                    return Sanitize(p);
                }

                var xr = Toward(1f);                       // reflect
                yield return xr; double fr = LastScore;
                if (fr < f[0])
                {
                    var xe = Toward(2f);                   // expand
                    yield return xe; double fe = LastScore;
                    if (fe < fr) { pts[n] = xe; f[n] = fe; }
                    else { pts[n] = xr; f[n] = fr; }
                }
                else if (fr < f[n - 1])
                {
                    pts[n] = xr; f[n] = fr;                // accept reflection
                }
                else
                {
                    bool outside = fr < f[n];
                    var xc = Toward(outside ? 0.5f : -0.5f); // outside / inside contraction
                    yield return xc; double fc = LastScore;
                    if ((outside && fc <= fr) || (!outside && fc < f[n]))
                    {
                        pts[n] = xc; f[n] = fc;
                    }
                    else
                    {
                        for (int i = 1; i <= n; i++)       // shrink toward the best vertex
                        {
                            var p = new float[n];
                            for (int k = 0; k < n; k++) p[k] = pts[0][k] + 0.5f * (pts[i][k] - pts[0][k]);
                            pts[i] = Sanitize(p);
                            yield return pts[i]; f[i] = LastScore;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// (mu/mu_w, lambda)-CMA-ES in ask/tell form, dependency-free (Hansen's standard constants,
    /// positive recombination weights only). Operates in per-axis NORMALIZED coordinates
    /// (x_norm = (x - lo) / (hi - lo)) so one isotropic sigma0 covers parameters with wildly
    /// different ranges (0.099 for DeepCapBase vs 63 for ConfFadeN).
    ///
    /// Bounds: sampled points are repaired by clamping into [0,1]^n; the covariance/mean update uses
    /// the REPAIRED step (keeps the mean feasible — it becomes a convex combination of feasible
    /// points), and the told score is penalized multiplicatively by the squared repair distance
    /// (scale-free, since lab scores are non-negative MSE-like totals).
    ///
    /// Deterministic: fixed-seed System.Random + Box-Muller, and the per-generation sort breaks
    /// score ties by candidate index. The eigendecomposition (cyclic Jacobi) runs once per
    /// generation — trivial next to 240-frame GPU evals at n &lt;= 22.
    /// </summary>
    public sealed class CmaEsOptimizer : IOptimizer
    {
        private readonly int N;
        private readonly float[] Lo, Hi;
        private readonly Random Rng;

        private readonly double[] Mean;         // normalized [0,1] coords
        private double Sigma;
        private readonly double[,] C, B;        // covariance, eigenbasis (columns)
        private readonly double[] D;            // sqrt eigenvalues
        private readonly double[] Pc, Ps;
        private int Gen;

        private readonly int Lambda, Mu;
        private readonly double[] W;
        private readonly double MuEff, Cc, Cs, C1, CMu, Damps, ChiN;

        // per-generation buffers (Ask fills slot AskIdx, Tell scores it)
        private readonly double[][] GenY;       // repaired steps (x_norm - mean) / sigma
        private readonly float[][] GenX;        // denormalized candidates as handed out
        private readonly double[] GenFit;       // penalized fitness for the update
        private int AskIdx;
        private bool AwaitingTell;
        private bool HaveGauss; private double GaussSpare;

        public double BestScore { get; private set; } = double.MaxValue;
        public float[] BestPoint { get; private set; }
        public int Evals { get; private set; }
        public bool Finished { get; private set; }

        public CmaEsOptimizer(float[] start, float[] lo, float[] hi, int seed, double sigma0 = 0.15, int lambda = 0)
        {
            N = start.Length;
            Lo = lo; Hi = hi;
            Rng = new Random(seed);
            Sigma = sigma0;

            Mean = new double[N];
            for (int i = 0; i < N; i++) Mean[i] = Math.Clamp((start[i] - lo[i]) / Range(i), 0.0, 1.0);

            Lambda = lambda > 0 ? lambda : 4 + (int)(3.0 * Math.Log(N));
            Mu = Lambda / 2;
            W = new double[Mu];
            double wsum = 0;
            for (int i = 0; i < Mu; i++) { W[i] = Math.Log(Mu + 0.5) - Math.Log(i + 1); wsum += W[i]; }
            double w2 = 0;
            for (int i = 0; i < Mu; i++) { W[i] /= wsum; w2 += W[i] * W[i]; }
            MuEff = 1.0 / w2;

            Cc = (4.0 + MuEff / N) / (N + 4.0 + 2.0 * MuEff / N);
            Cs = (MuEff + 2.0) / (N + MuEff + 5.0);
            C1 = 2.0 / ((N + 1.3) * (N + 1.3) + MuEff);
            CMu = Math.Min(1.0 - C1, 2.0 * (MuEff - 2.0 + 1.0 / MuEff) / ((N + 2.0) * (N + 2.0) + MuEff));
            Damps = 1.0 + 2.0 * Math.Max(0.0, Math.Sqrt((MuEff - 1.0) / (N + 1.0)) - 1.0) + Cs;
            ChiN = Math.Sqrt(N) * (1.0 - 1.0 / (4.0 * N) + 1.0 / (21.0 * N * N));

            C = new double[N, N]; B = new double[N, N]; D = new double[N];
            for (int i = 0; i < N; i++) { C[i, i] = 1.0; B[i, i] = 1.0; D[i] = 1.0; }
            Pc = new double[N]; Ps = new double[N];

            GenY = new double[Lambda][]; GenX = new float[Lambda][]; GenFit = new double[Lambda];
        }

        private double Range(int i) => Math.Max(1e-12, Hi[i] - Lo[i]);

        private double Gauss()
        {
            if (HaveGauss) { HaveGauss = false; return GaussSpare; }
            double u1;
            do { u1 = Rng.NextDouble(); } while (u1 <= 1e-300);
            double u2 = Rng.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1)), th = 2.0 * Math.PI * u2;
            GaussSpare = r * Math.Sin(th); HaveGauss = true;
            return r * Math.Cos(th);
        }

        public float[] Ask()
        {
            if (AwaitingTell) throw new InvalidOperationException("Tell() the previous candidate's score first.");
            if (Finished) return null;

            // sample: y = B * (D o z), x = mean + sigma*y, repair into [0,1], recompute the repaired step
            var z = new double[N];
            for (int i = 0; i < N; i++) z[i] = Gauss();
            var y = new double[N];
            for (int i = 0; i < N; i++)
            {
                double s = 0;
                for (int j = 0; j < N; j++) s += B[i, j] * (D[j] * z[j]);
                y[i] = s;
            }
            var yRep = new double[N];
            var x = new float[N];
            double pen = 0;
            for (int i = 0; i < N; i++)
            {
                double xn = Mean[i] + Sigma * y[i];
                double r = Math.Clamp(xn, 0.0, 1.0);
                double d = r - xn;
                pen += d * d;
                yRep[i] = (r - Mean[i]) / Sigma;
                x[i] = (float)Math.Clamp(Lo[i] + r * Range(i), Lo[i], Hi[i]);
            }
            GenY[AskIdx] = yRep;
            GenX[AskIdx] = x;
            GenFit[AskIdx] = pen; // stashed; Tell folds the raw score in
            AwaitingTell = true;
            return (float[])x.Clone();
        }

        public void Tell(double score)
        {
            if (!AwaitingTell) throw new InvalidOperationException("Ask() a candidate first.");
            AwaitingTell = false;
            Evals++;
            if (score < BestScore)
            {
                BestScore = score;
                BestPoint = (float[])GenX[AskIdx].Clone();
            }
            double pen = GenFit[AskIdx];
            // scale-free bound penalty: lab totals are non-negative, so multiplicative works; the
            // additive term keeps infeasibility ordered even at score == 0.
            GenFit[AskIdx] = score * (1.0 + 10.0 * pen) + pen * 1e-9;
            if (++AskIdx == Lambda)
            {
                UpdateGeneration();
                AskIdx = 0;
            }
        }

        private void UpdateGeneration()
        {
            // rank ascending by fitness, ties broken by candidate index (Array.Sort is not stable)
            var order = new int[Lambda];
            for (int i = 0; i < Lambda; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = GenFit[a].CompareTo(GenFit[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var yw = new double[N];
            for (int k = 0; k < Mu; k++)
            {
                var y = GenY[order[k]];
                for (int i = 0; i < N; i++) yw[i] += W[k] * y[i];
            }
            for (int i = 0; i < N; i++) Mean[i] = Math.Clamp(Mean[i] + Sigma * yw[i], 0.0, 1.0);

            // ps update needs C^-1/2 * yw = B diag(1/D) B^T yw
            var tmp = new double[N];
            for (int j = 0; j < N; j++)
            {
                double s = 0;
                for (int i = 0; i < N; i++) s += B[i, j] * yw[i];
                tmp[j] = s / D[j];
            }
            var csn = new double[N];
            for (int i = 0; i < N; i++)
            {
                double s = 0;
                for (int j = 0; j < N; j++) s += B[i, j] * tmp[j];
                csn[i] = s;
            }
            double csFac = Math.Sqrt(Cs * (2.0 - Cs) * MuEff);
            double psNorm2 = 0;
            for (int i = 0; i < N; i++)
            {
                Ps[i] = (1.0 - Cs) * Ps[i] + csFac * csn[i];
                psNorm2 += Ps[i] * Ps[i];
            }
            double psNorm = Math.Sqrt(psNorm2);
            Gen++;
            bool hsig = psNorm / Math.Sqrt(1.0 - Math.Pow(1.0 - Cs, 2.0 * Gen)) / ChiN < 1.4 + 2.0 / (N + 1.0);

            double ccFac = Math.Sqrt(Cc * (2.0 - Cc) * MuEff);
            for (int i = 0; i < N; i++) Pc[i] = (1.0 - Cc) * Pc[i] + (hsig ? ccFac * yw[i] : 0.0);

            // C <- (1-c1-cmu)C + c1(pc pc^T + delta(hsig) C) + cmu SUM w_k y_k y_k^T   (Hansen tutorial)
            double delta = hsig ? 0.0 : Cc * (2.0 - Cc);
            double keep = 1.0 - C1 - CMu + C1 * delta;
            for (int i = 0; i < N; i++)
            {
                for (int j = i; j < N; j++)
                {
                    double v = keep * C[i, j] + C1 * Pc[i] * Pc[j];
                    for (int k = 0; k < Mu; k++)
                    {
                        var y = GenY[order[k]];
                        v += CMu * W[k] * y[i] * y[j];
                    }
                    C[i, j] = v; C[j, i] = v;
                }
            }

            Sigma *= Math.Exp((Cs / Damps) * (psNorm / ChiN - 1.0));
            Sigma = Math.Clamp(Sigma, 1e-12, 1.0);

            Eigen(C, B, D, N);
            double maxD = 0;
            for (int i = 0; i < N; i++) maxD = Math.Max(maxD, D[i]);
            if (Sigma * maxD < 1e-7) Finished = true; // converged in normalized space
        }

        /// <summary>
        /// Cyclic Jacobi eigendecomposition of symmetric A: V's COLUMNS become the eigenvectors,
        /// d the sqrt of the (floored) eigenvalues. Deterministic fixed sweep order.
        /// </summary>
        private static void Eigen(double[,] A, double[,] V, double[] d, int n)
        {
            var M = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    M[i, j] = A[i, j];
                    V[i, j] = i == j ? 1.0 : 0.0;
                }
            for (int sweep = 0; sweep < 60; sweep++)
            {
                double off = 0;
                for (int p = 0; p < n - 1; p++)
                    for (int q = p + 1; q < n; q++) off += M[p, q] * M[p, q];
                if (off < 1e-24) break;
                for (int p = 0; p < n - 1; p++)
                {
                    for (int q = p + 1; q < n; q++)
                    {
                        if (Math.Abs(M[p, q]) < 1e-18) continue;
                        double theta = (M[q, q] - M[p, p]) / (2.0 * M[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        if (theta == 0) t = 1.0;
                        double c = 1.0 / Math.Sqrt(t * t + 1.0), s = t * c;
                        for (int k = 0; k < n; k++)
                        {
                            double mkp = M[k, p], mkq = M[k, q];
                            M[k, p] = c * mkp - s * mkq;
                            M[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double mpk = M[p, k], mqk = M[q, k];
                            M[p, k] = c * mpk - s * mqk;
                            M[q, k] = s * mpk + c * mqk;
                            double vkp = V[k, p], vkq = V[k, q];
                            V[k, p] = c * vkp - s * vkq;
                            V[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            for (int i = 0; i < n; i++) d[i] = Math.Sqrt(Math.Max(M[i, i], 1e-14));
        }
    }
}
