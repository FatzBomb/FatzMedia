#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// How network video checks the certificate of an <c>https</c> server (ENG-034). By default the
/// certificate must chain to a trusted root and match the host name. A host that plays from a
/// private or self-signed server either names that server's CA in
/// <see cref="CertificateAuthorityFile"/> or, as a last resort, turns the check off with
/// <see cref="AllowInvalidCertificates"/>. Network audio played through BASS (014A) follows
/// BASS's own HTTPS rules, not this policy.
/// </summary>
public sealed record NetworkTlsPolicy
{
    /// <summary>Gets the default policy: certificates are verified against the platform's trusted roots.</summary>
    public static NetworkTlsPolicy Default { get; } = new();

    /// <summary>
    /// Gets whether an invalid, expired, self-signed or mismatched server certificate is accepted.
    /// The connection is still encrypted, but a tampered connection is no longer detected. Off by
    /// default; use only for servers the host controls.
    /// </summary>
    public bool AllowInvalidCertificates { get; init; }

    /// <summary>
    /// Gets a PEM file of trusted CA certificates used instead of the platform's default roots, or
    /// <see langword="null"/> for the defaults. Honoured by OpenSSL, GnuTLS and mbedTLS builds of
    /// FFmpeg; a Schannel (Windows) build ignores it and trusts the Windows certificate store, so
    /// there the CA must be installed in the store instead.
    /// </summary>
    public string? CertificateAuthorityFile { get; init; }
}
