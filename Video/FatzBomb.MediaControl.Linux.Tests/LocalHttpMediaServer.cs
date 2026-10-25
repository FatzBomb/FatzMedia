using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>How the server answers one request (Stage 039A native tests).</summary>
/// <param name="DelayBeforeResponse">Wait before sending anything.</param>
/// <param name="TruncateAfterBytes">Send at most this many body bytes, then close the connection.</param>
internal sealed record HttpResponsePlan(TimeSpan DelayBeforeResponse = default, long? TruncateAfterBytes = null);

/// <summary>One request the server received.</summary>
internal sealed record HttpRequestRecord(int Number, string Path, long? RangeStart);

/// <summary>
/// A tiny in-process HTTP/1.1 file server on 127.0.0.1 for the network video tests: serves files
/// from a folder with byte ranges (<c>Accept-Ranges: bytes</c>, 206 responses) and lets a test
/// delay a response or cut it short to simulate a stalled or dropped server. Text files can be
/// overridden (a live HLS playlist). With a certificate it serves https (ENG-034).
/// </summary>
internal sealed class LocalHttpMediaServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte[]> _overrides = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<HttpRequestRecord> _requests = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly X509Certificate2? _certificate;
    private int _requestCount;
    private int _handshakes;

    public LocalHttpMediaServer(string root, Func<HttpRequestRecord, HttpResponsePlan>? plan = null, X509Certificate2? certificate = null)
    {
        _root = root;
        _certificate = certificate;
        Plan = plan ?? (_ => new HttpResponsePlan());
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public Func<HttpRequestRecord, HttpResponsePlan> Plan { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyCollection<HttpRequestRecord> Requests => [.. _requests];

    /// <summary>The number of TLS handshakes started (https only), accepted or not.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    public Uri UriFor(string relativePath) => new($"{(_certificate is null ? "http" : "https")}://127.0.0.1:{Port}/{relativePath.TrimStart('/')}");

    /// <summary>Serves <paramref name="content"/> for <paramref name="relativePath"/> instead of the file.</summary>
    public void Override(string relativePath, string content) => _overrides["/" + relativePath.TrimStart('/')] = Encoding.UTF8.GetBytes(content);

    private async Task AcceptLoopAsync()
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

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using SslStream? tls = _certificate is null ? null : new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                Stream stream = tls ?? (Stream)client.GetStream();
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
                foreach (var line in lines.Skip(1))
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line["Range:".Length..].Trim();
                        if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                            && long.TryParse(value["bytes=".Length..].Split('-')[0], out var requestedStart))
                            rangeStart = requestedStart;
                    }
                }

                var record = new HttpRequestRecord(Interlocked.Increment(ref _requestCount), path, rangeStart);
                _requests.Enqueue(record);
                HttpResponsePlan plan = Plan(record);
                if (plan.DelayBeforeResponse > TimeSpan.Zero)
                    await Task.Delay(plan.DelayBeforeResponse, _stop.Token).ConfigureAwait(false);

                byte[]? body = _overrides.TryGetValue(path, out var overridden) ? overridden : null;
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
                    : "video/mp4";
                var status = rangeStart is null ? "200 OK" : "206 Partial Content";
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {status}\r\n")
                    .Append($"Content-Type: {contentType}\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append($"Content-Length: {count}\r\n");
                if (rangeStart is not null)
                    head.Append($"Content-Range: bytes {start}-{length - 1}/{length}\r\n");
                head.Append("Connection: close\r\n\r\n");
                await WriteAsync(stream, head.ToString()).ConfigureAwait(false);

                var limit = plan.TruncateAfterBytes is { } truncate ? Math.Min(count, truncate) : count;
                if (body is not null)
                {
                    await stream.WriteAsync(body.AsMemory((int)start, (int)limit), _stop.Token).ConfigureAwait(false);
                }
                else
                {
                    await using FileStream source = File.OpenRead(file);
                    source.Position = start;
                    var buffer = new byte[64 * 1024];
                    var remaining = limit;
                    while (remaining > 0)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), _stop.Token).ConfigureAwait(false);
                        if (read <= 0)
                            break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
                        remaining -= read;
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

    private static async Task<string?> ReadHeaderAsync(Stream stream)
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

    private Task WriteAsync(Stream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).AsTask();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (TcpClient client in _clients)
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
internal sealed class SelfSignedCertificate : IDisposable
{
    private SelfSignedCertificate(X509Certificate2 certificate, string pemPath)
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
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var pemPath = Path.Combine(Directory.CreateTempSubdirectory("fbm-tls-").FullName, "ca.pem");
        File.WriteAllText(pemPath, created.ExportCertificatePem());
        // Reload through PKCS#12 so the private key is usable by SslStream on every platform.
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
        return new SelfSignedCertificate(certificate, pemPath);
    }

    public void Dispose()
    {
        Certificate.Dispose();
        Directory.Delete(Path.GetDirectoryName(PemPath)!, recursive: true);
    }
}

/// <summary>A TCP port that accepts connections and never answers (a hung server).</summary>
internal sealed class SilentTcpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentBag<Socket> _accepted = [];
    private readonly CancellationTokenSource _stop = new();

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
        foreach (Socket socket in _accepted)
            socket.Dispose();
        _stop.Dispose();
    }
}
