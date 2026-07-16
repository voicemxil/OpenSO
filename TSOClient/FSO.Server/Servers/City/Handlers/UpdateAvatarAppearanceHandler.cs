using FSO.Server.Database.DA;
using FSO.Server.Protocol.Electron.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Vitaboy;
using System.Text.RegularExpressions;
using FSO.Server.Framework.Voltron;
using FSO.SimAntics.Engine.Scopes;
using NLog;

namespace FSO.Server.Servers.City.Handlers
{
    /// <summary>
    /// Applies an appearance edit (gender, skin tone, head, body, description) to an existing avatar
    /// owned by the session's account. Arrives on the anonymous CAS connection, so the avatar itself
    /// is offline and the database is the single source of truth — the next login picks the change up
    /// everywhere. Name changes are intentionally not possible here (moderation only).
    /// </summary>
    public class UpdateAvatarAppearanceHandler
    {
        private static Logger LOG = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Only printable ascii characters
        /// Minimum 0 characters
        /// Maximum 499 characters
        /// </summary>
        private static Regex DESC_VALIDATION = new Regex("^([a-zA-Z0-9\\s\\x20-\\x7F]){0,499}$");

        private CityServerContext Context;
        private IDAFactory DAFactory;

        /// <summary>
        /// Used for validation, keyed by the high dword of the outfit asset ID (same as RegistrationHandler)
        /// </summary>
        private Dictionary<uint, PurchasableOutfit> ValidFemaleOutfits = new Dictionary<uint, PurchasableOutfit>();
        private Dictionary<uint, PurchasableOutfit> ValidMaleOutfits = new Dictionary<uint, PurchasableOutfit>();

        public UpdateAvatarAppearanceHandler(CityServerContext context, IDAFactory daFactory, Content.Content content)
        {
            this.Context = context;
            this.DAFactory = daFactory;

            content.AvatarCollections.Get("ea_female_heads.col")
                .Select(x => content.AvatarPurchasables.Get(x.PurchasableOutfitId)).ToList()
                    .ForEach(x => ValidFemaleOutfits.Add((uint)(x.OutfitID >> 32), x));

            content.AvatarCollections.Get("ea_female.col")
                .Select(x => content.AvatarPurchasables.Get(x.PurchasableOutfitId)).ToList()
                    .ForEach(x => ValidFemaleOutfits.Add((uint)(x.OutfitID >> 32), x));

            content.AvatarCollections.Get("ea_male_heads.col")
                .Select(x => content.AvatarPurchasables.Get(x.PurchasableOutfitId)).ToList()
                    .ForEach(x => ValidMaleOutfits.Add((uint)(x.OutfitID >> 32), x));

            content.AvatarCollections.Get("ea_male.col")
                .Select(x => content.AvatarPurchasables.Get(x.PurchasableOutfitId)).ToList()
                    .ForEach(x => ValidMaleOutfits.Add((uint)(x.OutfitID >> 32), x));
        }

        private void Fail(IVoltronSession session, UpdateAvatarAppearanceFailureReason reason)
        {
            session.Write(new UpdateAvatarAppearanceResponse
            {
                Status = UpdateAvatarAppearanceStatus.FAILED,
                Reason = reason
            });
        }

        public void Handle(IVoltronSession session, UpdateAvatarAppearanceRequest packet)
        {
            //only the anonymous CAS connection may edit — a session with a selected avatar has live
            //city/lot state that would go stale under it.
            if (!session.IsAnonymous) return;

            var male = packet.Gender == Protocol.Voltron.Model.Gender.MALE;
            var valid = male ? ValidMaleOutfits : ValidFemaleOutfits;

            PurchasableOutfit head = null;
            PurchasableOutfit body = null;
            valid.TryGetValue(packet.HeadOutfitId, out head);
            valid.TryGetValue(packet.BodyOutfitId, out body);

            if (head == null)
            {
                Fail(session, UpdateAvatarAppearanceFailureReason.HEAD_VALIDATION_ERROR);
                return;
            }

            if (body == null)
            {
                Fail(session, UpdateAvatarAppearanceFailureReason.BODY_VALIDATION_ERROR);
                return;
            }

            if (packet.Description == null || !DESC_VALIDATION.IsMatch(packet.Description))
            {
                Fail(session, UpdateAvatarAppearanceFailureReason.DESC_VALIDATION_ERROR);
                return;
            }

            using (var db = DAFactory.Get())
            {
                var avatar = db.Avatars.Get(packet.AvatarId);
                if (avatar == null || avatar.user_id != session.UserId || avatar.shard_id != Context.ShardId)
                {
                    Fail(session, UpdateAvatarAppearanceFailureReason.NOT_OWNER);
                    return;
                }

                //refuse while the avatar is claimed (online in this or another session)
                if (db.AvatarClaims.GetByAvatarID(packet.AvatarId) != null)
                {
                    Fail(session, UpdateAvatarAppearanceFailureReason.AVATAR_IN_USE);
                    return;
                }

                //edit price comes from the 'edit_sim' tuning entry (fso_tuning), free by default
                var cost = (int)(db.Tuning.AllCategory("edit_sim", 0)
                    .FirstOrDefault(x => x.tuning_index == 0)?.value ?? 0);

                var newGender = male ? Database.DA.Avatars.DbAvatarGender.male : Database.DA.Avatars.DbAvatarGender.female;
                var genderChanged = avatar.gender != newGender;

                var update = new Database.DA.Avatars.DbAvatar();
                update.avatar_id = avatar.avatar_id;
                update.gender = newGender;
                update.skin_tone = (byte)packet.SkinTone;
                update.head = head.OutfitID;
                update.body = body.OutfitID;
                update.body_current = body.OutfitID;

                if (genderChanged)
                {
                    //old-gender underwear sets no longer apply; reset to the CAS defaults for the new gender
                    if (male)
                    {
                        update.body_swimwear = 0x5470000000D;
                        update.body_sleepwear = 0x5440000000D;
                    }
                    else
                    {
                        update.body_swimwear = 0x620000000D;
                        update.body_sleepwear = 0x5150000000D;
                    }
                }
                else
                {
                    update.body_swimwear = avatar.body_swimwear;
                    update.body_sleepwear = avatar.body_sleepwear;
                }

                if (db.Avatars.UpdateAvatarAppearance(update, cost) == 0)
                {
                    //avatar row exists, so the only failing condition is the budget guard
                    Fail(session, UpdateAvatarAppearanceFailureReason.INSUFFICIENT_FUNDS);
                    return;
                }

                if (packet.Description != avatar.description)
                {
                    db.Avatars.UpdateDescription(avatar.avatar_id, packet.Description);
                }

                //make sure the dresser owns the outfits the avatar now wears (mirrors CAS creation rows)
                try
                {
                    var owned = db.Outfits.GetByAvatarId(avatar.avatar_id);
                    var ensure = new List<Tuple<ulong, byte>>
                    {
                        new Tuple<ulong, byte>(update.body, (byte)VMPersonSuits.DefaultDaywear)
                    };
                    if (genderChanged)
                    {
                        ensure.Add(new Tuple<ulong, byte>(update.body_sleepwear, (byte)VMPersonSuits.DefaultSleepwear));
                        ensure.Add(new Tuple<ulong, byte>(update.body_swimwear, (byte)VMPersonSuits.DefaultSwimwear));
                    }
                    foreach (var outfit in ensure)
                    {
                        if (owned.Any(x => x.asset_id == outfit.Item1)) continue;
                        db.Outfits.Create(new Database.DA.Outfits.DbOutfit
                        {
                            avatar_owner = avatar.avatar_id,
                            asset_id = outfit.Item1,
                            purchase_price = 0,
                            sale_price = 0,
                            outfit_type = outfit.Item2,
                            outfit_source = Database.DA.Outfits.DbOutfitSource.cas
                        });
                    }
                }
                catch (Exception e)
                {
                    //the appearance change itself already applied; missing dresser rows are not fatal
                    LOG.Warn(e, "Failed to ensure dresser outfits after appearance edit for avatar " + avatar.avatar_id);
                }

                LOG.Info("Avatar " + avatar.name + " (" + avatar.avatar_id + ") appearance edited by user " + session.UserId
                    + (genderChanged ? " (gender changed)" : "") + (cost > 0 ? " for $" + cost : "") + ".");
            }

            session.Write(new UpdateAvatarAppearanceResponse
            {
                Status = UpdateAvatarAppearanceStatus.SUCCESS
            });
        }
    }
}
