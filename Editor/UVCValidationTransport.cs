using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;

namespace MultiplayerARPG
{
    /// <summary>Disposable localhost UDP server and two clients for adapter packet regression checks.</summary>
    internal sealed class UVCValidationTransport : IDisposable
    {
        private readonly NetManager[] _nodes = new NetManager[3];
        private readonly NetPeer[] _clients = new NetPeer[3];
        private readonly NetPeer[] _serverPeers = new NetPeer[3];
        private readonly Queue<byte[]>[] _received = { new Queue<byte[]>(), new Queue<byte[]>(), new Queue<byte[]>() };

        public UVCValidationTransport()
        {
            try
            {
                for (int i = 0; i < _nodes.Length; ++i)
                {
                    int node = i;
                    var listener = new EventBasedNetListener();
                    listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
                    {
                        _received[node].Enqueue(reader.GetRemainingBytes());
                        reader.Recycle();
                    };
                    if (i == 0)
                    {
                        listener.ConnectionRequestEvent += request => request.AcceptIfKey("uvc-validation");
                        listener.PeerConnectedEvent += peer =>
                        {
                            for (int c = 1; c < _nodes.Length; ++c)
                                if (_nodes[c] != null && peer.Port == _nodes[c].LocalPort)
                                    _serverPeers[c] = peer;
                        };
                    }
                    _nodes[i] = new NetManager(listener) { ChannelsCount = 3 };
                    if (!_nodes[i].Start("127.0.0.1", "::1", 0))
                        throw new InvalidOperationException("Could not bind UVC validation localhost socket.");
                }
                for (int i = 1; i < _nodes.Length; ++i)
                    _clients[i] = _nodes[i].Connect("127.0.0.1", _nodes[0].LocalPort, "uvc-validation");
                PumpUntil(() => _serverPeers[1] != null && _serverPeers[2] != null &&
                    _clients[1].ConnectionState == ConnectionState.Connected && _clients[2].ConnectionState == ConnectionState.Connected);
            }
            catch { Dispose(); throw; }
        }

        public byte[] Transfer(int from, int to, byte[] payload)
        {
            NetPeer peer = from == 0 ? _serverPeers[to] : _clients[from];
            peer.Send(payload, 2, DeliveryMethod.Unreliable);
            PumpUntil(() => _received[to].Count != 0);
            return _received[to].Dequeue();
        }

        private void PumpUntil(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                foreach (var node in _nodes) node?.PollEvents();
                if (timer.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("UVC localhost transport validation timed out.");
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            foreach (var node in _nodes) node?.Stop();
        }
    }
}
