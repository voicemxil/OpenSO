using FSO.Common.DataService;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Avatars;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.CitySelector;
using FSO.Server.Protocol.Electron.Packets;
using Ninject;
using NLog;
using System.Linq;

namespace FSO.Server.Servers.City.Handlers
{
    internal class ArchiveAvatarsHandler
    {
        private static Logger LOG = LogManager.GetCurrentClassLogger();
        private IDAFactory DA;
        private CityServerContext Context;
        private IKernel Kernel;

        public ArchiveAvatarsHandler(CityServerContext context, IDAFactory da, IDataService dataService, IKernel kernel)
        {
            Context = context;
            DA = da;
            Kernel = kernel;
        }

        private static ArchiveAvatar ToArchiveAvatar(DbAvatarSummary ava)
        {
            return new ArchiveAvatar()
            {
                AvatarId = ava.avatar_id,
                UserId = ava.user_id,
                LotId = ava.lot_location ?? 0,
                Name = ava.name,
                LotName = ava.lot_name,
                Type = (AvatarAppearanceType)ava.skin_tone,
                Head = ava.head,
                Body = ava.body
            };
        }

        public async void Handle(IVoltronSession session, ArchiveAvatarsRequest _packet)
        {
            if (Context.Config.Archive == null)
                return;

            if (session.UserId == 0)
                return;

            try
            {
                if (session is VoltronSession vSession && vSession.Unverified)
                {
                    // User must be verified first.
                    session.Write(new ArchiveAvatarsResponse()
                    {
                        IsVerified = false,
                        RecentAvatars = new uint[0],
                        UserAvatars = new ArchiveAvatar[0],
                        SharedAvatars = new ArchiveAvatar[0],
                    });

                    return;
                }

                using (var da = DA.Get())
                {
                    var forUser = da.Avatars.GetSummaryByUserId(session.UserId);

                    var userAvatars = forUser.Select(ToArchiveAvatar).ToArray();

                    // TODO: cache?

                    var shared = da.Avatars.GetSummaryByUserId(1);
                    var sharedAvatars = shared.Select(ToArchiveAvatar).ToArray();

                    // Can't cache this obviously
                    var mostRecent = da.ArchiveRecents.AvatarsByUser((int)session.UserId, 5);
                    var recentAvatars = mostRecent.Select(x => (uint)x).ToArray();

                    session.Write(new ArchiveAvatarsResponse()
                    {
                        IsVerified = true,
                        UserAvatars = userAvatars,
                        SharedAvatars = sharedAvatars,
                        RecentAvatars = recentAvatars
                    });
                }
            }
            catch
            {

            }
        }
      }
}
