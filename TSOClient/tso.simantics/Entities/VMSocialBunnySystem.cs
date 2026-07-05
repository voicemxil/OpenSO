using System.Collections.Generic;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Entities
{
    /// <summary>
    /// Deterministic per-tick system: spawns a private "Social Bunny" NPC next to a Sim whose
    /// Social motive is low and who has no other real player nearby, so they always have
    /// someone to talk to. This runs as ordinary simulation code inside VM.InternalTick, so it
    /// executes identically and independently on the dedicated server's VM instance and every
    /// connected client's VM instance - the spawn/despawn decision is never made unilaterally
    /// by a client, it falls out of the same synced deterministic state everyone already has.
    ///
    /// The bunny is a real VMAvatar (full pathing/pie-menu/BHAV interactions), hidden from all
    /// clients except the target via VMEntity.PrivateToPersistID, which is consulted only by
    /// the client-side render/pick/UI layer - never by simulation logic - so it introduces no
    /// determinism risk.
    /// </summary>
    public class VMSocialBunnySystem
    {
        // Motive scale in this codebase runs roughly -100 (starving) to 100 (fully satisfied).
        private const short SOCIAL_LOW_THRESHOLD = -50;
        private const short SOCIAL_SATISFIED_THRESHOLD = 20;
        private const int NEARBY_PLAYER_RADIUS_TILES = 20;
        private const int COOLDOWN_SIM_MINUTES = 30; // session-only, not persisted

        // Reserved above any real (DB-assigned) player persist id range, so ephemeral bunny
        // ids can never collide with a real avatar's persist id.
        private const uint EPHEMERAL_PERSIST_BASE = 0xF0000000;

        private int LastMinute = -1;
        private uint NextEphemeralPersist = EPHEMERAL_PERSIST_BASE;

        // target avatar persist id -> the bunny currently assigned to them
        private readonly Dictionary<uint, VMAvatar> ActiveBunnies = new Dictionary<uint, VMAvatar>();
        // target avatar persist id -> sim-minutes remaining before eligible for another bunny
        private readonly Dictionary<uint, int> Cooldowns = new Dictionary<uint, int>();

        // Candidate stock interaction names to try, in order, since content is fully
        // data-driven (no hardcoded interaction ids exist anywhere in this engine). Cached
        // once resolved, on the assumption that all Person-type avatars share the same
        // tree table content.
        private static readonly string[] TalkInteractionNames = { "Talk", "Chat", "Introduce" };
        private int? CachedTalkInteractionIndex;
        private bool CachedTalkInteractionGlobal;

        public void Tick(VM vm)
        {
            var context = vm.Context;
            if (context.Clock.Minutes == LastMinute) return;
            LastMinute = context.Clock.Minutes;

            TickCooldowns();

            // Snapshot: spawning/despawning below mutates ObjectQueries.Avatars, so iterate a
            // copy rather than the live collection.
            var avatars = new List<VMEntity>(context.ObjectQueries.Avatars);

            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar avatar)) continue;
                if (avatar.PersistID == 0 || avatar.IsPet) continue;
                if (avatar.PrivateToPersistID != null) continue; // this is a bunny (or other private npc)
                if (avatar.GetPersonData(VMPersonDataVariable.PersonType) >= 254) continue; // not a real player

                if (ActiveBunnies.TryGetValue(avatar.PersistID, out var bunny))
                {
                    if (ShouldDespawn(avatar, bunny, context))
                    {
                        DespawnBunny(avatar.PersistID, bunny, context);
                    }
                    continue;
                }

                if (Cooldowns.ContainsKey(avatar.PersistID)) continue;
                if (avatar.GetMotiveData(VMMotive.Social) > SOCIAL_LOW_THRESHOLD) continue;
                if (IsAnyOtherRealPlayerNearby(avatar, avatars)) continue;

                SpawnBunny(avatar, context);
            }
        }

        private void TickCooldowns()
        {
            if (Cooldowns.Count == 0) return;
            var expired = new List<uint>();
            foreach (var kv in Cooldowns)
            {
                var remaining = kv.Value - 1;
                if (remaining <= 0) expired.Add(kv.Key);
                else Cooldowns[kv.Key] = remaining;
            }
            foreach (var id in expired) Cooldowns.Remove(id);
        }

        private bool IsAnyOtherRealPlayerNearby(VMAvatar target, List<VMEntity> avatars)
        {
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar other) || other == target) continue;
                if (other.PersistID == 0 || other.IsPet) continue;
                if (other.PrivateToPersistID != null) continue; // ignore other bunnies
                if (other.GetPersonData(VMPersonDataVariable.PersonType) >= 254) continue;
                if (other.Position == LotTilePos.OUT_OF_WORLD || target.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (other.Position.Level != target.Position.Level) continue;

                var dx = other.Position.TileX - target.Position.TileX;
                var dy = other.Position.TileY - target.Position.TileY;
                if (dx * dx + dy * dy <= NEARBY_PLAYER_RADIUS_TILES * NEARBY_PLAYER_RADIUS_TILES) return true;
            }
            return false;
        }

        private bool ShouldDespawn(VMAvatar target, VMAvatar bunny, VMContext context)
        {
            if (target.Dead || target.Position == LotTilePos.OUT_OF_WORLD) return true; // target left the lot
            if (bunny.Dead) return true;
            if (target.GetMotiveData(VMMotive.Social) >= SOCIAL_SATISFIED_THRESHOLD) return true;

            var avatars = new List<VMEntity>(context.ObjectQueries.Avatars);
            if (IsAnyOtherRealPlayerNearby(target, avatars)) return true;

            return false;
        }

        private void SpawnBunny(VMAvatar target, VMContext context)
        {
            var group = context.CreateObjectInstance(VMAvatar.TEMPLATE_PERSON, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            if (group == null || group.Objects.Count == 0) return;

            var bunny = (VMAvatar)group.Objects[0];
            bunny.PersistID = NextEphemeralPersist++;
            bunny.PrivateToPersistID = target.PersistID;
            VMSocialBunnySuits.ApplyBunnyCostume(bunny);

            if (!VMFindLocationFor.FindLocationFor(bunny, target, context, VMPlaceRequestFlags.Default))
            {
                // No room to place the bunny near the target this tick - clean up and retry
                // on a later tick instead of leaving a stuck out-of-world avatar around.
                bunny.Delete(true, context);
                return;
            }

            ActiveBunnies[target.PersistID] = bunny;

            var talk = ResolveTalkInteraction(target, bunny, context);
            if (talk != null)
            {
                target.PushUserInteraction(talk.Value, bunny, context, CachedTalkInteractionGlobal);
            }
        }

        private void DespawnBunny(uint targetPersistID, VMAvatar bunny, VMContext context)
        {
            if (!bunny.Dead) bunny.Delete(true, context);
            ActiveBunnies.Remove(targetPersistID);
            Cooldowns[targetPersistID] = COOLDOWN_SIM_MINUTES;
        }

        private int? ResolveTalkInteraction(VMAvatar target, VMAvatar bunny, VMContext context)
        {
            if (CachedTalkInteractionIndex != null) return CachedTalkInteractionIndex;

            var pie = target.GetPieMenu(context.VM, bunny, false, true);
            foreach (var name in TalkInteractionNames)
            {
                foreach (var entry in pie)
                {
                    if (entry.Name != null && entry.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        CachedTalkInteractionIndex = entry.ID;
                        CachedTalkInteractionGlobal = entry.Global;
                        return CachedTalkInteractionIndex;
                    }
                }
            }

            return null;
        }
    }
}
