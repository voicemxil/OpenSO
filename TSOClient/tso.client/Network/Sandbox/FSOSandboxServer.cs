using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FSO.SimAntics.NetPlay.Model;
using FSO.Common.Utils;

namespace FSO.Client.Network.Sandbox
{
    /// <summary>
    /// The in-client sandbox lot server: accepts direct connections from other clients and
    /// relays VMNetMessages to/from the hosting VM. A joining client is announced (OnConnect)
    /// only once it has sent its avatar data.
    /// </summary>
    public class FSOSandboxServer
    {
        //todo: persist id provider
        //right now just give each new connected user a higher persist id than the last
        public uint PersistID = 2;

        public event Action<VMNetClient, VMNetMessage> OnMessage;
        public event Action<VMNetClient> OnConnect;
        public event Action<VMNetClient> OnDisconnect;

        private List<SandboxSession> Sessions = new List<SandboxSession>();
        private TcpListener Listener;
        private volatile bool Stopped;

        public void ForceDisconnect(VMNetClient cli)
        {
            if (cli.NetHandle == null) return;
            ((SandboxSession)cli.NetHandle).Close(false);
        }

        public void SendMessage(VMNetClient cli, VMNetMessage msg)
        {
            if (cli.NetHandle == null) return;
            ((SandboxSession)cli.NetHandle).Write(msg);
        }

        public void Broadcast(VMNetMessage msg, HashSet<VMNetClient> ignore)
        {
            List<SandboxSession> cliClone;
            lock (Sessions) cliClone = new List<SandboxSession>(Sessions);
            foreach (var s in cliClone)
            {
                if (ignore.Contains(s.Tag)) continue;
                s.Write(msg);
            }
        }

        private void MessageReceived(SandboxSession session, VMNetMessage nmsg)
        {
            GameThread.NextUpdate(x =>
            {
                var cli = (VMNetClient)session.Tag;
                if (cli.AvatarState == null)
                {
                    //we're still waiting for the avatar state so the user can join
                    if (nmsg.Type == VMNetMessageType.AvatarData)
                    {
                        var state = new VMNetAvatarPersistState();
                        try
                        {
                            state.Deserialize(new BinaryReader(new MemoryStream(nmsg.Data)));
                        }
                        catch (Exception)
                        {
                            return;
                        }
                        cli.PersistID = state.PersistID;
                        cli.AvatarState = state;

                        OnConnect(cli);
                    }
                }
                else
                {
                    OnMessage(cli, nmsg);
                }
            });
        }

        private void SessionClosed(SandboxSession session)
        {
            var cli = (VMNetClient)session.Tag;
            if (cli != null) GameThread.NextUpdate(x =>
            {
                OnDisconnect(cli);
            });

            lock (Sessions) Sessions.Remove(session);
        }

        public void Start(ushort port)
        {
            try
            {
                Listener = new TcpListener(new IPEndPoint(IPAddress.Any, port));
                Listener.Start();
            }
            catch
            {
                return; // same as before: a failed bind (port in use) is silently ignored
            }
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!Stopped)
            {
                TcpClient tcp;
                try
                {
                    tcp = await Listener.AcceptTcpClientAsync();
                }
                catch
                {
                    if (Stopped) return;
                    continue;
                }

                try
                {
                    tcp.NoDelay = true;
                    var session = new SandboxSession(tcp, MessageReceived, SessionClosed);
                    var cli = new VMNetClient()
                    {
                        PersistID = PersistID++,
                        RemoteIP = session.RemoteEndPoint?.ToString() ?? "unknown",
                        AvatarState = null,
                    };
                    cli.NetHandle = session;
                    session.Tag = cli;

                    lock (Sessions) Sessions.Add(session);
                    session.Start();
                }
                catch
                {
                    try { tcp.Close(); } catch { }
                }
            }
        }

        public void Shutdown()
        {
            Stopped = true;
            try { Listener?.Stop(); } catch { }
            lock (Sessions)
            {
                foreach (var s in Sessions) s.Close(true);
            }
        }
    }
}
