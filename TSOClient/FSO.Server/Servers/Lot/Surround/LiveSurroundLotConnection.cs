using FSO.Common.Model;
using FSO.Server.Protocol.Electron.Packets;
using FSO.Server.Servers.Lot.Domain;
using FSO.SimAntics;

namespace FSO.Server.Servers.Lot.Surround
{
    internal class LiveSurroundLotConnection : IDisposable
    {
        private const int QUEUE_LENGTH_MAX = 4;
        private const int QUEUE_LENGTH_RESET = 2;

        private readonly LiveSurroundHost Host;
        private readonly ILotHost LotHost;
        public readonly uint LotLocation;
        private readonly Dictionary<uint, SurroundPuppet> PuppetData = [];
        private readonly HashSet<uint> ExpectedAvatars = [];

        private Lock QueueLock = new();
        private Queue<SurroundPuppetLotTick> TickQueue = [];
        private SurroundPuppetLotTick? LastTick;

        private uint TickID;
        private int AdjacencyCount;

        private Lock PlayersLock = new();
        private HashSet<uint> NewPlayers = [];
        private HashSet<uint> Players = [];

        private HashSet<uint> PlayersCopy = [];
        private HashSet<uint> NewPlayersCopy = [];

        public LiveSurroundLotConnection(LiveSurroundHost host, ILotHost lotHost, uint lotLocation)
        {
            Host = host;
            LotHost = lotHost;
            LotLocation = lotLocation;
        }

        public void SubmitTick(VM vm, bool force, uint tickDelta = 1)
        {
            if (AdjacencyCount <= 0 && !force)
            {
                return;
            }

            TickID += tickDelta;

            ExpectedAvatars.Clear();
            ExpectedAvatars.UnionWith(PuppetData.Keys);

            foreach (var ava in vm.Context.ObjectQueries.AvatarsByPersist)
            {
                var puppet = ava.Value.GetSurroundPuppet();

                if (PuppetData.TryGetValue(puppet.PersistID, out var existing))
                {
                    puppet.CalculateDelta(in existing);
                }

                PuppetData[puppet.PersistID] = puppet;
            }

            foreach (var id in ExpectedAvatars)
            {
                // These avatars weren't updated, so they must be deleted.
                PuppetData.Remove(id);
            }

            var tick = new SurroundPuppetLotTick()
            {
                TickID = TickID,
                Puppets = PuppetData.Values.ToArray()
            };

            QueueTick(tick);
        }

        private void QueueTick(SurroundPuppetLotTick tick)
        {
            lock (QueueLock)
            {
                TickQueue.Enqueue(tick);
                tick.Outdated = true;
                LastTick = tick;
                if (TickQueue.Count > QUEUE_LENGTH_MAX)
                {
                    while (TickQueue.Count > QUEUE_LENGTH_RESET)
                    {
                        TickQueue.Dequeue();
                    }
                }
            }
        }

        public SurroundPuppetLotTick? PullTick()
        {
            lock (QueueLock)
            {
                if (TickQueue.TryDequeue(out var tick))
                {
                    return tick;
                }

                if (LastTick.HasValue)
                {
                    return LastTick.Value;
                }
            }

            return null;
        }

        public void NotifyAdjacent()
        {
            Interlocked.Increment(ref AdjacencyCount);
        }

        public void NotifyAdjacentDecrement()
        {
            Interlocked.Decrement(ref AdjacencyCount);
        }

        public void Broadcast(FSOVMSurroundPuppets broadcast)
        {
            // Note: this is called from the LiveSurroundHost thread.

            lock (PlayersLock)
            {
                PlayersCopy.Clear();
                PlayersCopy.UnionWith(Players);

                NewPlayersCopy.Clear();
                if (NewPlayers.Count > 0)
                {
                    NewPlayersCopy.UnionWith(NewPlayers);

                    Players.UnionWith(NewPlayers);
                    NewPlayers.Clear();
                }
            }

            // Broadcast version with deltas for most people
            if (PlayersCopy.Count > 0)
            {
                LotHost.Broadcast(PlayersCopy, broadcast);
            }

            // Without deltas for the host
            if (NewPlayersCopy.Count > 0)
            {
                var noDelta = new FSOVMSurroundPuppets() { Lots = broadcast.Lots, NewPlayer = true };
                LotHost.Broadcast(NewPlayersCopy, noDelta);
            }
        }

        public void AvatarJoin(uint persistId)
        {
            lock (PlayersLock)
            {
                NewPlayers.Add(persistId);
            }
        }

        public void AvatarLeave(uint persistId)
        {
            lock (PlayersLock)
            {
                NewPlayers.Remove(persistId);
                Players.Remove(persistId);
            }
        }

        public void Dispose()
        {
            Host.Disconnect(LotLocation);
        }
    }
}
