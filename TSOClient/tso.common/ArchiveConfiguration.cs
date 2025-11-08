using System;
using System.Collections.Generic;
using System.IO;

namespace FSO.Common
{
    [Flags]
    public enum ArchiveConfigFlags
    {
        None = 0,
        Offline = 1 << 0,
        UPnP = 1 << 1,
        HideNames = 1 << 2,
        Verification = 1 << 3,
        AllOpenable = 1 << 4,
        DebugFeatures = 1 << 5,
        AllowLotCreation = 1 << 6,
        AllowSimCreation = 1 << 7,
        LockArchivedSims = 1 << 8,
        ReducedTickRate = 1 << 9,

        DedicatedServer = 1 << 16,

        Default = UPnP | AllOpenable | AllowLotCreation | AllowSimCreation
    }

    public class ArchiveConfiguration
    {
        public ArchiveConfigFlags Flags { get; set; }
        public string ArchiveDataDirectory { get; set; } // Effectively equal to the nfs
        public ushort CityPort { get; set; }
        public ushort LotPort { get; set; }
        public string ServerKey { get; set; }
        public float GameScale { get; set; } = 1;
        public bool AllowUserApi { get; set; }

        // Runtime
        public IDisposable[] Disposables;
    }

    public class ClientArchiveConfiguration : IniConfig
    {
        public override string HeadingComment => "Archive client + self-hosting configuration. Don't send this to other users, as it contains authentication keys!";

        private static ClientArchiveConfiguration defaultInstance;

        public static ClientArchiveConfiguration Default
        {
            get
            {
                if (defaultInstance == null)
                {
                    defaultInstance = new ClientArchiveConfiguration(Path.Combine(FSOEnvironment.UserDir, "archiveConfig.ini"));

                    if (defaultInstance.ServerPrivateKey == "")
                    {
                        defaultInstance.ServerPrivateKey = GenerateGUID();
                    }

                    if (defaultInstance.ServerPublicKey == "")
                    {
                        defaultInstance.ServerPublicKey = GenerateGUID();
                    }

                    if (defaultInstance.ClientPrivateKey == "")
                    {
                        defaultInstance.ClientPrivateKey = GenerateGUID();
                    }

                    if (defaultInstance.ClientPublicKey == "")
                    {
                        defaultInstance.ClientPublicKey = GenerateGUID();
                    }
                }
                return defaultInstance;
            }
        }

        private static string GenerateGUID()
        {
            return Guid.NewGuid().ToString();
        }

        public ClientArchiveConfiguration(string path) : base(path) { }

        private Dictionary<string, string> _DefaultValues = new Dictionary<string, string>()
        {
            { "PlayerName", "" },
            { "LastJoinedHost", "127.0.0.1" },
            { "SelectedArchiveName", "FreeSO Archive" },

            { "ServerPrivateKey", "" },
            { "ServerPublicKey", "" },
            { "ClientPrivateKey", "" },
            { "ClientPublicKey", "" },

            { "Flags", ((int)ArchiveConfigFlags.Default).ToString() },
            { "ArchiveDataDirectory", "" },
            { "CityPort", "33101" },
            { "LotPort", "34101" },
            { "GameScale", "1" },
        };

        public override Dictionary<string, string> DefaultValues
        {
            get { return _DefaultValues; }
            set { _DefaultValues = value; }
        }


        // Client configuration
        public string PlayerName { get; set; }
        public string LastJoinedHost { get; set; }
        public string SelectedArchiveName { get; set; }

        // Keys
        public string ServerPrivateKey { get; set; }
        public string ServerPublicKey { get; set; }
        public string ClientPrivateKey { get; set; }
        public string ClientPublicKey { get; set; }

        // Server configuration
        public int Flags { get; set; }
        public ushort CityPort { get; set; }
        public ushort LotPort { get; set; }
        public float GameScale { get; set; } = 1;

        public ArchiveConfiguration ToHostConfig()
        {
            return new ArchiveConfiguration()
            {
                Flags = (ArchiveConfigFlags)Flags,
                ArchiveDataDirectory = "",
                CityPort = CityPort,
                LotPort = LotPort,
                GameScale = GameScale,

                ServerKey = ServerPrivateKey
            };
        }

        public void ApplyHostConfig(ArchiveConfiguration config)
        {
            Flags = (int)config.Flags;
            CityPort = config.CityPort;
            LotPort = config.LotPort;
            GameScale = config.GameScale;
        }
    }
}
