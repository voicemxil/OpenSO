using FSO.Common;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Avatars;
using FSO.Server.Database.DA.Bans;
using FSO.Server.Database.DA.Users;
using FSO.Server.Protocol.Embedded;

namespace FSO.Server.Embedded
{
    public class ArchiveManagement
    {
        private IDAFactory DAFactory;

        public ArchiveManagement(ArchiveConfiguration config)
        {
            var sConfig = ArchiveConfigBuilder.Build(config);

            DAFactory = new SqliteDAFactory(sConfig.Database);
        }

        private ArchiveDbAvatar AvatarFromSummary(DbAvatarSummary summary)
        {
            return new ArchiveDbAvatar()
            {
                ID = summary.avatar_id,
                Name = summary.name,
                LotName = summary.lot_name,
                LastLogin = 0
            };
        }

        private ArchiveDbUser UserFromSummary(UserSummary summary)
        {
            ArchiveDbUserStatus status;

            if (summary.is_banned)
            {
                status = ArchiveDbUserStatus.Banned;
            }
            else if (!summary.is_verified) // todo: server doesn't need verification?
            {
                status = ArchiveDbUserStatus.Unverified;
            }
            else if (summary.is_admin)
            {
                status = ArchiveDbUserStatus.Admin;
            }
            else if (summary.is_moderator)
            {
                status = ArchiveDbUserStatus.Mod;
            }
            else
            {
                status = ArchiveDbUserStatus.Normal;
            }

            return new ArchiveDbUser()
            {
                ID = summary.user_id,
                AvatarCount = summary.avatar_count,
                Status = status,
                Name = summary.display_name,
            };
        }

        private ArchiveDbIpBan BanFromDb(DbBan ban)
        {
            return new ArchiveDbIpBan()
            {
                IP = ban.ip_address
            };
        }

        public List<ArchiveDbUser> GetUsers()
        {
            using (var da = DAFactory.Get())
            {
                var users = da.Users.AllSummaries();

                return [.. users.Select(UserFromSummary)];
            }
        }

        public List<ArchiveDbAvatar> GetAvatars(uint userId)
        {
            using (var da = DAFactory.Get())
            {
                var avatars = da.Avatars.GetSummaryByUserId(userId);

                return [.. avatars.Select(AvatarFromSummary)];
            }
        }

        public List<ArchiveDbIpBan> GetIpBans()
        {
            using (var da = DAFactory.Get())
            {
                var bans = da.Bans.All();

                return [.. bans.Select(BanFromDb)];
            }
        }

        public void DeleteUser(int userId)
        {
            using (var da = DAFactory.Get())
            {
                var bans = da.Bans.All();
            }
        }

        public void BanUser(int userId)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public void UnbanUser(int userId)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public void DeleteAvatar(int avatarId)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public void MigrateAvatar(int avatarId, int userId)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public void BanIp(string ip)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public void UnbanIp(string ip)
        {
            using (var da = DAFactory.Get())
            {
            }
        }

        public List<ArchiveDbUser> GetUsersForIp(string ip)
        {
            using (var da = DAFactory.Get())
            {
                var users = da.Users.AllSummaries().Where((x) => x.last_ip == ip);

                return [.. users.Select(UserFromSummary)];
            }
        }
    }
}
