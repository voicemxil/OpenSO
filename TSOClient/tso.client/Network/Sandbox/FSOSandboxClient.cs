using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FSO.SimAntics.NetPlay.Model;
using FSO.Common.Utils;

namespace FSO.Client.Network.Sandbox
{
    /// <summary>
    /// Client end of a sandbox-mode connection. Matches the old Mina connector's observable
    /// behavior: OnConnectComplete fires (on the game thread) only when the connection succeeds;
    /// a failed or timed-out connect fires nothing.
    /// </summary>
    public class FSOSandboxClient
    {
        private const int ConnectTimeoutMs = 10000;

        private SandboxSession Session;

        public event Action<VMNetMessage> OnMessage;
        public event Action OnConnectComplete;

        public void Connect(string address)
        {
            Connect(CreateIPEndPoint(address));
        }

        public void Disconnect()
        {
            Session?.Close(false);
        }

        public void Connect(IPEndPoint target)
        {
            Task.Run(async () =>
            {
                try
                {
                    var tcp = new TcpClient(target.AddressFamily);
                    tcp.NoDelay = true;
                    using (var timeout = new CancellationTokenSource(ConnectTimeoutMs))
                    {
                        await tcp.ConnectAsync(target.Address, target.Port, timeout.Token);
                    }
                    var session = new SandboxSession(tcp, (s, msg) => OnMessage?.Invoke(msg), s => { });
                    Session = session;
                    session.Start();
                    GameThread.NextUpdate(x =>
                    {
                        OnConnectComplete();
                    });
                }
                catch
                {
                    // failed connect: no event, as before
                }
            });
        }

        public void Write(params object[] packets)
        {
            Session?.Write(packets);
        }

        public bool IsConnected
        {
            get
            {
                var session = Session;
                return session != null && session.Connected;
            }
        }

        public static IPEndPoint CreateIPEndPoint(string endPoint)
        {
            string[] ep = endPoint.Split(':');
            if (ep.Length != 2) throw new FormatException("Invalid endpoint format");
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(ep[0], out ip))
            {
                var addrs = Dns.GetHostEntry(ep[0]).AddressList;
                if (addrs.Length == 0)
                {
                    throw new FormatException("Invalid ip-address");
                }
                else ip = addrs[0];
            }

            int port;
            if (!int.TryParse(ep[1], NumberStyles.None, NumberFormatInfo.CurrentInfo, out port))
            {
                throw new FormatException("Invalid port");
            }
            return new IPEndPoint(ip, port);
        }
    }
}
