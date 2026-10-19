using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VTOLVRTacviewSPRecorder.Core;
using Xunit;

namespace VTOLVRTacviewSPRecorder.Core.Tests;

public sealed class RealtimeCoreTests
{
    private static readonly DateTimeOffset ReferenceTime = DateTimeOffset.Parse("2026-10-19T09:30:00Z");

    [Theory]
    [InlineData("", "0")]
    [InlineData("pass", "5e1e445fd60ac2e0")]
    [InlineData("password", "3c0e55f1cfff14c4")]
    [InlineData("abc", "fc99a9ae7dfa5bfc")]
    [InlineData("abc123", "2bd464b05d7103f1")]
    [InlineData("12345", "6b40207b495297f4")]
    public void Crc64PasswordHashMatchesReferenceVectors(string password, string expected)
    {
        Assert.Equal(expected, TacviewPasswordHash.Crc64(password));
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("pass", "7742b741")]
    [InlineData("password", "f335183e")]
    [InlineData("12345", "fcc50d33")]
    public void Crc32PasswordHashMatchesReferenceVectors(string password, string expected)
    {
        Assert.Equal(expected, TacviewPasswordHash.Crc32(password));
    }

    [Fact]
    public void SettingsParseValuesAndReportInvalidLines()
    {
        var warnings = new List<string>();
        TacviewRealtimeSettings settings = TacviewRealtimeSettings.Parse(
            "# comment\nenabled=no\nbind=0.0.0.0\nport=5000\npassword=s3cret\nmax_clients=2\nport=99999\nfoo=bar\nnot a pair\n",
            warnings);

        Assert.False(settings.Enabled);
        Assert.Equal("0.0.0.0", settings.BindAddress);
        Assert.Equal(5000, settings.Port);
        Assert.Equal("s3cret", settings.Password);
        Assert.Equal(2, settings.MaxClients);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void DefaultSettingsListenOnLoopbackOnly()
    {
        TacviewRealtimeSettings settings = TacviewRealtimeSettings.Parse(TacviewRealtimeSettings.DefaultFileContent, new List<string>());

        Assert.True(settings.Enabled);
        Assert.Equal("127.0.0.1", settings.BindAddress);
        Assert.Equal(TacviewRealtimeServer.DefaultPort, settings.Port);
        Assert.Equal(string.Empty, settings.Password);
    }

    [Fact]
    public void MirrorSnapshotAccumulatesDeltaEncodedFrames()
    {
        var text = new StringWriter();
        using (var writer = new TacviewEntityWriter(text, "Mirror", ReferenceTime, leaveOpen: true))
        {
            writer.WriteFrame(0.0, Updates((0x100, 7.0, 51.0), (0x200, 7.5, 51.5)), NoEvents());
            writer.WriteFrame(0.5, Updates((0x100, 7.1, 51.0)), NoEvents());
            writer.WriteFrame(1.0, new List<TacviewEntityUpdate>
            {
                TacviewEntityUpdate.Delete(0x200, TacviewEntityRemovalReason.Destroyed, "Bandit")
            }, NoEvents());
        }

        var mirror = new TacviewRealtimeMirror();
        mirror.Apply(text.ToString());
        string snapshot = mirror.RenderSnapshot();

        Assert.StartsWith("FileType=text/acmi/tacview\nFileVersion=2.2\n0,ReferenceTime=", snapshot);
        Assert.Contains("0,Title=Mirror\n", snapshot);
        Assert.Contains("#1.000\n", snapshot);
        // The latitude was omitted from the second sample; the snapshot restores it.
        Assert.Contains("100,T=7.1000000|51.0000000|1000.00|0.00|0.00|0.00|0.00|0.00|0.00,Type=Air+FixedWing", snapshot);
        Assert.DoesNotContain("\n200,", snapshot);
        Assert.DoesNotContain("Event=", snapshot);
        Assert.Equal(1, mirror.ObjectCount);
    }

    [Fact]
    public void MirrorHandlesSplitChunksEscapedCommasAndNewSessions()
    {
        var mirror = new TacviewRealtimeMirror();
        mirror.Apply("FileType=text/acmi/tacview\nFileVersion=2.2\n0,Briefing=a\\, b\n#0\nA,T=1|2|3,Na");
        mirror.Apply("me=X\\,Y\n");
        Assert.Contains("0,Briefing=a\\, b\n", mirror.RenderSnapshot());
        Assert.Contains("A,T=1|2|3,Name=X\\,Y\n", mirror.RenderSnapshot());

        mirror.Apply("FileType=text/acmi/tacview\nFileVersion=2.2\n");
        Assert.Equal(0, mirror.ObjectCount);
        Assert.Equal("FileType=text/acmi/tacview\nFileVersion=2.2\n", mirror.RenderSnapshot());
    }

    [Fact]
    public void CreateFileTeesEveryFlushedFrameToTheRealtimeSink()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tacview-rt-" + Guid.NewGuid().ToString("N"));
        var received = new StringBuilder();
        try
        {
            string finalPath;
            using (TacviewEntityWriter writer = TacviewEntityWriter.CreateFile(
                directory,
                "Tee",
                ReferenceTime,
                null,
                chunk => received.Append(chunk),
                null))
            {
                writer.WriteFrame(0.0, Updates((0x100, 7.0, 51.0)), NoEvents());
                writer.WriteFrame(0.025, Updates((0x100, 7.2, 51.0)), NoEvents());
                finalPath = writer.FinalPath;
            }

            string file = File.ReadAllText(finalPath);
            Assert.Equal(file, received.ToString());
            Assert.Contains("#0.025\n100,T=7.2000000", received.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void SinkFailureDoesNotInterruptFileRecording()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tacview-rt-" + Guid.NewGuid().ToString("N"));
        int errors = 0;
        try
        {
            string finalPath;
            using (TacviewEntityWriter writer = TacviewEntityWriter.CreateFile(
                directory,
                "Failing sink",
                ReferenceTime,
                null,
                _ => throw new InvalidOperationException("boom"),
                _ => errors++))
            {
                writer.WriteFrame(0.0, Updates((0x100, 7.0, 51.0)), NoEvents());
                writer.WriteFrame(0.5, Updates((0x100, 7.3, 51.0)), NoEvents());
                finalPath = writer.FinalPath;
            }

            Assert.Equal(1, errors);
            Assert.Contains("100,T=7.3000000", File.ReadAllText(finalPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void ServerSendsSnapshotThenLiveFramesAfterHandshake()
    {
        using var server = StartServer("vtol");
        server.Publish("FileType=text/acmi/tacview\nFileVersion=2.2\n0,Title=Live\n#0.000\n100,T=7|51|1000,Name=F/A-26B\n");

        using var client = Connect(server.Port, TacviewPasswordHash.Crc64("vtol"), out string greeting);
        Assert.Equal("XtraLib.Stream.0\nTacview.RealTimeTelemetry.0\nTest host\n", greeting);
        Assert.Contains("100,T=7|51|1000,Name=F/A-26B\n", ReadUntil(client, "Name=F/A-26B\n"));

        WaitFor(() => server.ClientCount == 1);
        server.Publish("#0.025\n100,T=7.1||\n");
        Assert.Contains("#0.025\n100,T=7.1||\n", ReadUntil(client, "7.1||\n"));
    }

    [Fact]
    public void ServerAcceptsLegacyCrc32PasswordHash()
    {
        using var server = StartServer("vtol");
        server.Publish("FileType=text/acmi/tacview\nFileVersion=2.2\n");

        using var client = Connect(server.Port, TacviewPasswordHash.Crc32("vtol"), out _);
        Assert.Contains("FileVersion=2.2\n", ReadUntil(client, "FileVersion=2.2\n"));
    }

    [Fact]
    public void ServerRefusesWrongPassword()
    {
        using var server = StartServer("vtol");
        server.Publish("FileType=text/acmi/tacview\nFileVersion=2.2\n");

        using var client = Connect(server.Port, TacviewPasswordHash.Crc64("wrong"), out _);
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 5000;
        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
        Assert.Equal(0, server.ClientCount);
    }

    private static TacviewRealtimeServer StartServer(string password)
    {
        var server = new TacviewRealtimeServer(new TacviewRealtimeSettings
        {
            Port = 0,
            Password = password,
            HostName = "Test host"
        });
        server.Start();
        return server;
    }

    private static TcpClient Connect(int port, string hash, out string greeting)
    {
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 5000;
        var bytes = new List<byte>();
        int value;
        while ((value = stream.ReadByte()) > 0)
        {
            bytes.Add((byte)value);
        }

        greeting = Encoding.UTF8.GetString(bytes.ToArray());
        byte[] reply = Encoding.UTF8.GetBytes($"XtraLib.Stream.0\nTacview.RealTimeTelemetry.0\ntest-client\n{hash}\0");
        stream.Write(reply, 0, reply.Length);
        return client;
    }

    private static string ReadUntil(TcpClient client, string marker)
    {
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 5000;
        var received = new StringBuilder();
        var buffer = new byte[4096];
        while (!received.ToString().Contains(marker))
        {
            int count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                break;
            }

            received.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }

        return received.ToString();
    }

    private static void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            Thread.Sleep(10);
        }
    }

    private static List<TacviewEntityUpdate> Updates(params (ulong Id, double Longitude, double Latitude)[] objects)
    {
        var updates = new List<TacviewEntityUpdate>();
        foreach ((ulong id, double longitude, double latitude) in objects)
        {
            updates.Add(TacviewEntityUpdate.Upsert(
                id,
                new EntityTransform(longitude, latitude, 1000.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
                new EntityMetadata("Air+FixedWing", "F/A-26B", "Allies", "Blue")));
        }

        return updates;
    }

    private static List<TacviewEvent> NoEvents()
    {
        return new List<TacviewEvent>();
    }
}
