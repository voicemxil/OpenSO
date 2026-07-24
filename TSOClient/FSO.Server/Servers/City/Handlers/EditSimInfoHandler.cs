using FSO.Server.Database.DA;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.Electron.Packets;
using System;
using System.Linq;

namespace FSO.Server.Servers.City.Handlers
{
    /// <summary>
    /// Tells CAS what an appearance edit costs and whether this sim can afford it, so the screen can
    /// show the price and disable Accept rather than letting the player build a new look and only then
    /// be told they're short. Also reports whether the rename cooldown is up.
    ///
    /// Nothing here is trusted for the charge: UpdateAvatarAppearanceHandler re-reads the price and the
    /// debit is guarded by budget >= cost in SQL. This exists purely so the UI can be honest up front.
    /// </summary>
    public class EditSimInfoHandler
    {
        private CityServerContext Context;
        private IDAFactory DAFactory;

        public EditSimInfoHandler(CityServerContext context, IDAFactory daFactory)
        {
            this.Context = context;
            this.DAFactory = daFactory;
        }

        public void Handle(IVoltronSession session, EditSimInfoRequest packet)
        {
            //same gate as the edit itself - this returns a balance, so it must never answer for an
            //avatar the caller doesn't own.
            if (!session.IsAnonymous) return;

            using (var db = DAFactory.Get())
            {
                var avatar = db.Avatars.Get(packet.AvatarId);
                if (avatar == null || avatar.user_id != session.UserId || avatar.shard_id != Context.ShardId)
                    return; //silently ignore: a non-owner learns nothing, not even that the sim exists

                var price = (int)(db.Tuning.AllCategory("edit_sim", 0)
                    .FirstOrDefault(x => x.tuning_index == 0)?.value ?? 0);

                var canRename = avatar.name_change_date == null
                    || (DateTime.UtcNow - avatar.name_change_date.Value) >= UpdateAvatarAppearanceHandler.NAME_CHANGE_COOLDOWN;

                session.Write(new EditSimInfoResponse
                {
                    AppearancePrice = price,
                    Budget = avatar.budget,
                    CanRename = canRename
                });
            }
        }
    }
}
