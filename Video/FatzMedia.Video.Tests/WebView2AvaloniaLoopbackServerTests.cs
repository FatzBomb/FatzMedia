using FatzMedia.Video.WebView2.Avalonia;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 060A: the loopback server that gives the Avalonia web view a real origin for the YouTube player page.</summary>
public sealed class WebView2AvaloniaLoopbackServerTests
{
    [Fact]
    public async Task ServesThePageOnLoopbackAndNothingElse()
    {
        using var server = new LoopbackPageServer("<html>player</html>"u8.ToArray());
        using var http = new HttpClient();

        var page = await http.GetAsync(server.PageUri("?v=abc&autoplay=0"));
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<html>player</html>", await page.Content.ReadAsStringAsync());

        var other = await http.GetAsync($"http://127.0.0.1:{server.Port}/other.html");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, other.StatusCode);

        Assert.Equal("127.0.0.1", server.PageUri(string.Empty).Host);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var server = new LoopbackPageServer([1]);
        var port = server.Port;
        server.Dispose();

        using var probe = new System.Net.Sockets.TcpClient();
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => probe.Connect(System.Net.IPAddress.Loopback, port));
    }
}
