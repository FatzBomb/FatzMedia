using FatzMedia.Video.Core.Network;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-034: certificate verification rules for https network video.</summary>
public sealed class NetworkTlsRulesTests
{
    // Trimmed configure lines of the builds the docs name.
    private const string LinuxBtbN = "--prefix=/ffbuild/prefix --enable-gpl=no --enable-version3 --disable-debug --enable-openssl --disable-schannel --enable-libdav1d";
    private const string WindowsTqk = "--arch=x86_64 --target-os=mingw32 --enable-version3 --enable-librist --enable-libssh --enable-schannel --enable-libsrt";
    private const string WindowsOpenSsl = "--arch=x86_64 --target-os=mingw32 --enable-openssl --disable-schannel";

    [Fact]
    public void FfmpegOptions_Default_VerifiesWithThePlatformRoots()
    {
        Dictionary<string, string> options = NetworkTlsRules.CreateFfmpegOptions(NetworkTlsPolicy.Default).ToDictionary();

        Assert.Equal("1", options["tls_verify"]);
        Assert.False(options.ContainsKey("ca_file"));
    }

    [Fact]
    public void FfmpegOptions_MapTheOptOutAndTheCaFile()
    {
        Assert.Equal("0", NetworkTlsRules.CreateFfmpegOptions(new NetworkTlsPolicy { AllowInvalidCertificates = true }).ToDictionary()["tls_verify"]);

        Dictionary<string, string> withCa = NetworkTlsRules.CreateFfmpegOptions(new NetworkTlsPolicy { CertificateAuthorityFile = "/etc/media/ca.pem" }).ToDictionary();
        Assert.Equal("1", withCa["tls_verify"]);
        Assert.Equal("/etc/media/ca.pem", withCa["ca_file"]);
    }

    [Theory]
    [InlineData(LinuxBtbN, false, "OpenSsl")]
    [InlineData(WindowsTqk, true, "Schannel")]
    [InlineData(WindowsOpenSsl, true, "OpenSsl")]
    [InlineData("--target-os=mingw32 --enable-gpl", true, "Schannel")]
    [InlineData("--target-os=mingw32 --disable-schannel", true, "None")]
    [InlineData("--enable-gnutls", false, "GnuTls")]
    [InlineData("--enable-mbedtls", true, "MbedTls")]
    [InlineData("--enable-gpl", false, "None")]
    [InlineData("", false, "Unknown")]
    [InlineData(null, true, "Unknown")]
    public void DetectBackend_ReadsTheConfigureLine(string? configuration, bool isWindows, string expected) =>
        Assert.Equal(expected, NetworkTlsRules.DetectBackend(configuration, isWindows).ToString());

    [Fact]
    public void DetectBackend_DoesNotMatchPartOfAnotherFlag() =>
        Assert.Equal(FfmpegTlsBackend.None, NetworkTlsRules.DetectBackend("--enable-openssl-legacy --enable-gnutlsx", isWindows: false));

    [Fact]
    public void SetupWarning_OnlyForBuildsThatCannotVerifyAsConfigured()
    {
        var withCa = new NetworkTlsPolicy { CertificateAuthorityFile = "ca.pem" };
        var optOut = new NetworkTlsPolicy { AllowInvalidCertificates = true };

        Assert.Contains("Windows certificate store", NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.OpenSsl, true, NetworkTlsPolicy.Default), StringComparison.Ordinal);
        Assert.Null(NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.OpenSsl, true, withCa));
        Assert.Null(NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.OpenSsl, false, NetworkTlsPolicy.Default));
        Assert.Contains("no default trusted roots", NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.MbedTls, false, NetworkTlsPolicy.Default), StringComparison.Ordinal);
        Assert.Contains("ignores", NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.Schannel, true, withCa), StringComparison.Ordinal);
        Assert.Null(NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.Schannel, true, NetworkTlsPolicy.Default));
        Assert.Null(NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.GnuTls, true, NetworkTlsPolicy.Default));
        Assert.Null(NetworkTlsRules.GetSetupWarning(FfmpegTlsBackend.OpenSsl, true, optOut));
    }

    [Theory]
    [InlineData("[tls @ 0x55] error:0A000086:SSL routines::certificate verify failed", true)]
    [InlineData("[tls @ 0x55] SSL_CTX_load_verify_locations error:FFFFFFFF80000002:system library::No such file or directory", true)]
    [InlineData("[tls @ 0x55] Peer certificate failed verification", true)]
    [InlineData("[tls @ 0x55] mbedtls_ssl_get_verify_result reported problems with the certificate", true)]
    [InlineData("[tls @ 0x55] SNI or certificate check failed", true)]
    [InlineData("[tls @ 0x55] Creating security context failed (0x80090325)", true)]
    [InlineData("[tls @ 0x55] Creating security context failed (0x800b0109)", true)]
    [InlineData("[tls @ 0x55] Creating security context failed (0x80090331)", false)]
    [InlineData("[tls @ 0x55] error:0A00010B:SSL routines::wrong version number", false)]
    [InlineData(null, false)]
    public void IsCertificateFailure_RecognisesEachBackend(string? line, bool expected) =>
        Assert.Equal(expected, NetworkTlsRules.IsCertificateFailure(line));

    [Fact]
    public void DescribeCertificateFailure_NamesTheHostAndFfmpegsText()
    {
        var message = NetworkTlsRules.DescribeCertificateFailure(
            "media.example",
            ["[tls @ 0x55d] error:0A000086:SSL routines::certificate verify failed"],
            "Input/output error",
            setupWarning: null);

        Assert.Equal("certificate verification failed for media.example: error:0A000086:SSL routines::certificate verify failed (Input/output error)", message);
        Assert.True(NetworkTlsRules.IsCertificateFailureError(message));
        Assert.False(NetworkTlsRules.IsCertificateFailureError("Connection refused"));
        Assert.EndsWith(". hint", NetworkTlsRules.DescribeCertificateFailure("h", [], "Input/output error", "hint"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://media.example/a.mp4", true)]
    [InlineData("HTTPS://media.example/a.mp4", true)]
    [InlineData("crypto+https://media.example/seg.ts", true)]
    [InlineData("http://media.example/a.mp4", false)]
    [InlineData(null, false)]
    public void IsHttps_IncludesEncryptedSegments(string? url, bool expected) =>
        Assert.Equal(expected, NetworkTlsRules.IsHttps(url));

    [Theory]
    [InlineData("https://media.example/a.mp4", true)]
    [InlineData("http://media.example/a.mp4", true)]
    [InlineData("crypto+http://media.example/seg.ts", true)]
    [InlineData("file:///tmp/a.mp4", false)]
    [InlineData(null, false)]
    public void TakesTlsOptions_IncludesHttpThatCanRedirectToHttps(string? url, bool expected) =>
        Assert.Equal(expected, NetworkTlsRules.TakesTlsOptions(url));
}
