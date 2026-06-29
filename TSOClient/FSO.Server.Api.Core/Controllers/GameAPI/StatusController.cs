using FSO.Server.Api.Core.Utils;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FSO.Server.Api.Core.Controllers.GameAPI
{
    /// <summary>
    /// Public, no-auth server status for launchers / community dashboards. One call returns server time, the
    /// advertised game version, total players + lots online, per-shard status, and the busiest lots. Built
    /// from the same data the game uses (shard list + lot claims) and cached briefly so many pollers don't
    /// hammer the DB. CORS-enabled like the other userapi endpoints.
    /// </summary>
    [EnableCors]
    [Route("userapi/status")]
    [ApiController]
    public class StatusController : ControllerBase
    {
        private static readonly object Lock = new object();
        private static ServerStatusModel Cached;
        private static long CachedAtUnix;

        [HttpGet]
        public IActionResult Get()
        {
            var api = Api.INSTANCE;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (Lock)
            {
                if (Cached != null && now - CachedAtUnix < 10)
                {
                    Cached.serverTime = DateTime.UtcNow; // keep the clock fresh even on a cache hit
                    return ApiResponse.Json(HttpStatusCode.OK, Cached);
                }
            }

            var shards = api.Shards.All.ToList();
            var shardSummaries = new List<ShardSummary>();
            var topLots = new List<TopLot>();
            int totalPlayers = 0, totalLots = 0;

            using (var da = api.DAFactory.Get())
            {
                foreach (var shard in shards)
                {
                    var claims = da.LotClaims.AllLocations(shard.Id); // active (spun-up) lots: location + active
                    var lots = da.Lots.AllLocations(shard.Id);        // all owned lots: location + name
                    var nameByLoc = new Dictionary<uint, string>();
                    foreach (var l in lots) nameByLoc[l.location] = l.name;

                    int players = claims.Sum(c => c.active);
                    int lotsOnline = claims.Count;
                    totalPlayers += players;
                    totalLots += lotsOnline;

                    shardSummaries.Add(new ShardSummary
                    {
                        id = shard.Id,
                        name = shard.Name,
                        status = shard.Status.ToString(),
                        version = shard.VersionName,
                        playersOnline = players,
                        lotsOnline = lotsOnline,
                        ownedLots = lots.Count
                    });

                    foreach (var c in claims)
                    {
                        if (c.active <= 0) continue;
                        nameByLoc.TryGetValue(c.location, out var nm);
                        topLots.Add(new TopLot { shardId = shard.Id, name = nm ?? "", location = c.location, players = c.active });
                    }
                }
            }

            var model = new ServerStatusModel
            {
                serverTime = DateTime.UtcNow,
                gameVersion = shards.FirstOrDefault()?.VersionName,
                playersOnline = totalPlayers,
                lotsOnline = totalLots,
                shards = shardSummaries.ToArray(),
                topLots = topLots.OrderByDescending(l => l.players).Take(5).ToArray()
            };

            lock (Lock)
            {
                Cached = model;
                CachedAtUnix = now;
            }
            return ApiResponse.Json(HttpStatusCode.OK, model);
        }
    }

    public class ServerStatusModel
    {
        public DateTime serverTime;
        public string gameVersion;
        public int playersOnline;
        public int lotsOnline;
        public ShardSummary[] shards;
        public TopLot[] topLots;
    }

    public class ShardSummary
    {
        public int id;
        public string name;
        public string status;
        public string version;
        public int playersOnline;
        public int lotsOnline;
        public int ownedLots;
    }

    public class TopLot
    {
        public int shardId;
        public string name;
        public uint location;
        public int players;
    }
}
