using System.Text.RegularExpressions;

namespace FatzBomb.MediaControl.Core.Network;

/// <summary>The TLS library FFmpeg's <c>https</c> protocol is built on.</summary>
internal enum FfmpegTlsBackend
{
    /// <summary>The build configuration could not be read.</summary>
    Unknown,

    /// <summary>No TLS library: <c>https</c> sources cannot open.</summary>
    None,

    /// <summary>OpenSSL: <c>ca_file</c> or the build-time default paths (<c>OPENSSLDIR</c>).</summary>
    OpenSsl,

    /// <summary>GnuTLS: <c>ca_file</c> or the system trust (including the Windows store).</summary>
    GnuTls,

    /// <summary>mbedTLS: <c>ca_file</c> only; it has no default roots.</summary>
    MbedTls,

    /// <summary>Windows Schannel: the Windows certificate store; <c>ca_file</c> is ignored.</summary>
    Schannel,
}

/// <summary>
/// Pure certificate-verification rules for network video (ENG-034): the FFmpeg TLS options of a
/// <see cref="NetworkTlsPolicy"/>, the TLS backend of an FFmpeg build, the setup warning for a
/// build that cannot verify with the policy as given, and the classification of a failed open.
/// </summary>
internal static partial class NetworkTlsRules
{
    /// <summary>The start of every certificate failure reported as a network open error.</summary>
    public const string CertificateFailurePrefix = "certificate verification failed for ";

    // Schannel reports a failed handshake as "Creating security context failed (0x%lx)". These are
    // the statuses of a certificate the client rejected (trust, name, validity, revocation).
    private static readonly HashSet<uint> SchannelCertificateStatuses =
    [
        0x80090322, // SEC_E_WRONG_PRINCIPAL
        0x80090325, // SEC_E_UNTRUSTED_ROOT
        0x80090327, // SEC_E_CERT_UNKNOWN
        0x80090328, // SEC_E_CERT_EXPIRED
        0x80090349, // SEC_E_CERT_WRONG_USAGE
        0x80090352, // SEC_E_ISSUING_CA_UNTRUSTED
        0x800B0101, // CERT_E_EXPIRED
        0x800B0109, // CERT_E_UNTRUSTEDROOT
        0x800B010F, // CERT_E_CN_NO_MATCH
        0x80092010, // CRYPT_E_REVOKED
        0x80092012, // CRYPT_E_NO_REVOCATION_CHECK
        0x80092013, // CRYPT_E_REVOCATION_OFFLINE
    ];

    /// <summary>Whether <paramref name="url"/> (possibly behind <c>crypto+</c>) is an https URL.</summary>
    public static bool IsHttps(string? url) =>
        url is not null && (url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("crypto+https:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The FFmpeg TLS options of <paramref name="policy"/>: <c>tls_verify</c> always (on unless the
    /// host allows invalid certificates), and <c>ca_file</c> when the host named one. Verification
    /// is set explicitly rather than left to FFmpeg's default, which was off before FFmpeg 9.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> CreateFfmpegOptions(NetworkTlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        List<KeyValuePair<string, string>> options = [new("tls_verify", policy.AllowInvalidCertificates ? "0" : "1")];
        if (!string.IsNullOrWhiteSpace(policy.CertificateAuthorityFile))
            options.Add(new("ca_file", policy.CertificateAuthorityFile));
        return options;
    }

    /// <summary>
    /// The TLS backend of an FFmpeg build from its configure line (<c>avformat_configuration</c>).
    /// An explicit TLS library wins; otherwise a Windows build uses Schannel, which FFmpeg's
    /// configure enables automatically unless <c>--disable-schannel</c> is given (it conflicts with
    /// every other TLS library).
    /// </summary>
    public static FfmpegTlsBackend DetectBackend(string? configuration, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(configuration))
            return FfmpegTlsBackend.Unknown;

        var flags = configuration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (flags.Contains("--enable-openssl"))
            return FfmpegTlsBackend.OpenSsl;
        if (flags.Contains("--enable-gnutls"))
            return FfmpegTlsBackend.GnuTls;
        if (flags.Contains("--enable-mbedtls"))
            return FfmpegTlsBackend.MbedTls;
        if (flags.Contains("--enable-schannel") || (isWindows && !flags.Contains("--disable-schannel")))
            return FfmpegTlsBackend.Schannel;
        return FfmpegTlsBackend.None;
    }

    /// <summary>
    /// A warning for a build that cannot verify https certificates with <paramref name="policy"/>
    /// as given, or <see langword="null"/>. Verification stays on: an OpenSSL build on Windows (no
    /// system store) or an mbedTLS build (no default roots) without a CA file rejects every https
    /// server, and Schannel ignores a CA file. The open is not weakened to work around either.
    /// </summary>
    public static string? GetSetupWarning(FfmpegTlsBackend backend, bool isWindows, NetworkTlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.AllowInvalidCertificates)
            return null;

        var hasCaFile = !string.IsNullOrWhiteSpace(policy.CertificateAuthorityFile);
        return backend switch
        {
            FfmpegTlsBackend.OpenSsl when isWindows && !hasCaFile =>
                "FFmpeg's TLS backend is OpenSSL, which cannot read the Windows certificate store: https certificate verification fails " +
                "unless NetworkTlsPolicy.CertificateAuthorityFile names a CA bundle. Use an FFmpeg build with Schannel, or set the CA file.",
            FfmpegTlsBackend.MbedTls when !hasCaFile =>
                "FFmpeg's TLS backend is mbedTLS, which has no default trusted roots: https certificate verification fails " +
                "unless NetworkTlsPolicy.CertificateAuthorityFile names a CA bundle.",
            FfmpegTlsBackend.Schannel when hasCaFile =>
                "FFmpeg's TLS backend is Schannel, which ignores NetworkTlsPolicy.CertificateAuthorityFile: " +
                "install that CA in the Windows certificate store (Trusted Root Certification Authorities) instead.",
            _ => null,
        };
    }

    /// <summary>
    /// Whether an FFmpeg log line reports a rejected server certificate (or an unusable CA file):
    /// OpenSSL "certificate verify failed" and <c>SSL_CTX_load_verify_locations</c>, GnuTLS "Peer
    /// certificate failed verification", mbedTLS "verify_result", and Schannel's certificate
    /// statuses or "SNI or certificate check failed".
    /// </summary>
    public static bool IsCertificateFailure(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        if (line.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            || line.Contains("verify", StringComparison.OrdinalIgnoreCase))
            return true;

        Match status = SchannelStatus().Match(line);
        return status.Success
            && uint.TryParse(status.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var code)
            && SchannelCertificateStatuses.Contains(code);
    }

    /// <summary>Whether a network open error is a certificate failure (not worth a retry).</summary>
    public static bool IsCertificateFailureError(string? error) =>
        error is not null && error.StartsWith(CertificateFailurePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The open error of a rejected certificate: "certificate verification failed for &lt;host&gt;:
    /// &lt;FFmpeg's TLS lines&gt; (&lt;FFmpeg error&gt;)", with the backend hint when there is one.
    /// </summary>
    public static string DescribeCertificateFailure(string host, IReadOnlyList<string> tlsLines, string ffmpegError, string? setupWarning)
    {
        ArgumentNullException.ThrowIfNull(tlsLines);
        var detail = tlsLines.Count == 0 ? ffmpegError : $"{string.Join("; ", tlsLines.Select(StripContextPrefix))} ({ffmpegError})";
        var message = $"{CertificateFailurePrefix}{host}: {detail}";
        return setupWarning is null ? message : $"{message}. {setupWarning}";
    }

    /// <summary>Removes FFmpeg's "[tls @ 0x...] " context prefix.</summary>
    public static string StripContextPrefix(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is var end and > 0
            ? line[(end + 2)..]
            : line;
    }

    [GeneratedRegex(@"security context failed \(0x([0-9a-fA-F]{1,8})\)", RegexOptions.IgnoreCase)]
    private static partial Regex SchannelStatus();
}
