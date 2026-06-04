using System.Globalization;
using System.Text;

namespace Stampd.Engine.Pades;

/// <summary>
/// Appends a PAdES B-LT Document Security Store to a finished signed PDF as an
/// <i>incremental update revision</i>, per ETSI EN 319 142-1 §5.4.3.
/// </summary>
/// <remarks>
/// <para>
/// The signing pipeline emits a B-T-signed PDF whose <c>/ByteRange</c> covers the entire
/// file body (with the <c>/Contents</c> placeholder excluded). This writer takes those
/// finished bytes and appends a strictly-additive revision that registers the DSS,
/// cert / OCSP / CRL streams, and a revised catalog object pointing at the DSS.
/// </para>
/// <para>
/// Because the original bytes are NOT modified, the signature's <c>/ByteRange</c> still
/// validates against the unchanged content — and the appended DSS data is available to
/// long-term-validation verifiers without weakening the signature itself. This is the
/// strict-ETSI shape: validators that don't trust the post-signing DSS can verify the
/// signature alone; validators that need LTV use the DSS without rejecting the document.
/// </para>
/// <para>
/// Implementation strategy: read the original file's last 1 KB to locate the trailing
/// <c>startxref</c> pointer, parse the previous xref to find the catalog object's
/// byte offset and the largest live object number, read the catalog dict's bytes
/// directly, insert <c>/DSS K 0 R</c> immediately before its closing <c>&gt;&gt;</c>,
/// then emit new indirect objects + a new xref + trailer + EOF. No PdfSharp involvement
/// on the write path — PdfSharp's <c>Save()</c> would re-serialize the whole document
/// and invalidate the signature.
/// </para>
/// </remarks>
internal static class PadesIncrementalUpdateWriter
{
    private static readonly byte[] StartxrefKeyword = "startxref"u8.ToArray();
    private static readonly byte[] TrailerKeyword = "trailer"u8.ToArray();
    private static readonly byte[] XrefKeyword = "xref"u8.ToArray();
    private static readonly byte[] DictOpen = "<<"u8.ToArray();
    private static readonly byte[] DictClose = ">>"u8.ToArray();
    private static readonly byte[] ObjKeyword = " obj"u8.ToArray();
    private static readonly byte[] EndobjKeyword = "endobj"u8.ToArray();

    /// <summary>
    /// Appends an incremental-update revision to <paramref name="signedPdf"/> that
    /// registers the supplied revocation material as a <c>/DSS</c> entry on the catalog.
    /// Returns the concatenated bytes (original + appended). When <paramref name="data"/>
    /// is empty, returns the input unchanged.
    /// </summary>
    public static byte[] AppendDss(byte[] signedPdf, PadesRevocationData data)
    {
        ArgumentNullException.ThrowIfNull(signedPdf);
        ArgumentNullException.ThrowIfNull(data);

        if (data.IsEmpty)
        {
            return signedPdf;
        }

        // ---- Parse the existing trailer + xref ----
        var previousXrefOffset = FindPreviousXrefOffset(signedPdf);
        var (catalogObjectNumber, previousSize) = ReadTrailerRootAndSize(signedPdf, previousXrefOffset);
        var catalogOffset = ReadCatalogOffsetFromXref(signedPdf, previousXrefOffset, catalogObjectNumber);
        var (catalogDictBody, _) = ReadCatalogDictBody(signedPdf, catalogOffset, catalogObjectNumber);

        // ---- Allocate object numbers for the appended objects ----
        // K     = DSS dict
        // K+1.. = cert / OCSP / CRL streams (in that order, flat, matching DSS array layout)
        var dssObjNum = previousSize;             // first free slot (PDF /Size is "max obj num + 1")
        var firstStreamObjNum = dssObjNum + 1;

        var certCount = data.Certificates.Count;
        var ocspCount = data.OcspResponses.Count;
        var crlCount = data.Crls.Count;
        var totalStreamCount = certCount + ocspCount + crlCount;

        // ---- Build appended-region bytes ----
        // The appended bytes are positioned at offset `signedPdf.Length` in the final file
        // (after we tack on a leading newline if the original didn't end with one). All
        // byte offsets recorded in the new xref are absolute offsets from the start of the
        // final file, NOT relative to the appended region.
        using var appended = new MemoryStream();
        var leadingNewline = NeedsLeadingNewline(signedPdf);
        if (leadingNewline)
        {
            appended.WriteByte((byte)'\n');
        }

        var appendedRegionStart = signedPdf.Length + (leadingNewline ? 1 : 0);

        // ---- Revised catalog object ----
        var catalogObjectOffsetInFinal = appendedRegionStart + (int)appended.Position;
        var revisedCatalog = BuildRevisedCatalogObject(
            objectNumber: catalogObjectNumber,
            originalDictBody: catalogDictBody,
            dssObjNum: dssObjNum);
        appended.Write(revisedCatalog, 0, revisedCatalog.Length);

        // ---- Stream objects (one per blob, in flat cert→OCSP→CRL order) ----
        // We record each stream's object number AND its byte offset so the DSS dict can
        // reference them and the xref can locate them. The cert/OCSP/CRL arrays each
        // index into a contiguous range of these object numbers.
        var streamOffsets = new long[totalStreamCount];
        var nextObjNum = firstStreamObjNum;
        var streamWriterIdx = 0;

        // Build the DSS dict first (in memory) so we can know the cert / OCSP / CRL
        // object-number ranges before emitting any stream. Then emit the DSS dict
        // followed by every stream.
        var certObjNums = new long[certCount];
        for (var i = 0; i < certCount; i++)
        {
            certObjNums[i] = nextObjNum + i;
        }
        var ocspObjNums = new long[ocspCount];
        for (var i = 0; i < ocspCount; i++)
        {
            ocspObjNums[i] = nextObjNum + certCount + i;
        }
        var crlObjNums = new long[crlCount];
        for (var i = 0; i < crlCount; i++)
        {
            crlObjNums[i] = nextObjNum + certCount + ocspCount + i;
        }

        // ---- DSS dict object ----
        var dssOffsetInFinal = appendedRegionStart + (int)appended.Position;
        var dssBytes = BuildDssObject(dssObjNum, certObjNums, ocspObjNums, crlObjNums);
        appended.Write(dssBytes, 0, dssBytes.Length);

        // ---- Stream objects ----
        var certLocalIdx = 0;
        foreach (var blob in data.Certificates)
        {
            streamOffsets[streamWriterIdx] = appendedRegionStart + appended.Position;
            var streamBytes = BuildStreamObject(certObjNums[certLocalIdx], blob);
            appended.Write(streamBytes, 0, streamBytes.Length);
            streamWriterIdx++;
            certLocalIdx++;
        }
        // Each blob loop tracks its own array-local index; streamWriterIdx is the flat
        // index into streamOffsets (covers cert→OCSP→CRL contiguously).
        var ocspLocalIdx = 0;
        foreach (var blob in data.OcspResponses)
        {
            streamOffsets[streamWriterIdx] = appendedRegionStart + appended.Position;
            var streamBytes = BuildStreamObject(ocspObjNums[ocspLocalIdx], blob);
            appended.Write(streamBytes, 0, streamBytes.Length);
            streamWriterIdx++;
            ocspLocalIdx++;
        }
        var crlLocalIdx = 0;
        foreach (var blob in data.Crls)
        {
            streamOffsets[streamWriterIdx] = appendedRegionStart + appended.Position;
            var streamBytes = BuildStreamObject(crlObjNums[crlLocalIdx], blob);
            appended.Write(streamBytes, 0, streamBytes.Length);
            streamWriterIdx++;
            crlLocalIdx++;
        }

        // ---- xref + trailer + startxref + EOF ----
        var newXrefOffsetInFinal = appendedRegionStart + appended.Position;
        var newSize = dssObjNum + 1 + totalStreamCount; // catalog (already counted) + new objects

        var xrefAndTrailer = BuildXrefAndTrailer(
            catalogObjectNumber: catalogObjectNumber,
            catalogOffset: catalogObjectOffsetInFinal,
            dssObjNum: dssObjNum,
            dssOffset: dssOffsetInFinal,
            streamFirstObjNum: firstStreamObjNum,
            streamOffsets: streamOffsets,
            previousSize: previousSize,
            newSize: newSize,
            previousXrefOffset: previousXrefOffset,
            newXrefOffset: newXrefOffsetInFinal);

        appended.Write(xrefAndTrailer, 0, xrefAndTrailer.Length);

        // ---- Concatenate original + appended ----
        var result = new byte[signedPdf.Length + (int)appended.Length];
        Buffer.BlockCopy(signedPdf, 0, result, 0, signedPdf.Length);
        var appendedBytes = appended.ToArray();
        Buffer.BlockCopy(appendedBytes, 0, result, signedPdf.Length, appendedBytes.Length);
        return result;
    }

    // ============================================================================
    // PDF structure parsing — read-only on signedPdf bytes
    // ============================================================================

    /// <summary>
    /// Scans the last ~1 KB of the file for <c>startxref\n&lt;offset&gt;\n%%EOF</c>
    /// and returns the parsed offset. The PDF spec mandates startxref + EOF in the last
    /// 1024 bytes for conforming readers; PdfSharp emits them within ~30 bytes of EOF.
    /// </summary>
    private static long FindPreviousXrefOffset(byte[] pdf)
    {
        var scanStart = Math.Max(0, pdf.Length - 1024);
        var keywordIdx = LastIndexOf(pdf, StartxrefKeyword, scanStart, pdf.Length - scanStart);
        if (keywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: signed PDF has no 'startxref' marker in the last 1 KB. "
                + "Cannot build an incremental update.");
        }

        // Skip the keyword + whitespace, then parse digits.
        long cursor = keywordIdx + StartxrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);
        var (offset, _) = ReadAsciiInteger(pdf, cursor);
        return offset;
    }

    /// <summary>
    /// Reads the trailer dictionary preceding the previous xref's tail and extracts
    /// <c>/Root N 0 R</c> (the catalog object number) and <c>/Size</c> (the high-water-mark
    /// for object numbers).
    /// </summary>
    private static (int CatalogObjectNumber, int Size) ReadTrailerRootAndSize(byte[] pdf, long previousXrefOffset)
    {
        // Trailer lives between the xref table and the startxref line. Find the
        // 'trailer' keyword AFTER previousXrefOffset.
        var trailerKeywordIdx = IndexOf(pdf, TrailerKeyword, previousXrefOffset, pdf.Length - previousXrefOffset);
        if (trailerKeywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: no 'trailer' keyword found after xref. "
                + "Hybrid xref-stream PDFs are not supported.");
        }

        // Find << after trailer.
        var dictOpenIdx = IndexOf(pdf, DictOpen, trailerKeywordIdx, pdf.Length - trailerKeywordIdx);
        if (dictOpenIdx < 0)
        {
            throw new InvalidOperationException("PadesIncrementalUpdateWriter: trailer dict not found.");
        }

        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);

        // Now scan the dict body for /Root and /Size.
        var dictBody = AsciiSubstring(pdf, dictOpenIdx + 2, (int)(dictCloseIdx - (dictOpenIdx + 2)));

        var rootMatch = System.Text.RegularExpressions.Regex.Match(
            dictBody,
            @"/Root\s+(\d+)\s+\d+\s+R",
            System.Text.RegularExpressions.RegexOptions.None);
        if (!rootMatch.Success)
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: trailer has no /Root entry.");
        }

        var catalogObjNum = int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var sizeMatch = System.Text.RegularExpressions.Regex.Match(
            dictBody,
            @"/Size\s+(\d+)",
            System.Text.RegularExpressions.RegexOptions.None);
        if (!sizeMatch.Success)
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: trailer has no /Size entry.");
        }

        var size = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        return (catalogObjNum, size);
    }

    /// <summary>
    /// Parses the classic-form xref table at <paramref name="xrefOffset"/> and returns
    /// the byte offset of the catalog object. Refuses to proceed on cross-reference
    /// streams (PDF 1.5+).
    /// </summary>
    private static long ReadCatalogOffsetFromXref(byte[] pdf, long xrefOffset, int catalogObjectNumber)
    {
        // The xref keyword must be at xrefOffset.
        if (!StartsWithAt(pdf, xrefOffset, XrefKeyword))
        {
            throw new InvalidOperationException(
                $"PadesIncrementalUpdateWriter: expected 'xref' keyword at offset {xrefOffset}; "
                + "cross-reference streams (xref objects) are not supported.");
        }

        var cursor = xrefOffset + XrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);

        // Parse subsection headers: "<firstObj> <count>\n" then <count> entries of
        // exactly 20 bytes each ("nnnnnnnnnn ggggg n \n" or "nnnnnnnnnn ggggg f \n").
        // Continue until we either find the catalog's entry OR hit the 'trailer' keyword.
        while (cursor < pdf.Length)
        {
            // Stop if we've reached the trailer.
            if (StartsWithAt(pdf, cursor, TrailerKeyword))
            {
                throw new InvalidOperationException(
                    $"PadesIncrementalUpdateWriter: catalog object {catalogObjectNumber} not found in xref table.");
            }

            var (firstObj, afterFirst) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipWhitespace(pdf, afterFirst);
            var (count, afterCount) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipToLineEnd(pdf, afterCount);
            cursor = SkipLineEnd(pdf, cursor);

            // Each entry is exactly 20 bytes per PDF spec §7.5.4.
            for (var i = 0; i < count; i++)
            {
                var entryStart = cursor;
                if (entryStart + 20 > pdf.Length)
                {
                    throw new InvalidOperationException(
                        "PadesIncrementalUpdateWriter: xref entry runs past end of file.");
                }

                var objNum = firstObj + i;
                if (objNum == catalogObjectNumber)
                {
                    // Parse the entry: 10-digit offset, space, 5-digit gen, space, 'n' or 'f'.
                    var offsetStr = AsciiSubstring(pdf, entryStart, 10);
                    var inUseChar = (char)pdf[entryStart + 17];
                    if (inUseChar != 'n')
                    {
                        throw new InvalidOperationException(
                            $"PadesIncrementalUpdateWriter: catalog object {catalogObjectNumber} is marked free in xref.");
                    }

                    return long.Parse(offsetStr, CultureInfo.InvariantCulture);
                }

                cursor += 20;
            }
        }

        throw new InvalidOperationException(
            $"PadesIncrementalUpdateWriter: catalog object {catalogObjectNumber} not found in xref table.");
    }

    /// <summary>
    /// Reads the catalog indirect object starting at <paramref name="catalogOffset"/>
    /// and returns the bytes of its dictionary body (everything inside the outer
    /// <c>&lt;&lt; ... &gt;&gt;</c>, exclusive of the delimiters).
    /// </summary>
    private static (byte[] DictBody, long DictBodyOffset) ReadCatalogDictBody(
        byte[] pdf, long catalogOffset, int expectedObjNum)
    {
        // Expect "<num> 0 obj" header. Validate.
        var (objNum, afterObjNum) = ReadAsciiInteger(pdf, catalogOffset);
        if (objNum != expectedObjNum)
        {
            throw new InvalidOperationException(
                $"PadesIncrementalUpdateWriter: object at offset {catalogOffset} has number {objNum}, "
                + $"expected catalog {expectedObjNum}.");
        }

        var cursor = SkipWhitespace(pdf, afterObjNum);
        // Skip generation number.
        var (_, afterGen) = ReadAsciiInteger(pdf, cursor);
        cursor = SkipWhitespace(pdf, afterGen);

        if (!StartsWithAt(pdf, cursor, "obj"u8))
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: catalog object header is malformed (no 'obj' keyword).");
        }
        cursor += 3;
        cursor = SkipWhitespace(pdf, cursor);

        if (!StartsWithAt(pdf, cursor, DictOpen))
        {
            throw new InvalidOperationException(
                "PadesIncrementalUpdateWriter: catalog object body does not begin with '<<'. "
                + "Stream-form catalog objects are not supported.");
        }

        var dictOpenIdx = cursor;
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);

        var bodyStart = dictOpenIdx + 2;
        var bodyLength = (int)(dictCloseIdx - bodyStart);
        var body = new byte[bodyLength];
        Buffer.BlockCopy(pdf, (int)bodyStart, body, 0, bodyLength);
        return (body, bodyStart);
    }

    /// <summary>
    /// Returns the offset of the outermost matching <c>&gt;&gt;</c> for the
    /// <c>&lt;&lt;</c> at <paramref name="openOffset"/>, tracking depth so nested dicts
    /// don't confuse the scan.
    /// </summary>
    private static long FindMatchingDictClose(byte[] pdf, long openOffset)
    {
        if (!StartsWithAt(pdf, openOffset, DictOpen))
        {
            throw new InvalidOperationException(
                $"PadesIncrementalUpdateWriter: expected '<<' at offset {openOffset}.");
        }

        var depth = 1;
        var cursor = openOffset + 2;
        while (cursor < pdf.Length - 1)
        {
            // Skip over PDF string literals "(...)" and hex strings "<...>" so we don't
            // misinterpret an embedded '<' or '>'.
            var b = pdf[cursor];
            if (b == (byte)'(')
            {
                cursor = SkipLiteralString(pdf, cursor);
                continue;
            }
            if (b == (byte)'<' && cursor + 1 < pdf.Length && pdf[cursor + 1] != (byte)'<')
            {
                cursor = SkipHexString(pdf, cursor);
                continue;
            }

            if (pdf[cursor] == (byte)'<' && pdf[cursor + 1] == (byte)'<')
            {
                depth++;
                cursor += 2;
                continue;
            }
            if (pdf[cursor] == (byte)'>' && pdf[cursor + 1] == (byte)'>')
            {
                depth--;
                if (depth == 0)
                {
                    return cursor;
                }
                cursor += 2;
                continue;
            }

            cursor++;
        }

        throw new InvalidOperationException(
            $"PadesIncrementalUpdateWriter: unmatched '<<' starting at offset {openOffset}.");
    }

    // ============================================================================
    // Appended-region builders — pure byte emission
    // ============================================================================

    private static byte[] BuildRevisedCatalogObject(int objectNumber, byte[] originalDictBody, long dssObjNum)
    {
        // We INSERT "/DSS K 0 R\n" at the end of the original dict body (just before the
        // closing >> that we'll re-emit). Don't try to detect a pre-existing /DSS — the
        // signing pipeline produces a catalog without one; if a future call layers a
        // second DSS update, that's a v1.3 concern.
        var sb = new StringBuilder();
        sb.Append(objectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n");
        sb.Append("<<");
        sb.Append(Encoding.Latin1.GetString(originalDictBody));
        // Make sure there's whitespace between the original body and our new key.
        if (originalDictBody.Length > 0 && !IsWhitespace(originalDictBody[^1]))
        {
            sb.Append('\n');
        }
        sb.Append("/DSS ");
        sb.Append(dssObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] BuildDssObject(long dssObjNum, long[] certObjNums, long[] ocspObjNums, long[] crlObjNums)
    {
        var sb = new StringBuilder();
        sb.Append(dssObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n<<\n");

        if (certObjNums.Length > 0)
        {
            sb.Append("/Certs [");
            for (var i = 0; i < certObjNums.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(certObjNums[i].ToString(CultureInfo.InvariantCulture));
                sb.Append(" 0 R");
            }
            sb.Append("]\n");
        }

        if (ocspObjNums.Length > 0)
        {
            sb.Append("/OCSPs [");
            for (var i = 0; i < ocspObjNums.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(ocspObjNums[i].ToString(CultureInfo.InvariantCulture));
                sb.Append(" 0 R");
            }
            sb.Append("]\n");
        }

        if (crlObjNums.Length > 0)
        {
            sb.Append("/CRLs [");
            for (var i = 0; i < crlObjNums.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(crlObjNums[i].ToString(CultureInfo.InvariantCulture));
                sb.Append(" 0 R");
            }
            sb.Append("]\n");
        }

        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] BuildStreamObject(long objNum, byte[] payload)
    {
        var header = Encoding.Latin1.GetBytes(
            $"{objNum.ToString(CultureInfo.InvariantCulture)} 0 obj\n"
            + $"<< /Length {payload.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n");
        var trailer = "\nendstream\nendobj\n"u8.ToArray();

        var result = new byte[header.Length + payload.Length + trailer.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(payload, 0, result, header.Length, payload.Length);
        Buffer.BlockCopy(trailer, 0, result, header.Length + payload.Length, trailer.Length);
        return result;
    }

    private static byte[] BuildXrefAndTrailer(
        int catalogObjectNumber,
        long catalogOffset,
        long dssObjNum,
        long dssOffset,
        long streamFirstObjNum,
        long[] streamOffsets,
        int previousSize,
        long newSize,
        long previousXrefOffset,
        long newXrefOffset)
    {
        var sb = new StringBuilder();
        sb.Append("xref\n");
        // Subsection 1: object 0 free head (required so any reader doing subsection
        // arithmetic stays sane).
        sb.Append("0 1\n");
        sb.Append("0000000000 65535 f \n");

        // Subsection 2: revised catalog (single object).
        sb.Append(catalogObjectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 1\n");
        AppendXrefEntry(sb, catalogOffset);

        // Subsection 3: new DSS + stream objects, contiguous starting at dssObjNum.
        var contiguousCount = 1 + streamOffsets.Length;
        sb.Append(dssObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(contiguousCount.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        AppendXrefEntry(sb, dssOffset);
        foreach (var offset in streamOffsets)
        {
            AppendXrefEntry(sb, offset);
        }

        // Trailer.
        sb.Append("trailer\n<<\n");
        sb.Append("/Size ");
        sb.Append(newSize.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("/Prev ");
        sb.Append(previousXrefOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("/Root ");
        sb.Append(catalogObjectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\n");

        sb.Append("startxref\n");
        sb.Append(newXrefOffset.ToString(CultureInfo.InvariantCulture));
        sb.Append('\n');
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void AppendXrefEntry(StringBuilder sb, long offset)
    {
        // 10-digit zero-padded offset, space, 5-digit zero-padded generation, space, n, space, \n
        sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append("00000");
        sb.Append(" n \n");
    }

    // ============================================================================
    // Low-level byte helpers
    // ============================================================================

    private static bool NeedsLeadingNewline(byte[] pdf)
    {
        if (pdf.Length == 0) return false;
        var last = pdf[^1];
        return last != (byte)'\n' && last != (byte)'\r';
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle, int start, int length)
    {
        for (var i = start + length - needle.Length; i >= start; i--)
        {
            if (MatchesAt(haystack, i, needle))
            {
                return i;
            }
        }
        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, long start, long length)
    {
        var end = start + length - needle.Length;
        for (var i = start; i <= end; i++)
        {
            if (MatchesAt(haystack, (int)i, needle))
            {
                return (int)i;
            }
        }
        return -1;
    }

    private static bool MatchesAt(byte[] haystack, int offset, byte[] needle)
    {
        if (offset + needle.Length > haystack.Length) return false;
        for (var i = 0; i < needle.Length; i++)
        {
            if (haystack[offset + i] != needle[i]) return false;
        }
        return true;
    }

    private static bool StartsWithAt(byte[] haystack, long offset, ReadOnlySpan<byte> needle)
    {
        if (offset < 0 || offset + needle.Length > haystack.Length) return false;
        for (var i = 0; i < needle.Length; i++)
        {
            if (haystack[(int)(offset + i)] != needle[i]) return false;
        }
        return true;
    }

    private static long SkipWhitespace(byte[] pdf, long cursor)
    {
        while (cursor < pdf.Length && IsWhitespace(pdf[(int)cursor]))
        {
            cursor++;
        }
        return cursor;
    }

    private static long SkipToLineEnd(byte[] pdf, long cursor)
    {
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'\n' && pdf[(int)cursor] != (byte)'\r')
        {
            cursor++;
        }
        return cursor;
    }

    private static long SkipLineEnd(byte[] pdf, long cursor)
    {
        // Accept \r, \n, or \r\n
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\r') cursor++;
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\n') cursor++;
        return cursor;
    }

    private static bool IsWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == 0 || b == 0x0C;

    private static (long Value, long AfterCursor) ReadAsciiInteger(byte[] pdf, long cursor)
    {
        var start = cursor;
        while (cursor < pdf.Length && pdf[(int)cursor] >= (byte)'0' && pdf[(int)cursor] <= (byte)'9')
        {
            cursor++;
        }
        if (cursor == start)
        {
            throw new InvalidOperationException(
                $"PadesIncrementalUpdateWriter: expected ASCII digit at offset {start}, found 0x{pdf[(int)start]:X2}.");
        }
        var digits = AsciiSubstring(pdf, start, (int)(cursor - start));
        return (long.Parse(digits, CultureInfo.InvariantCulture), cursor);
    }

    private static string AsciiSubstring(byte[] pdf, long offset, int length)
        => Encoding.Latin1.GetString(pdf, (int)offset, length);

    /// <summary>Skips a PDF literal string "(..)" handling balanced parens + escapes.</summary>
    private static long SkipLiteralString(byte[] pdf, long openOffset)
    {
        var depth = 1;
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && depth > 0)
        {
            var b = pdf[(int)cursor];
            if (b == (byte)'\\')
            {
                cursor += 2; // skip escape sequence
                continue;
            }
            if (b == (byte)'(') depth++;
            else if (b == (byte)')') depth--;
            cursor++;
        }
        return cursor;
    }

    /// <summary>Skips a PDF hex string "&lt;...&gt;" — single-angle form, not dict.</summary>
    private static long SkipHexString(byte[] pdf, long openOffset)
    {
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'>')
        {
            cursor++;
        }
        return cursor + 1;
    }
}
