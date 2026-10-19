using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VTOLVRTacviewSPRecorder.Core;

/// <summary>
/// Tacview Real-Time Telemetry host. Clients (Tacview, or any bridge that
/// speaks the protocol) connect over TCP, complete the handshake, receive a
/// snapshot of the current mission state, then every ACMI frame as it is
/// recorded.
///
/// <para>Handshake, each line terminated by <c>\n</c> and the block by <c>\0</c>:
/// host sends <c>XtraLib.Stream.0</c>, <c>Tacview.RealTimeTelemetry.0</c> and its
/// name; the client answers the same two lines, its user name and the password
/// hash (<see cref="TacviewPasswordHash"/>).</para>
///
/// <para>Threading: <see cref="Publish"/> is called from Unity's main thread and
/// never blocks on the network. Accepting, handshakes and socket writes run on
/// background threads; a client that falls too far behind is disconnected.</para>
/// </summary>
public sealed class TacviewRealtimeServer : IDisposable
{
    public const int DefaultPort = 42674;
    private const string StreamHeader = "XtraLib.Stream.0";
    private const string TelemetryHeader = "Tacview.RealTimeTelemetry.0";
    private const int HandshakeTimeoutMilliseconds = 5000;
    private const int MaxHandshakeBytes = 4096;

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly object _gate = new object();
    private readonly TacviewRealtimeMirror _mirror = new TacviewRealtimeMirror();
    private readonly List<Client> _clients = new List<Client>();
    private readonly string _passwordHash64;
    private readonly string _passwordHash32;
    private readonly string _hostName;
    private readonly int _maxQueuedChunks;
    private readonly Action<string> _log;
    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _stopping;

    public TacviewRealtimeServer(TacviewRealtimeSettings settings, Action<string> log = null)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        Settings = settings;
        _passwordHash64 = TacviewPasswordHash.Crc64(settings.Password);
        _passwordHash32 = TacviewPasswordHash.Crc32(settings.Password);
        _hostName = string.IsNullOrWhiteSpace(settings.HostName) ? "VTOL VR" : settings.HostName.Trim();
        _maxQueuedChunks = Math.Max(16, settings.MaxQueuedFrames);
        _log = log ?? (_ => { });
    }

    public TacviewRealtimeSettings Settings { get; }

    /// <summary>Port actually listened on (useful when the setting is 0).</summary>
    public int Port { get; private set; }

    public int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    public void Start()
    {
        if (_listener != null)
        {
            return;
        }

        IPAddress address = ParseBindAddress(Settings.BindAddress);
        _listener = new TcpListener(address, Settings.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "Tacview real-time accept"
        };
        _acceptThread.Start();
        _log($"Tacview real-time telemetry listening on {address}:{Port}.");
    }

    /// <summary>
    /// Forwards ACMI text to every live client and to the snapshot mirror.
    /// A new <c>FileType=</c> header starts a new session.
    /// </summary>
    public void Publish(string text)
    {
        if (string.IsNullOrEmpty(text) || _stopping)
        {
            return;
        }

        byte[] bytes = Utf8.GetBytes(text);
        lock (_gate)
        {
            _mirror.Apply(text);
            for (int index = _clients.Count - 1; index >= 0; index--)
            {
                Client client = _clients[index];
                if (!client.Enqueue(bytes, _maxQueuedChunks))
                {
                    _clients.RemoveAt(index);
                    _log($"Tacview real-time client {client.Name} disconnected: it fell too far behind.");
                    client.Close();
                }
            }
        }
    }

    /// <summary>Forgets the mission state; connected clients stay connected.</summary>
    public void EndSession()
    {
        lock (_gate)
        {
            _mirror.Reset();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        lock (_gate)
        {
            for (int index = 0; index < _clients.Count; index++)
            {
                _clients[index].Close();
            }

            _clients.Clear();
        }

        _acceptThread?.Join(1000);
        _listener = null;
    }

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient tcp;
            try
            {
                tcp = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                if (_stopping)
                {
                    return;
                }

                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var handshake = new Thread(() => Handshake(tcp))
            {
                IsBackground = true,
                Name = "Tacview real-time handshake"
            };
            handshake.Start();
        }
    }

    private void Handshake(TcpClient tcp)
    {
        string remote = SafeRemote(tcp);
        try
        {
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();
            stream.ReadTimeout = HandshakeTimeoutMilliseconds;
            byte[] hello = Utf8.GetBytes($"{StreamHeader}\n{TelemetryHeader}\n{_hostName}\n\0");
            stream.Write(hello, 0, hello.Length);

            string reply = ReadUntilNull(stream);
            string[] lines = reply.Split('\n');
            if (lines.Length < 4 || lines[0] != StreamHeader || lines[1] != TelemetryHeader)
            {
                _log($"Tacview real-time client {remote} refused: unexpected handshake.");
                tcp.Close();
                return;
            }

            string hash = lines[3].Trim().ToLowerInvariant();
            if (hash != _passwordHash64 && hash != _passwordHash32)
            {
                _log($"Tacview real-time client {remote} refused: wrong password.");
                tcp.Close();
                return;
            }

            stream.ReadTimeout = Timeout.Infinite;
            string name = string.IsNullOrWhiteSpace(lines[2]) ? remote : $"{lines[2].Trim()} ({remote})";
            var client = new Client(tcp, name);
            lock (_gate)
            {
                if (_stopping)
                {
                    client.Close();
                    return;
                }

                if (_clients.Count >= Math.Max(1, Settings.MaxClients))
                {
                    _log($"Tacview real-time client {name} refused: {Settings.MaxClients} client(s) already connected.");
                    client.Close();
                    return;
                }

                // Snapshot and registration under the same lock: no frame can be
                // missed or duplicated between the two.
                string snapshot = _mirror.RenderSnapshot();
                if (snapshot.Length > 0)
                {
                    client.Enqueue(Utf8.GetBytes(snapshot), int.MaxValue);
                }

                _clients.Add(client);
            }

            client.StartSending(OnClientFailed);
            _log($"Tacview real-time client {name} connected.");
        }
        catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException)
        {
            _log($"Tacview real-time client {remote} handshake failed: {exception.Message}");
            tcp.Close();
        }
    }

    private void OnClientFailed(Client client, string reason)
    {
        bool removed;
        lock (_gate)
        {
            removed = _clients.Remove(client);
        }

        client.Close();
        if (removed && !_stopping)
        {
            _log($"Tacview real-time client {client.Name} disconnected: {reason}");
        }
    }

    private static string ReadUntilNull(Stream stream)
    {
        var bytes = new List<byte>(128);
        while (bytes.Count < MaxHandshakeBytes)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new IOException("connection closed during handshake");
            }

            if (value == 0)
            {
                return Utf8.GetString(bytes.ToArray()).Replace("\r", string.Empty);
            }

            bytes.Add((byte)value);
        }

        throw new IOException("handshake too long");
    }

    private static IPAddress ParseBindAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "127.0.0.1" || value.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (value.Trim() == "0.0.0.0" || value.Trim() == "*")
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(value.Trim(), out IPAddress address))
        {
            return address;
        }

        throw new FormatException($"Invalid real-time bind address '{value}'.");
    }

    private static string SafeRemote(TcpClient tcp)
    {
        try
        {
            return tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        }
        catch (ObjectDisposedException)
        {
            return "?";
        }
    }

    private sealed class Client
    {
        private readonly TcpClient _tcp;
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly object _queueGate = new object();
        private bool _closed;

        public Client(TcpClient tcp, string name)
        {
            _tcp = tcp;
            Name = name;
        }

        public string Name { get; }

        /// <summary>Queues a chunk; false when the client is too far behind.</summary>
        public bool Enqueue(byte[] chunk, int maxQueued)
        {
            lock (_queueGate)
            {
                if (_closed)
                {
                    return false;
                }

                if (_queue.Count >= maxQueued)
                {
                    return false;
                }

                _queue.Enqueue(chunk);
                Monitor.Pulse(_queueGate);
                return true;
            }
        }

        public void StartSending(Action<Client, string> onFailed)
        {
            var thread = new Thread(() => SendLoop(onFailed))
            {
                IsBackground = true,
                Name = "Tacview real-time sender"
            };
            thread.Start();
        }

        public void Close()
        {
            lock (_queueGate)
            {
                _closed = true;
                _queue.Clear();
                Monitor.PulseAll(_queueGate);
            }

            try
            {
                _tcp.Close();
            }
            catch (SocketException)
            {
            }
        }

        private void SendLoop(Action<Client, string> onFailed)
        {
            try
            {
                NetworkStream stream = _tcp.GetStream();
                while (true)
                {
                    byte[] chunk;
                    lock (_queueGate)
                    {
                        while (_queue.Count == 0 && !_closed)
                        {
                            Monitor.Wait(_queueGate);
                        }

                        if (_closed)
                        {
                            return;
                        }

                        chunk = _queue.Dequeue();
                    }

                    stream.Write(chunk, 0, chunk.Length);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException || exception is InvalidOperationException)
            {
                onFailed(this, exception.Message);
            }
        }
    }
}

/// <summary>Password hashes accepted by Tacview Real-Time Telemetry.</summary>
public static class TacviewPasswordHash
{
    private const ulong Crc64Polynomial = 0x42F0E1EBA9EA3693UL;
    private static readonly ulong[] Crc64Table = BuildCrc64Table();
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Official hash: CRC-64/WE (ECMA-182, not reflected, init and final XOR
    /// all ones) of the UTF-16LE password, lowercase hex; "0" for no password.
    /// </summary>
    public static string Crc64(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "0";
        }

        ulong crc = ulong.MaxValue;
        foreach (byte value in Encoding.Unicode.GetBytes(password))
        {
            crc = Crc64Table[(int)(((crc >> 56) ^ value) & 0xFF)] ^ (crc << 8);
        }

        return (crc ^ ulong.MaxValue).ToString("x", CultureInfo.InvariantCulture);
    }

    /// <summary>Legacy hash (DCS2ACMI): CRC-32 IEEE of the UTF-16LE password.</summary>
    public static string Crc32(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "0";
        }

        uint crc = uint.MaxValue;
        foreach (byte value in Encoding.Unicode.GetBytes(password))
        {
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return (crc ^ uint.MaxValue).ToString("x", CultureInfo.InvariantCulture);
    }

    private static ulong[] BuildCrc64Table()
    {
        var table = new ulong[256];
        for (int index = 0; index < 256; index++)
        {
            ulong crc = (ulong)index << 56;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000000000000000UL) != 0 ? (crc << 1) ^ Crc64Polynomial : crc << 1;
            }

            table[index] = crc;
        }

        return table;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            uint crc = index;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
            }

            table[index] = crc;
        }

        return table;
    }
}
