using FSO.Server.Api.Core.Utils;
using FSO.Server.Database.DA.DynPayouts;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FSO.Server.Api.Core.Controllers.GameAPI
{
    /// <summary>
    /// Public, no-auth daily money-object payout rates — the same numbers the in-game newspaper reports,
    /// exposed so launchers and community dashboards can show "what pays well today".
    ///
    /// The rates come from <c>fso_dyn_payouts</c>, which the nightly JobBalanceTask rewrites: it reads the
    /// last few days of money flow per object type and rebalances each type's multiplier so under-used
    /// objects pay more, then randomly picks one type for an extra boost (that row gets <c>flags = 1</c>).
    /// A multiplier of 1.0 is the baseline rate; 1.5 means that object currently pays 150%.
    ///
    /// Nothing here is per-player: it is global tuning, identical for everyone, so it needs no auth. Player
    /// balances (fso_avatars.budget) are deliberately NOT exposed by this or any other endpoint.
    /// </summary>
    [EnableCors]
    [Route("userapi/payouts")]
    [ApiController]
    public class PayoutsController : ControllerBase
    {
        private static readonly object Lock = new object();
        private static PayoutsModel Cached;
        private static long CachedAtUnix;

        /// <summary>The rates only change once a night, so a long cache is safe and keeps pollers off the DB.</summary>
        private const int CacheSeconds = 300;

        [HttpGet]
        public IActionResult Get()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (Lock)
            {
                if (Cached != null && now - CachedAtUnix < CacheSeconds)
                    return ApiResponse.Json(HttpStatusCode.OK, Cached);
            }

            List<DbDynPayout> history;
            using (var da = Api.INSTANCE.DAFactory.Get())
            {
                // Returns the most recent rows across all skill types (newest day first). We only need the
                // latest day, plus the one before it to report each rate's movement.
                history = da.DynPayouts.GetPayoutHistory(0);
            }

            var model = PayoutProjection.Build(history);

            lock (Lock)
            {
                Cached = model;
                CachedAtUnix = now;
            }
            return ApiResponse.Json(HttpStatusCode.OK, model);
        }
    }

    /// <summary>
    /// Turns raw <c>fso_dyn_payouts</c> rows into the API response. Deliberately a plain static class with
    /// no ASP.NET dependency, so the projection can be exercised without a database or a web host.
    /// </summary>
    public static class PayoutProjection
    {
        /// <summary>
        /// skilltype index -> (stable key, display name). The indices are assigned by JobBalanceTask's own
        /// TransactionToType map (see its comments: 0 typewriter, 1 easel, 2 boards, 3 jams, 4 potions,
        /// 5 gnome, 6 pinata, 7 telemarketing) — keep these in lockstep with it.
        /// </summary>
        private static readonly (string Key, string Name)[] SkillTypes =
        {
            ("typewriter",    "Typewriter"),
            ("easel",         "Easel"),
            ("boards",        "Boards"),
            ("jam",           "Jam Stand"),
            ("potion",        "Potion Table"),
            ("gnome",         "Gnome"),
            ("pinata",        "Pinata"),
            ("telemarketing", "Telemarketing"),
        };

        /// <summary>
        /// Projects raw payout rows into the response. <paramref name="history"/> may hold any number of
        /// days in any order; the newest day present becomes the current rates and the next-newest supplies
        /// the comparison.
        /// </summary>
        public static PayoutsModel Build(List<DbDynPayout> history)
        {
            if (history == null || history.Count == 0)
                // The nightly task has never run (fresh server) — say so plainly rather than inventing 1.0s.
                return new PayoutsModel { day = 0, skills = Array.Empty<PayoutSkill>(), bonusSkill = null };

            var days = history.Select(h => h.day).Distinct().OrderByDescending(d => d).ToList();
            var today = days[0];
            var yesterday = days.Count > 1 ? (int?)days[1] : null;

            var current = history.Where(h => h.day == today).ToDictionary(h => h.skilltype, h => h);
            var previous = yesterday == null
                ? new Dictionary<int, DbDynPayout>()
                : history.Where(h => h.day == yesterday).GroupBy(h => h.skilltype)
                         .ToDictionary(g => g.Key, g => g.First());

            var skills = new List<PayoutSkill>();
            string bonus = null;
            for (int i = 0; i < SkillTypes.Length; i++)
            {
                // A skill type missing from the newest day has no rate to report — skip rather than
                // publishing a fabricated baseline.
                if (!current.TryGetValue(i, out var row)) continue;

                var (key, name) = SkillTypes[i];
                double? variation = null;
                if (previous.TryGetValue(i, out var prev) && Math.Abs(prev.multiplier) > 0.0001f)
                    variation = Math.Round((row.multiplier - prev.multiplier) / (double)prev.multiplier * 100.0, 1);

                bool isBonus = (row.flags & 1) != 0;
                if (isBonus) bonus = name;

                skills.Add(new PayoutSkill
                {
                    key = key,
                    name = name,
                    multiplier = Math.Round(row.multiplier, 6),
                    percent = (int)Math.Round(row.multiplier * 100),
                    variation = variation,
                    variationUp = variation > 0,
                    isBonus = isBonus
                });
            }

            return new PayoutsModel
            {
                day = today,
                // fso_dyn_payouts.day is a day count since the unix epoch, not a date — convert so clients
                // don't have to know that.
                date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(today),
                skills = skills.ToArray(),
                bonusSkill = bonus
            };
        }
    }

    public class PayoutsModel
    {
        /// <summary>Day the rates were generated, counted in days since the unix epoch (the raw DB value).</summary>
        public int day;
        /// <summary>The same day as a UTC date, for clients that would rather not do the arithmetic.</summary>
        public DateTime? date;
        public PayoutSkill[] skills;
        /// <summary>Display name of the object picked for tonight's extra boost, or null if none was.</summary>
        public string bonusSkill;
    }

    public class PayoutSkill
    {
        /// <summary>Stable machine-readable id (e.g. "easel"). Safe to key UI off; never localized.</summary>
        public string key;
        public string name;
        /// <summary>Raw payout multiplier. 1.0 is the baseline rate.</summary>
        public double multiplier;
        /// <summary>The multiplier as a whole-number percentage (1.5 -> 150).</summary>
        public int percent;
        /// <summary>Percent change against the previous day's rate, or null when there's no prior day.</summary>
        public double? variation;
        public bool variationUp;
        /// <summary>True for the object the nightly task singled out for an extra boost.</summary>
        public bool isBonus;
    }
}
