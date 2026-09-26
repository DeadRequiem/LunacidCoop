using System;
using System.Net;
using System.Net.Sockets;

namespace LunacidCoopMod
{
    // TCP-localhost transport for local-test mode. Single-peer; reuses MessageBuffer framing.
    public class LocalTransport
    {
        public const int DEFAULT_PORT = 7777;

        // Host accepted a TCP connection. Owner sends the handshake from here.
        public event Action OnPeerAccepted;

        // A complete framed message was parsed from the stream.
        public event Action<NetworkMessage> OnMessageParsed;

        // Connection dropped. Fires once per drop.
        public event Action OnPeerDisconnected;

        public bool IsHosting   => listener != null;
        public bool IsConnected => client != null && stream != null;

        private TcpListener listener;
        private TcpClient   client;
        private NetworkStream stream;
        private readonly MessageBuffer buffer = new MessageBuffer();
        private bool disconnectedFired;

        public bool StartHosting(int port = DEFAULT_PORT)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Plugin.Log.LogInfo($"[Local] Hosting on localhost:{port} - waiting for connection");
                disconnectedFired = false;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Local] Host start failed: {e.Message}");
                listener = null;
                return false;
            }
        }

        public bool ConnectAsClient(int port = DEFAULT_PORT)
        {
            try
            {
                client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                stream = client.GetStream();
                disconnectedFired = false;
                Plugin.Log.LogInfo($"[Local] Connected to localhost:{port}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Local] Connect failed: {e.Message}");
                StopPeer();
                return false;
            }
        }

        // Write raw bytes to the peer. Tears the connection down and fires OnPeerDisconnected on failure.
        public void Send(byte[] packet)
        {
            if (stream == null) return;
            try { stream.Write(packet, 0, packet.Length); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Local] Send error: {e.Message} - dropping connection");
                FireDisconnect();
            }
        }

        // Per-frame: accept a pending client (host) and drain available bytes into parsed messages.
        public void Pump()
        {
            // Host: accept a waiting connection.
            if (listener != null && client == null)
            {
                if (listener.Pending())
                {
                    try
                    {
                        client = listener.AcceptTcpClient();
                        stream = client.GetStream();
                        Plugin.Log.LogInfo("[Local] Client connected (awaiting handshake)");
                        OnPeerAccepted?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[Local] Accept failed: {e.Message}");
                    }
                }
                return;
            }

            if (stream == null || client == null) return;

            // Drain incoming data and dispatch parsed messages.
            try
            {
                while (client.Available > 0)
                {
                    byte[] buf = new byte[client.Available];
                    int read = stream.Read(buf, 0, buf.Length);
                    if (read <= 0) break;

                    var msg = buffer.ProcessData(buf, read);
                    while (msg != null)
                    {
                        OnMessageParsed?.Invoke(msg);
                        msg = buffer.ProcessData(new byte[0], 0);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Local] Receive error: {e.Message}");
                FireDisconnect();
            }
        }

        // Close the active peer but keep the listener alive so a new client can reconnect. Idempotent.
        public void StopPeer()
        {
            try { stream?.Close(); } catch { }
            try { client?.Close(); } catch { }
            stream = null;
            client = null;
            buffer.Reset();
        }

        // Full teardown: close peer and listener. For when leaving local-test mode entirely.
        public void Stop()
        {
            StopPeer();
            try { listener?.Stop(); } catch { }
            listener = null;
        }

        private void FireDisconnect()
        {
            StopPeer();
            if (disconnectedFired) return;
            disconnectedFired = true;
            OnPeerDisconnected?.Invoke();
        }
    }
}
