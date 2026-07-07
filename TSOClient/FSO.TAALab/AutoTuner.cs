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
    public sealed class NelderMeadOptimizer
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
}
