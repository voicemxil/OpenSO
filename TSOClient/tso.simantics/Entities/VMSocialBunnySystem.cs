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
    /// someone to talk to. Runs as ordinary simulation code inside VM.InternalTick, executing
    /// identically on the server's VM and every client's VM.
    ///
    /// This system is deliberately STATELESS: every decision derives from the shared entity
    /// list (bunnies are identified by VMEntity.PrivateToPersistID) and the synced tick
    /// counter. It holds no fields. Any private state here (tracking dicts, cooldown timers,
    /// cached interaction ids) would NOT be marshalled with the VM, so a lot reload or a
    /// late-joining client would start with different state than the server and make
    /// different spawn decisions - duplicating bunnies and desyncing the simulation.
    /// The scan-based approach also self-heals: orphaned or duplicate bunnies from older
    /// saves are deleted, and bunnies missing their name/costume are re-dressed.
    ///
    /// There is no explicit re-spawn cooldown: the spawn (-50) / despawn (+20) hysteresis is
    /// the cooldown, since Social has to decay all the way back down before a new bunny is
    /// considered - and motive state is shared/deterministic, unlike a local timer.
    /// </summary>
    public class VMSocialBunnySystem
    {
        public const string BUNNY_NAME = "Social Bunny";

        // Motive scale runs -100 (empty, 0%) to +100 (full, 100%); percent = (motive+100)/2.
        // The two thresholds form the spawn/despawn hysteresis: the bunny only appears when the
        // owner's Social is CRITICALLY low, helps until it recovers into the 30-50% band, then
        // leaves and stays gone until Social decays back down to critical. The gap between them
        // is what stops the bunny flickering in and out around a single point.
        private const short SOCIAL_LOW_THRESHOLD = -60;       // spawn/return: ~20% (critically low)
        private const short SOCIAL_SATISFIED_THRESHOLD = -20; // despawn/leave: ~40% (middle of 30-50%)
        private const int NEARBY_PLAYER_RADIUS_TILES = 20;

        // Reserved above any real (DB-assigned) player persist id range, so ephemeral bunny
        // ids can never collide with a real avatar's persist id.
        private const uint EPHEMERAL_PERSIST_BASE = 0xF0000000;

        // Candidate stock interaction names to try, in order, since content is fully
        // data-driven (no hardcoded interaction ids exist anywhere in this engine).
        // NOT cached: resolving runs the pie menu check trees, which can consume the shared
        // random stream - so every VM instance must run it at the same tick (spawn time),
        // not just the instances that happen to have an empty cache.
        private static readonly string[] TalkInteractionNames = { "Talk", "Chat", "Introduce" };

        public void Tick(VM vm, uint tickID)
        {
            var context = vm.Context;
            var ticksPerMinute = context.Clock.TicksPerMinute;
            if (ticksPerMinute <= 0 || tickID % (uint)ticksPerMinute != 0) return; // once per sim-minute, synced via tickID

            // Snapshot: spawning/despawning below mutates ObjectQueries.Avatars.
            var avatars = new List<VMEntity>(context.ObjectQueries.Avatars);

            // Pass 1 - rebuild the authoritative bunny map from shared state. Delete orphans
            // (target no longer on the lot) and duplicates (from pre-stateless saves), and
            // re-dress any bunny that lost its identity (loaded from an old save).
            var bunniesByTarget = new Dictionary<uint, VMAvatar>();
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar bunny) || bunny.Dead || bunny.PrivateToPersistID == null) continue;

                var targetId = bunny.PrivateToPersistID.Value;
                var bunnyTarget = FindRealPlayer(avatars, targetId);
                if (bunniesByTarget.ContainsKey(targetId) || bunnyTarget == null)
                {
                    bunny.Delete(true, context);
                    continue;
                }

                bunniesByTarget[targetId] = bunny;
                if (bunny.Name != BUNNY_NAME)
                {
                    bunny.Name = BUNNY_NAME;
                    VMSocialBunnySuits.ApplyBunnyCostume(bunny);
                }
                // Re-asserted every sim-minute: keeps the bunny accepting socials even after
                // interactions drain its motives, and heals bunnies from older saves.
                SetupBunnyPsyche(bunny, bunnyTarget);
            }

            // Pass 2 - per real player: despawn a no-longer-needed bunny, or spawn one.
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar avatar) || !IsRealPlayer(avatar)) continue;

                if (bunniesByTarget.TryGetValue(avatar.PersistID, out var bunny))
                {
                    if (ShouldDespawn(avatar, bunny, avatars)) bunny.Delete(true, context);
                    continue;
                }

                if (avatar.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (avatar.GetMotiveData(VMMotive.Social) > SOCIAL_LOW_THRESHOLD) continue;
                if (IsAnyOtherRealPlayerNearby(avatar, avatars)) continue;

                SpawnBunny(avatar, avatars, context);
            }
        }

        private static bool IsRealPlayer(VMAvatar avatar)
        {
            return !avatar.Dead && avatar.PersistID != 0 && !avatar.IsPet
                && avatar.PrivateToPersistID == null
                && avatar.GetPersonData(VMPersonDataVariable.PersonType) < 254;
        }

        private static VMAvatar FindRealPlayer(List<VMEntity> avatars, uint persistID)
        {
            foreach (var ent in avatars)
            {
                if (ent is VMAvatar avatar && avatar.PersistID == persistID && IsRealPlayer(avatar)) return avatar;
            }
            return null;
        }

        private static bool IsAnyOtherRealPlayerNearby(VMAvatar target, List<VMEntity> avatars)
        {
            if (target.Position == LotTilePos.OUT_OF_WORLD) return false;
            foreach (var ent in avatars)
            {
                if (!(ent is VMAvatar other) || other == target || !IsRealPlayer(other)) continue;
                if (other.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (other.Position.Level != target.Position.Level) continue;

                var dx = other.Position.TileX - target.Position.TileX;
                var dy = other.Position.TileY - target.Position.TileY;
                if (dx * dx + dy * dy <= NEARBY_PLAYER_RADIUS_TILES * NEARBY_PLAYER_RADIUS_TILES) return true;
            }
            return false;
        }

        // True while the bunny and its target are engaged with each other (either one has the
        // other queued/active) - don't yank the bunny away mid-social.
        private static bool BusyWithEachOther(VMAvatar target, VMAvatar bunny)
        {
            if (bunny.Thread != null)
            {
                foreach (var action in bunny.Thread.Queue)
                {
                    if (action.Callee == target || action.StackObject == target) return true;
                }
            }
            if (target.Thread != null)
            {
                foreach (var action in target.Thread.Queue)
                {
                    if (action.Callee == bunny || action.StackObject == bunny) return true;
                }
            }
            return false;
        }

        private static bool ShouldDespawn(VMAvatar target, VMAvatar bunny, List<VMEntity> avatars)
        {
            if (target.Dead || target.Position == LotTilePos.OUT_OF_WORLD) return true; // target left the lot
            if (bunny.Dead) return false; // already gone, nothing to do
            if (BusyWithEachOther(target, bunny)) return false;

            if (target.GetMotiveData(VMMotive.Social) >= SOCIAL_SATISFIED_THRESHOLD) return true;
            if (IsAnyOtherRealPlayerNearby(target, avatars)) return true;

            return false;
        }

        private static void SpawnBunny(VMAvatar target, List<VMEntity> avatars, VMContext context)
        {
            var group = context.CreateObjectInstance(VMAvatar.TEMPLATE_PERSON, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            if (group == null || group.Objects.Count == 0) return;

            var bunny = (VMAvatar)group.Objects[0];
            bunny.PersistID = AllocateEphemeralPersistID(avatars);
            bunny.PrivateToPersistID = target.PersistID;
            bunny.Name = BUNNY_NAME;
            VMSocialBunnySuits.ApplyBunnyCostume(bunny);
            SetupBunnyPsyche(bunny, target);

            if (!VMFindLocationFor.FindLocationFor(bunny, target, context, VMPlaceRequestFlags.Default))
            {
                // No room to place the bunny near the target this tick - clean up and retry
                // on a later tick instead of leaving a stuck out-of-world avatar around.
                bunny.Delete(true, context);
                return;
            }

            // Greet the target once: the bunny initiates a stock social. After this the bunny
            // is a normal pie-menu target - the player interacts with it like any Sim.
            var talk = ResolveTalkInteraction(target, bunny, context);
            if (talk != null)
            {
                target.PushUserInteraction(talk.Value.index, bunny, context, talk.Value.global);
            }
        }

        // Make the bunny accept any social the player initiates (dance, play, etc.).
        // Acceptance check trees in the stock social BHAVs weigh the RECEIVER's relationship
        // to the asker, their mood, and their personality - so give the bunny a maxed
        // opinion of its player, a permanently great mood (Cheats=1 also freezes motive
        // decay, see VMAvatarMotiveDecay.Tick), and a maximally friendly/playful
        // personality. All synced simulation state, applied identically on every VM
        // instance. Only the bunny's own relationship map is touched - writing to the
        // player's map would leak ephemeral bunny ids into their DB relationship rows.
        private static void SetupBunnyPsyche(VMAvatar bunny, VMAvatar target)
        {
            if (!bunny.MeToPersist.TryGetValue(target.PersistID, out var rel))
            {
                rel = new List<short>();
                bunny.MeToPersist[target.PersistID] = rel;
            }
            while (rel.Count < 2) rel.Add(0);
            rel[0] = 100; // short-term relationship
            rel[1] = 100; // long-term relationship

            // NPC person type (>=254): excluded from visitor counting/greet logic, the
            // person-grid toolbar, and shown with the object cursor. Engine-side
            // interaction permissions use AvatarState.Permissions (not PersonType), so
            // socials still work. GreetStatus 2 = "greeted", in case any content still
            // runs visitor-greet checks against it.
            bunny.SetPersonData(VMPersonDataVariable.PersonType, 254);
            bunny.SetPersonData(VMPersonDataVariable.GreetStatus, 2);

            bunny.SetPersonData(VMPersonDataVariable.Cheats, 1);
            bunny.SetPersonData(VMPersonDataVariable.Gender, 1); // matches the female costume set
            bunny.SetPersonData(VMPersonDataVariable.NicePersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.OutgoingPersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.PlayfulPersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.ActivePersonality, 1000);
            bunny.SetPersonData(VMPersonDataVariable.GenerousPersonality, 1000);

            bunny.SetMotiveData(VMMotive.Mood, 100);
            bunny.SetMotiveData(VMMotive.Energy, 100);
            bunny.SetMotiveData(VMMotive.Comfort, 100);
            bunny.SetMotiveData(VMMotive.Hunger, 100);
            bunny.SetMotiveData(VMMotive.Hygiene, 100);
            bunny.SetMotiveData(VMMotive.Bladder, 100);
            bunny.SetMotiveData(VMMotive.Fun, 100);
            bunny.SetMotiveData(VMMotive.Social, 100);
        }

        private static uint AllocateEphemeralPersistID(List<VMEntity> avatars)
        {
            // Derived from shared state so it's identical on every VM instance, and never
            // reuses the id of a bunny still present from an earlier save.
            uint id = EPHEMERAL_PERSIST_BASE;
            foreach (var ent in avatars)
            {
                if (ent.PersistID >= id) id = ent.PersistID + 1;
            }
            return id;
        }

        private static (int index, bool global)? ResolveTalkInteraction(VMAvatar target, VMAvatar bunny, VMContext context)
        {
            var pie = target.GetPieMenu(context.VM, bunny, false, true);
            foreach (var name in TalkInteractionNames)
            {
                foreach (var entry in pie)
                {
                    if (entry.Name != null && entry.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return (entry.ID, entry.Global);
                    }
                }
            }
            return null;
        }
    }
}
