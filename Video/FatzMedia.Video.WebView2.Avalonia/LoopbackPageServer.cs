using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FatzMedia.Video.WebView2.Avalonia;

/// <summary>
/// Serves one static page (the YouTube player page) on the loopback address only, so the web view has a real
/// origin and sends a Referer, which YouTube needs (without it the player answers error 153). Answers GET
/// requests for the page and 404 for everything else; it never reads a body and never listens beyond 127.0.0.1.
/// </summary>
public sealed class LoopbackPageServer : IDisposable
{
    const string PagePath = "/youtube-player.html";

    readonly TcpListener _listener;
    readonly byte[] _page;
    readonly CancellationTokenSource _stop = new();

    /// <summary>Starts serving <paramref name="page"/> on a free loopback port.</summary>
    public LoopbackPageServer(byte[] page)
    {
        _page = page;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    /// <summary>Gets the port the server listens on (loopback only).</summary>
    public int Port { get; }

    /// <summary>Gets the address of the page, with <paramref name="query"/> (for example <c>?v=id</c>).</summary>
    public Uri PageUri(string query) => new($"http://127.0.0.1:{Port}{PagePath}{query}");

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The server was disposed.
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 5000;
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var line = request.Split('\r', 2)[0].Split(' ');
                var found = line.Length >= 2 && line[0] == "GET" && line[1].Split('?', 2)[0] == PagePath;

                var body = found ? _page : [];
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The web view went away mid-request.
            }
        }
    }
}
