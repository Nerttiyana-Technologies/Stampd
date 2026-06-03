using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace Stampd.Revocation.Http;

/// <summary>
/// Pulls revocation-related URLs out of an X.509 certificate's extensions. Used by the
/// OCSP and CRL HTTP providers to discover where to talk to.
/// </summary>
/// <remarks>
/// Public-key infrastructure extensions referenced:
/// <list type="bullet">
///   <item><description>Authority Information Access (AIA) — OID 1.3.6.1.5.5.7.1.1. Carries an
///   array of <c>(accessMethod, accessLocation)</c>; the OCSP method OID is
///   1.3.6.1.5.5.7.48.1.</description></item>
///   <item><description>CRL Distribution Points (CDP) — OID 2.5.29.31. Carries an array of
///   distribution-point structures; we extract the simple URI variant.</description></item>
/// </list>
///
/// <para>
/// This parser is intentionally narrow: it handles the common cases used by real-world CAs
/// (DigiCert, Sectigo, GlobalSign, Let's Encrypt, internal Microsoft AD CS) and ignores
/// exotic CRL distribution constructions like <c>nameRelativeToCRLIssuer</c>.
/// </para>
/// </remarks>
internal static class CertificateExtensionExtractor
{
    private const string AiaOid = "1.3.6.1.5.5.7.1.1";
    private const string OcspAccessMethodOid = "1.3.6.1.5.5.7.48.1";
    private const string CdpOid = "2.5.29.31";

    /// <summary>
    /// Extracts every OCSP responder URL embedded in the cert's AIA extension. Returns an
    /// empty list when the extension is missing or contains no OCSP entries.
    /// </summary>
    public static IReadOnlyList<Uri> GetOcspUrls(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var aia = certificate.Extensions[AiaOid];
        if (aia is null)
        {
            return [];
        }

        var urls = new List<Uri>();
        try
        {
            var reader = new AsnReader(aia.RawData, AsnEncodingRules.DER);
            var aiaSequence = reader.ReadSequence();
            while (aiaSequence.HasData)
            {
                var accessDescription = aiaSequence.ReadSequence();
                var methodOid = accessDescription.ReadObjectIdentifier();
                // accessLocation is a GeneralName (CHOICE). For HTTP OCSP, it's [6] IA5String.
                var locationTag = new Asn1Tag(TagClass.ContextSpecific, tagValue: 6);
                if (string.Equals(methodOid, OcspAccessMethodOid, StringComparison.Ordinal)
                    && accessDescription.PeekTag() == locationTag)
                {
                    var url = accessDescription.ReadCharacterString(UniversalTagNumber.IA5String, locationTag);
                    if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                    {
                        urls.Add(parsed);
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // Malformed AIA — return what we have so far.
        }

        return urls;
    }

    /// <summary>
    /// Extracts every full-URI CRL distribution point from the cert's CDP extension.
    /// Returns an empty list when the extension is missing or only contains non-URI
    /// variants.
    /// </summary>
    public static IReadOnlyList<Uri> GetCrlUrls(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var cdp = certificate.Extensions[CdpOid];
        if (cdp is null)
        {
            return [];
        }

        var urls = new List<Uri>();
        try
        {
            var reader = new AsnReader(cdp.RawData, AsnEncodingRules.DER);
            var cdpSequence = reader.ReadSequence();
            while (cdpSequence.HasData)
            {
                var distributionPoint = cdpSequence.ReadSequence();
                // distributionPoint ::= SEQUENCE { distributionPoint [0] DistributionPointName OPTIONAL, ... }
                if (!distributionPoint.HasData)
                {
                    continue;
                }

                var dpNameTag = new Asn1Tag(TagClass.ContextSpecific, tagValue: 0, isConstructed: true);
                if (distributionPoint.PeekTag() != dpNameTag)
                {
                    continue;
                }

                var dpName = distributionPoint.ReadSequence(dpNameTag);
                // distributionPointName CHOICE { fullName [0] GeneralNames, ... }
                var fullNameTag = new Asn1Tag(TagClass.ContextSpecific, tagValue: 0, isConstructed: true);
                if (dpName.PeekTag() != fullNameTag)
                {
                    continue;
                }

                var fullName = dpName.ReadSequence(fullNameTag);
                while (fullName.HasData)
                {
                    // GeneralName ::= CHOICE { ... uniformResourceIdentifier [6] IA5String, ... }
                    var uriTag = new Asn1Tag(TagClass.ContextSpecific, tagValue: 6);
                    if (fullName.PeekTag() == uriTag)
                    {
                        var url = fullName.ReadCharacterString(UniversalTagNumber.IA5String, uriTag);
                        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                        {
                            urls.Add(parsed);
                        }
                    }
                    else
                    {
                        // Skip non-URI GeneralName variants.
                        _ = fullName.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // Malformed CDP — return what we have so far.
        }

        return urls;
    }
}
