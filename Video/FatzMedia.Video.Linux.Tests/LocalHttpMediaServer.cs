using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>How the server answers one request (Stage 039A native tests).</summary>
/// <param name="DelayBeforeResponse">Wait before sending anything.</param>
/// <param name="TruncateAfterBytes">Send at most this many body bytes, then close the connection.</param>
/// <param name="Refuse">Close the connection without answering, as a lost network does.</param>
/// <param name="BytesPerSecond">Send the body at about this rate (0 = as fast as possible).</param>
sealed record HttpResponsePlan(TimeSpan DelayBeforeResponse = default, long? TruncateAfterBytes = null, bool Refuse = false, long BytesPerSecond = 0);

/// <summary>One request the server received.</summary>
sealed record HttpRequestRecord(int Number, string Path, long? RangeStart);

/// <summary>
/// A tiny in-process HTTP/1.1 file server on 127.0.0.1 for the network video tests: serves files
/// from a folder with byte ranges (<c>Accept-Ranges: bytes</c>, 206 responses) and lets a test
/// delay a response or cut it short to simulate a stalled or dropped server. Text files can be
/// overridden (a live HLS playlist). With a certificate it serves https (ENG-034). Byte ranges can
/// be turned off (<see cref="SupportsRanges"/>, like <c>python -m http.server</c>), and a client
/// that asks for ICY metadata gets an Icecast-style live stream (<see cref="IcyTitle"/>).
/// </summary>
sealed class LocalHttpMediaServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly string _root;
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<string, byte[]> _overrides = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<HttpRequestRecord> _requests = new();
    readonly ConcurrentBag<TcpClient> _clients = [];
    readonly X509Certificate2? _certificate;
    int _requestCount;
    int _handshakes;

    public LocalHttpMediaServer(string root, Func<HttpRequestRecord, HttpResponsePlan>? plan = null, X509Certificate2? certificate = null)
    {
        _root = root;
        _certificate = certificate;
        Plan = plan ?? (_ => new HttpResponsePlan());
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public Func<HttpRequestRecord, HttpResponsePlan> Plan { get; set; }

    /// <summary>Whether byte ranges are served (default). Without them every response is the whole file, with no <c>Accept-Ranges</c>.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>
    /// The ICY title of each metadata block (by block number), or null for no ICY support. A
    /// request with <c>Icy-MetaData: 1</c> then gets a live stream: no length, no ranges,
    /// <c>icy-metaint: </c><see cref="IcyMetaInterval"/>, and a metadata block after every interval.
    /// </summary>
    public Func<int, string>? IcyTitle { get; set; }

    /// <summary>The audio bytes between two ICY metadata blocks.</summary>
    public int IcyMetaInterval { get; set; } = 16000;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyCollection<HttpRequestRecord> Requests => [.. _requests];

    /// <summary>The number of TLS handshakes started (https only), accepted or not.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    public Uri UriFor(string relativePath) => new($"{(_certificate is null ? "http" : "https")}://127.0.0.1:{Port}/{relativePath.TrimStart('/')}");

    /// <summary>Resets every open connection, as a lost network does to a download in progress.</summary>
    public void DropConnections()
    {
        foreach (var client in _clients)
        {
            try
            {
                client.Client?.Close(0);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Serves <paramref name="content"/> for <paramref name="relativePath"/> instead of the file.</summary>
    public void Override(string relativePath, string content) => _overrides["/" + relativePath.TrimStart('/')] = Encoding.UTF8.GetBytes(content);

    async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _clients.Add(client);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = _certificate is null ? null : new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                var stream = tls ?? (Stream)client.GetStream();
                if (tls is not null)
                {
                    // A client that rejects the certificate aborts the handshake; no request is recorded.
                    _ = Interlocked.Increment(ref _handshakes);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token).ConfigureAwait(false);
                }

                var header = await ReadHeaderAsync(stream).ConfigureAwait(false);
                if (header is null)
                    return;

                var lines = header.Split("\r\n");
                var parts = lines[0].Split(' ');
                var path = Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Split('?')[0] : "/");
                long? rangeStart = null;
                var wantsIcy = false;
                foreach (var line in lines.Skip(1))
                {
                    if (line.StartsWith("Icy-MetaData:", StringComparison.OrdinalIgnoreCase) && line["Icy-MetaData:".Length..].Trim() == "1")
                        wantsIcy = true;

                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line["Range:".Length..].Trim();
                        if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                            && long.TryParse(value["bytes=".Length..].Split('-')[0], out var requestedStart))
                            rangeStart = requestedStart;
                    }
                }

                var icyTitle = wantsIcy ? IcyTitle : null;
                if (!SupportsRanges || icyTitle is not null)
                    rangeStart = null;
                var record = new HttpRequestRecord(Interlocked.Increment(ref _requestCount), path, rangeStart);
                _requests.Enqueue(record);
                var plan = Plan(record);
                if (plan.Refuse)
                {
                    client.Client.Close(0);
                    return;
                }

                if (plan.DelayBeforeResponse > TimeSpan.Zero)
                    await Task.Delay(plan.DelayBeforeResponse, _stop.Token).ConfigureAwait(false);

                var body = _overrides.TryGetValue(path, out var overridden) ? overridden : null;
                var file = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/')));
                if (body is null && (!file.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal) || !File.Exists(file)))
                {
                    await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                var length = body?.LongLength ?? new FileInfo(file).Length;
                var start = Math.Clamp(rangeStart ?? 0, 0, length);
                var count = length - start;
                var contentType = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "application/vnd.apple.mpegurl"
                    : path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "video/mp2t"
                    : path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg"
                    : path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? "audio/ogg"
                    : "video/mp4";
                var status = rangeStart is null ? "200 OK" : "206 Partial Content";
                var head = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status}\r\n")
                    .Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n");
                if (icyTitle is not null)
                    head.Append("icy-name: FatzBomb test radio\r\n").Append(CultureInfo.InvariantCulture, $"icy-metaint: {IcyMetaInterval}\r\n");
                else
                    head.Append(CultureInfo.InvariantCulture, $"Content-Length: {count}\r\n");
                if (SupportsRanges && icyTitle is null)
                    head.Append("Accept-Ranges: bytes\r\n");
                if (rangeStart is not null)
                    head.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{length - 1}/{length}\r\n");
                head.Append("Connection: close\r\n\r\n");
                await WriteAsync(stream, head.ToString()).ConfigureAwait(false);
                if (icyTitle is not null)
                    stream = new IcyWriteStream(stream, IcyMetaInterval, icyTitle);

                var limit = plan.TruncateAfterBytes is { } truncate ? Math.Min(count, truncate) : count;
                if (body is not null)
                {
                    await stream.WriteAsync(body.AsMemory((int)start, (int)limit), _stop.Token).ConfigureAwait(false);
                }
                else
                {
                    await using var source = File.OpenRead(file);
                    source.Position = start;
                    // A throttled body goes out in twentieths of a second, not in bursts.
                    var buffer = new byte[plan.BytesPerSecond > 0 ? (int)Math.Clamp(plan.BytesPerSecond / 20, 1024, 64 * 1024) : 64 * 1024];
                    var remaining = limit;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _stop.Token).ConfigureAwait(false);
                        if (read <= 0)
                            break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
                        remaining -= read;
                        if (plan.BytesPerSecond > 0)
                            await Task.Delay(TimeSpan.FromSeconds(read / (double)plan.BytesPerSecond), _stop.Token).ConfigureAwait(false);
                    }
                }

                if (limit < count)
                {
                    // End the connection mid-body with a clean close: the client reads what was
                    // sent, then finds the body short of its Content-Length.
                    client.Client.Shutdown(SocketShutdown.Send);
                }
            }
            catch
            {
                // The client went away or the server is stopping.
            }
        }
    }

    /// <summary>Inserts an ICY metadata block after every <c>interval</c> body bytes (Shoutcast/Icecast framing).</summary>
    sealed class IcyWriteStream(Stream inner, int interval, Func<int, string> title) : Stream
    {
        int _sinceBlock;
        int _blocks;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                var take = Math.Min(buffer.Length, interval - _sinceBlock);
                await inner.WriteAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
                buffer = buffer[take..];
                _sinceBlock += take;
                if (_sinceBlock < interval)
                    continue;

                _sinceBlock = 0;
                var text = Encoding.UTF8.GetBytes($"StreamTitle='{title(_blocks++)}';");
                var blocks = (text.Length + 15) / 16;
                var block = new byte[1 + (blocks * 16)];
                block[0] = (byte)blocks;
                text.CopyTo(block, 1);
                await inner.WriteAsync(block, cancellationToken).ConfigureAwait(false);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    static async Task<string?> ReadHeaderAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total)).ConfigureAwait(false);
            if (read <= 0)
                return null;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
                return text[..end];
        }

        return null;
    }

    Task WriteAsync(Stream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).AsTask();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _clients)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }

        _stop.Dispose();
    }
}

/// <summary>
/// A self-signed server certificate for 127.0.0.1 and localhost, made in process (ENG-034 tests),
/// with its PEM written to a temporary file for <see cref="NetworkTlsPolicy.CertificateAuthorityFile"/>.
/// </summary>
sealed class SelfSignedCertificate : IDisposable
{
    SelfSignedCertificate(X509Certificate2 certificate, string pemPath)
    {
        Certificate = certificate;
        PemPath = pemPath;
    }

    public X509Certificate2 Certificate { get; }

    public string PemPath { get; }

    public static SelfSignedCertificate Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var pemPath = Path.Combine(Directory.CreateTempSubdirectory("fbm-tls-").FullName, "ca.pem");
        File.WriteAllText(pemPath, created.ExportCertificatePem());
        // Reload through PKCS#12 so the private key is usable by SslStream on every platform.
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
        return new SelfSignedCertificate(certificate, pemPath);
    }

    public void Dispose()
    {
        Certificate.Dispose();
        Directory.Delete(Path.GetDirectoryName(PemPath)!, recursive: true);
    }
}

/// <summary>A TCP port that accepts connections and never answers (a hung server).</summary>
sealed class SilentTcpServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly ConcurrentBag<Socket> _accepted = [];
    readonly CancellationTokenSource _stop = new();

    public SilentTcpServer()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    _accepted.Add(await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false));
                }
                catch
                {
                    return;
                }
            }
        });
    }

    public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/hung.mp4");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var socket in _accepted)
            socket.Dispose();
        _stop.Dispose();
    }
}
