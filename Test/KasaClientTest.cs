using System.Net;
using System.Net.Sockets;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using Kasa;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Test;

public class KasaClientTest {

    private readonly KasaClient _client = new TestableKasaClient("0.0.0.0"); //allows RetryConnect() to fail faster than if this is 127.0.0.1

    public KasaClientTest() {
        _client.Options.MaxAttempts = 1;
    }

    [Fact]
    public void Hostname() {
        _client.Hostname.Should().Be("0.0.0.0");
    }

    [Fact]
    public void Cipher() {
        byte[] cleartext = Encoding.UTF8.GetBytes(@"{""system"":{""get_sysinfo"":null}}");
        byte[] expected  = Convert.FromBase64String("eyJzedisyaSGvMflgueTzL/Gtdyy1LuZo8241LjFuA==");
        byte[] actual    = KasaClient.Cipher(cleartext);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Decipher() {
        byte[] ciphertext = Encoding.UTF8.GetBytes(@"{""system"":{""set_led_off"":{""err_code"":0}}}");
        byte[] expected   = Convert.FromBase64String("eyJzedgHEQhPGEFZURYRKzMJATswCQBEGEFZRxcALTwMCwFHGApNAAA=");
        byte[] actual     = KasaClient.Decipher(ciphertext);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Serialize() {
        byte[] actual   = _client.Serialize(new JObject(new JProperty("system", new JObject(new JProperty("get_sysinfo", (object?) null)))), 1);
        byte[] expected = Convert.FromBase64String("AAAAH9DygfiL/5r31e+UttG0wJ/sleaP4YfoyvCe64frlus=");
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Deserialize() {
        IEnumerable<byte> responseBytes = Convert.FromBase64String("AAAAKdDygfiL/5r31e+UtsWg1Ivngua51rDW9M61l/KA8q3OocWggriI9Yj1");
        JObject           actual        = _client.Deserialize<JObject>(responseBytes, 1, CommandFamily.System, "set_led_off");
        actual.Should().BeEquivalentTo(JObject.Parse(@"{""err_code"":0}"));
    }

    [Fact]
    public void DeserializeInvalidJson() {
        byte[] responseBytes = { 0x00, 0x00, 0x00, 0x01, 0x00 };
        Action thrower       = () => _client.Deserialize<JObject>(responseBytes, 1, CommandFamily.System, "test");
        thrower.Should().Throw<ResponseParsingException>();
    }

    [Fact]
    public void DeserializeMissingFeature() {
        IEnumerable<byte> responseBytes = Convert.FromBase64String("AAAAOdDyl/qf64783uSfvdiq2Ifki++KqJK/jqKA5Zflutekw+Hb+ZT7n+qG48OtwraW5ZDgkP+N+dum2w==");
        Action            thrower       = () => _client.Deserialize<JObject>(responseBytes, 1, CommandFamily.EnergyMeter, "get_realtime");
        thrower.Should().Throw<FeatureUnavailable>();
    }

    [Fact]
    public async Task Send() {
        Stream fakeStream = await _client.GetNetworkStream();

        A.CallTo(() => fakeStream.ReadAsync(A<byte[]>._, 0, 4, A<CancellationToken>._)).ReturnsLazily((byte[] destination, int offset, int length, CancellationToken _) => {
            Array.Copy(new byte[] { 0x00, 0x00, 0x00, 0x29 }, 0, destination, offset, length);
            return Task.FromResult(length);
        });

        byte[] responseAfterHeader = Convert.FromBase64String("0PKB+Iv/mvfV75S2xaDUi+eC5rnWsNb0zrWX8oDyrc6hxaCCuIj1iPU=");
        A.CallTo(() => fakeStream.ReadAsync(A<byte[]>._, 0, 0x29, A<CancellationToken>._)).ReturnsLazily((byte[] destination, int offset, int length, CancellationToken _) => {
            Array.Copy(responseAfterHeader, 0, destination, offset, length);
            return Task.FromResult(length);
        });

        JObject actual = await _client.Send<JObject>(CommandFamily.System, "set_led_off", new { off = 0 });

        byte[] expectedRequest = Convert.FromBase64String("AAAAJNDygfiL/5r31e+UtsWg1Ivngua51rDW9M61l/ie+Nrg0K3QrQ==");
        A.CallTo(() => fakeStream.WriteAsync(A<byte[]>.That.IsSameSequenceAs(expectedRequest), 0, expectedRequest.Length, A<CancellationToken>._)).MustHaveHappened();

        actual.Should().BeEquivalentTo(JObject.Parse(@"{""err_code"":0}"));
    }

    [Fact]
    public async Task SendHeaderTooShort() {
        Stream fakeStream = await _client.GetNetworkStream();
        A.CallTo(() => fakeStream.ReadAsync(A<byte[]>._, 0, 4, A<CancellationToken>._)).ReturnsLazily((byte[] destination, int offset, int length, CancellationToken _) => {
            Array.Copy(new byte[] { 0x00, 0x00, 0x00 }, 0, destination, offset, 3);
            return Task.FromResult(3);
        });

        Func<Task> thrower = async () => await _client.Send<JObject>(CommandFamily.System, "set_led_off", new { off = 0 });
        await thrower.Should().ThrowAsync<NetworkException>();
    }

    [Fact]
    public async Task SendResponsePayloadTooShort() {
        Stream fakeStream = await _client.GetNetworkStream();
        A.CallTo(() => fakeStream.ReadAsync(A<byte[]>._, 0, 4, A<CancellationToken>._)).ReturnsLazily((byte[] destination, int offset, int length, CancellationToken _) => {
            Array.Copy(new byte[] { 0x00, 0x00, 0x00, 0x29 }, 0, destination, offset, length);
            return Task.FromResult(length);
        });

        byte[] responseAfterHeader = { 0x00 };
        A.CallTo(() => fakeStream.ReadAsync(A<byte[]>._, 0, 0x29, A<CancellationToken>._)).ReturnsLazily((byte[] destination, int offset, int length, CancellationToken _) => {
            Array.Copy(responseAfterHeader, 0, destination, offset, 1);
            return Task.FromResult(1);
        });

        Func<Task> thrower = async () => await _client.Send<JObject>(CommandFamily.System, "set_led_off", new { off = 0 });
        await thrower.Should().ThrowAsync<NetworkException>();
    }

    [Fact]
    public async Task EnsureConnected() {
        (TcpListener server, ushort _, Wrapper<TcpClient?> serverSocket, KasaClient kasaClient) = StartTestServer();

        await kasaClient.EnsureConnected();
        await Task.Delay(100);
        kasaClient.Connected.Should().BeTrue();
        serverSocket.Value.Should().NotBeNull();
        serverSocket.Value!.Client.Connected.Should().BeTrue();
        Stream networkStream = await kasaClient.GetNetworkStream();
        networkStream.Should().NotBeNull().And.BeOfType<NetworkStream>();

        kasaClient.Dispose();
        kasaClient.Connected.Should().BeFalse();

        serverSocket.Value.Close();
        server.Stop();
    }

    [Fact]
    public async Task Reconnect() {
        (TcpListener server, ushort _, Wrapper<TcpClient?> serverSocket, KasaClient kasaClient) = StartTestServer();

        await kasaClient.Connect();
        await Task.Delay(100);
        kasaClient.Connected.Should().BeTrue();
        serverSocket.Value.Should().NotBeNull();
        serverSocket.Value!.Client.Connected.Should().BeTrue();
        Stream networkStream = await kasaClient.GetNetworkStream();
        networkStream.Should().NotBeNull().And.BeOfType<NetworkStream>();

        // This doesn't actually exercise the Socket.Disconnect() call in EnsureConnected(). Disconnect() is required, because it fails in real-life scenarios, but I can't synthesize it with my in-process test TCP server. Maybe .NET's Socket implementation is always polite enough to send FIN on shutdown, but the Kasa TCP server just crashes itself on reboot?
        serverSocket.Value.Client.Close();

        await kasaClient.EnsureConnected();
        await Task.Delay(100);
        kasaClient.Connected.Should().BeTrue();

        kasaClient.Dispose();
        kasaClient.Connected.Should().BeFalse();

        serverSocket.Value.Close();
        server.Stop();
    }

    [Fact]
    public void ConnectFailsIfAlreadyDisposed() {
        KasaClient kasaClient = new("localhost");
        kasaClient.Dispose();
        Func<Task> connect = () => kasaClient.Connect();
        connect.Should().ThrowExactlyAsync<ObjectDisposedException>();
    }

    [Fact]
    public void AutoconnectFailsIfAlreadyDisposed() {
        KasaClient kasaClient = new("localhost");
        kasaClient.Dispose();
        Func<Task> thrower = () => kasaClient.EnsureConnected();
        thrower.Should().ThrowExactlyAsync<ObjectDisposedException>();
    }

    [Fact]
    public void EnsureConnectedFailsIfAlreadyDisposed() {
        KasaClient kasaClient = new("localhost");
        kasaClient.Dispose();
        Func<Task> thrower = () => kasaClient.EnsureConnected();
        thrower.Should().ThrowExactlyAsync<ObjectDisposedException>();
    }
    //
    // [Fact]
    // public async Task AutoReconnect() {
    //     (TcpListener server, ushort serverPort, Wrapper<TcpClient?> serverSocket, KasaClient kasaClient) = StartTestServer();
    //     kasaClient.Connected.Should().BeFalse();
    //
    //     await kasaClient.Connect();
    //     await Task.Delay(100);
    //     kasaClient.Connected.Should().BeTrue();
    //     serverSocket.Value.Should().NotBeNull();
    //     serverSocket.Value!.Client.Connected.Should().BeTrue();
    //     Stream networkStream = await kasaClient.GetNetworkStream();
    //     networkStream.Should().NotBeNull().And.BeOfType<NetworkStream>();
    //
    //     await kasaClient.EnsureConnected(true);
    //     await Task.Delay(100);
    //     kasaClient.Connected.Should().BeTrue();
    //     serverSocket.Value.Should().NotBeNull();
    //     serverSocket.Value!.Client.Connected.Should().BeTrue();
    //     networkStream = await kasaClient.GetNetworkStream();
    //     networkStream.Should().NotBeNull().And.BeOfType<NetworkStream>();
    //
    //     serverSocket.Value?.Close();
    //     server.Stop();
    // }

    //
    // [Fact]
    // public void EnsureConnectedIgnoresSocketExceptionWhileDisconnecting() {
    //     KasaClient kasaClient = new("0.0.0.0");
    //     Func<Task> thrower    = () => kasaClient.EnsureConnected(true);
    //     thrower.Should().ThrowExactlyAsync<SocketException>();
    // }

    [Fact]
    public async Task Connect() {
        (TcpListener server, ushort _, Wrapper<TcpClient?> serverSocket, KasaClient kasaClient) = StartTestServer();
        kasaClient.Connected.Should().BeFalse();

        await kasaClient.Connect();
        await Task.Delay(100);
        kasaClient.Connected.Should().BeTrue();
        serverSocket.Value.Should().NotBeNull();
        serverSocket.Value!.Client.Connected.Should().BeTrue();
        Stream networkStream = await kasaClient.GetNetworkStream();
        networkStream.Should().NotBeNull().And.BeOfType<NetworkStream>();

        Func<Task> assertConnected = async () => await kasaClient.EnsureConnected();
        await assertConnected.Should().NotThrowAsync();

        Func<Task> connect = async () => await kasaClient.Connect();
        await connect.Should().NotThrowAsync();

        kasaClient.Dispose();
        kasaClient.Connected.Should().BeFalse();

        serverSocket.Value.Close();
        server.Stop();
    }

    [Fact]
    public async Task RetryConnect() {
        _client.Options = new Options { MaxAttempts = 2, RetryDelay = TimeSpan.Zero, SendTimeout = TimeSpan.Zero, ReceiveTimeout = TimeSpan.Zero };
        Func<Task> thrower = async () => await _client.Connect();
        await thrower.Should().ThrowAsync<NetworkException>();
    }

    [Fact]
    public async Task RetrySend() {
        KasaClient client = new("0.0.0.0") {
            Options = new Options { MaxAttempts = 2, RetryDelay = TimeSpan.Zero, SendTimeout = TimeSpan.Zero, ReceiveTimeout = TimeSpan.Zero }
        };
        Func<Task> thrower = async () => await client.Send<JObject>(CommandFamily.System, "test", parameters: null);
        await thrower.Should().ThrowAsync<NetworkException>();
    }

    private static (TcpListener server, ushort serverPort, Wrapper<TcpClient?> serverSocket, KasaClient kasaClient) StartTestServer(ushort? desiredPort = null) {
        TcpListener? server     = null;
        ushort?      serverPort = desiredPort;
        while (!server?.Server.IsBound ?? true) {
            serverPort ??= (ushort) Random.Shared.Next(1024, 65536);
            server     =   new TcpListener(IPAddress.Loopback, serverPort.Value);
            try {
                server.Start();
            } catch (SocketException e) {
                if (e.SocketErrorCode != SocketError.AccessDenied) { // already in use
                    throw;
                }

                serverPort = null;
            }
        }

        Wrapper<TcpClient?> tcpServerSocket = new();
        server!.AcceptTcpClientAsync().ContinueWith(task => tcpServerSocket.Value = task.Result);
        KasaClient kasaClient = new("localhost") { Port = serverPort!.Value };
        return (server, serverPort.Value, tcpServerSocket, kasaClient);
    }

    [Fact]
    public void IsRetryAllowed() {
        KasaClient.IsRetryAllowed(new ObjectDisposedException(null)).Should().BeFalse();
        KasaClient.IsRetryAllowed(new SocketException((int) SocketError.HostNotFound)).Should().BeFalse();
        KasaClient.IsRetryAllowed(new SocketException((int) SocketError.TimedOut)).Should().BeTrue();
        KasaClient.IsRetryAllowed(new SocketException((int) SocketError.ConnectionRefused)).Should().BeTrue();
        KasaClient.IsRetryAllowed(new IOException(null)).Should().BeTrue();
        KasaClient.IsRetryAllowed(new FeatureUnavailable("method", Feature.EnergyMeter, "host")).Should().BeFalse();
        KasaClient.IsRetryAllowed(new ResponseParsingException("method", "<invalid json>", typeof(JObject), "host", new JsonReaderException())).Should().BeFalse();
    }

}

internal class TestableKasaClient: KasaClient {

    private readonly Stream _networkStream = A.Fake<Stream>();

    public TestableKasaClient(string hostname): base(hostname) { }

    protected internal override Task EnsureConnected(CancellationToken cancellationToken, bool forceReconnect = false) {
        // we're not actually connected during most tests, so don't throw
        return Task.CompletedTask;
    }

    internal override Task<Stream> GetNetworkStream(CancellationToken cancellationToken) {
        return Task.FromResult(_networkStream);
    }

}

internal class Wrapper<T> {

    public T Value { get; set; } = default!;

}