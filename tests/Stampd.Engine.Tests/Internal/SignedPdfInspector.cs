using System.Globalization;
using System.Text.RegularExpressions;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;

#pragma warning disable IDE0005

namespace Stampd.Engine.Tests.Internal;

/// <summary>
/// Parses a signed PDF and surfaces the bits the engine regression tests need to assert
/// on. Reads <c>/ByteRange</c>, locates the signature dictionary's <c>/Contents</c> hex
/// blob, decodes it as CMS, and exposes the signed-attributes view.
/// </summary>
public sealed class SignedPdfInspector
{
    private static readonly Regex ByteRangeRegex = new(
        @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]",
        RegexOptions.Compiled);

    private SignedPdfInspector(
        byte[] file,
        long[] byteRange,
        long contentsOpenIndex,
        long contentsCloseIndex,
        byte[] cmsBytes,
        CmsSignedData signedData)
    {
        File = file;
        ByteRange = byteRange;
        ContentsOpenIndex = contentsOpenIndex;
        ContentsCloseIndex = contentsCloseIndex;
        CmsBytes = cmsBytes;
        SignedData = signedData;
    }

    public byte[] File { get; }

    /// <summary>Four-element /ByteRange [offset1, length1, offset2, length2].</summary>
    public long[] ByteRange { get; }

    /// <summary>Byte index of the '&lt;' opening the signature /Contents hex value.</summary>
    public long ContentsOpenIndex { get; }

    /// <summary>Byte index of the '&gt;' closing the signature /Contents hex value.</summary>
    public long ContentsCloseIndex { get; }

    /// <summary>Raw CMS bytes extracted from /Contents (with trailing hex-zero padding stripped).</summary>
    public byte[] CmsBytes { get; }

    public CmsSignedData SignedData { get; }

    public SignerInformation FirstSigner =>
        SignedData.GetSignerInfos().GetSigners().Cast<SignerInformation>().First();

    /// <summary>
    /// Extracts the messageDigest octet string from signedAttributes (OID 1.2.840.113549.1.9.4).
    /// This is what the engine signs over; verifiers compare it to SHA-256(byte range).
    /// </summary>
    public byte[] MessageDigest
    {
        get
        {
            var attr = FirstSigner.SignedAttributes[PkcsObjectIdentifiers.Pkcs9AtMessageDigest];
            var values = attr.AttrValues;
            var octet = (Asn1OctetString)values[0];
            return octet.GetOctets();
        }
    }

    /// <summary>
    /// Returns true iff the SignerInfo carries a <c>signatureTimeStampToken</c> unsigned
    /// attribute (OID 1.2.840.113549.1.9.16.2.14) — i.e., the signature is PAdES B-T.
    /// </summary>
    public bool HasEmbeddedTimestamp
    {
        get
        {
            var unsigned = FirstSigner.UnsignedAttributes;
            if (unsigned is null)
            {
                return false;
            }

            return unsigned[PkcsObjectIdentifiers.IdAASignatureTimeStampToken] is not null;
        }
    }

    /// <summary>
    /// Returns the concatenation of the bytes covered by /ByteRange — what a verifier
    /// hashes to compare against messageDigest.
    /// </summary>
    public byte[] ByteRangeBytes()
    {
        var r1 = new ArraySegment<byte>(File, (int)ByteRange[0], (int)ByteRange[1]);
        var r2 = new ArraySegment<byte>(File, (int)ByteRange[2], (int)ByteRange[3]);
        var combined = new byte[r1.Count + r2.Count];
        Buffer.BlockCopy(r1.Array!, r1.Offset, combined, 0, r1.Count);
        Buffer.BlockCopy(r2.Array!, r2.Offset, combined, r1.Count, r2.Count);
        return combined;
    }

    public static SignedPdfInspector Parse(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var text = System.Text.Encoding.Latin1.GetString(pdfBytes);
        var match = ByteRangeRegex.Match(text);
        if (!match.Success)
        {
            throw new InvalidOperationException("No /ByteRange entry found in PDF.");
        }

        var byteRange = new[]
        {
            long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
        };

        // The signature /Contents <...> sits in the excluded region between byteRange[1]
        // and byteRange[2]. The '<' is at byteRange[1] and the '>' is at byteRange[2]-1.
        var openIdx = byteRange[0] + byteRange[1];
        var closeIdx = byteRange[2] - 1;

        if (pdfBytes[openIdx] != (byte)'<' || pdfBytes[closeIdx] != (byte)'>')
        {
            throw new InvalidOperationException(
                $"Expected '<' at byte {openIdx} and '>' at byte {closeIdx}; got " +
                $"'{(char)pdfBytes[openIdx]}' and '{(char)pdfBytes[closeIdx]}'.");
        }

        var hex = System.Text.Encoding.Latin1
            .GetString(pdfBytes, (int)openIdx + 1, (int)(closeIdx - openIdx - 1))
            .TrimEnd('0', '\r', '\n', ' ', '\t');
        if (hex.Length % 2 == 1)
        {
            hex += "0"; // restore final nibble if we trimmed one too many
        }

        var cmsBytes = Convert.FromHexString(hex);
        var signedData = new CmsSignedData(cmsBytes);

        return new SignedPdfInspector(pdfBytes, byteRange, openIdx, closeIdx, cmsBytes, signedData);
    }
}
