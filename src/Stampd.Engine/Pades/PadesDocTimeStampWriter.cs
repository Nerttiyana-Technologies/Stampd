using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Stampd.Core.Sealing;

namespace Stampd.Engine.Pades;

/// <summary>
/// v1.3 #132 — appends a PAdES <i>Document Timestamp</i> (PDF /DocTimeStamp signature
/// dictionary) to a finished signed PDF as a strictly additive incremental update revision,
/// per ETSI EN 319 142-1 §5.4 (PAdES baseline B-LTA conformance level).
/// </summary>
/// <remarks>
/// <para>
/// A Document Timestamp is structurally a second signature on the document — but instead
/// of signing user data, it carries an RFC 3161 timestamp token over the entire prior PDF
/// (including the original B-T signature AND the DSS revocation material appended by
/// <see cref="PadesIncrementalUpdateWriter"/>). This anchors everything — signature,
/// timestamp, revocation info, document content — to a fresh TSA assertion, so the
/// composite stays verifiable even after the original signer and TSA certificates expire.
/// </para>
/// <para>
/// <b>Why Document Timestamps over CMS archive-time-stamp-v3.</b> v1.2 implemented B-LTA
/// as an <c>id-aa-ets-archiveTimestampV3</c> CMS unsigned attribute on the original
/// signer (Part 2 era; v1.3 #131 made the imprint strict). ETSI EN 319 142-1 Part 4 §5.4
/// explicitly defines B-LTA conformance via <i>Document Timestamps</i> instead — a
/// separate PDF signature with <c>/Type /DocTimeStamp</c> and <c>/SubFilter
/// /ETSI.RFC3161</c>. Stampd now emits BOTH: belt-and-suspenders Part 2 + Part 4 coverage
/// at the cost of one extra TSA round-trip per B-LTA signing.
/// </para>
/// <para>
/// <b>Wire format.</b> The appended revision contains:
/// </para>
/// <list type="bullet">
///   <item>A revised <c>/AcroForm</c> indirect object whose <c>/Fields</c> array gains a
///   reference to the new document-timestamp signature widget.</item>
///   <item>An invisible signature widget annotation (<c>/Subtype /Widget /FT /Sig /Rect
///   [0 0 0 0]</c>) whose <c>/V</c> entry points at the signature dict below.</item>
///   <item>A signature dictionary with <c>/Type /DocTimeStamp</c>, <c>/Filter
///   /Adobe.PPKLite</c>, <c>/SubFilter /ETSI.RFC3161</c>, a four-segment <c>/ByteRange</c>
///   covering everything except the <c>/Contents</c> value, and <c>/Contents</c> filled
///   with the RFC 3161 TST DER hex-encoded.</item>
/// </list>
/// <para>
/// <b>Implementation strategy.</b> Same byte-level approach as
/// <see cref="PadesIncrementalUpdateWriter"/>: no PdfSharp on the write path (it would
/// re-serialize and invalidate the original signature). We build the revision with
/// fixed-width placeholders for <c>/ByteRange</c> numbers and a zero-filled hex
/// placeholder for <c>/Contents</c>, then patch both in place after computing the
/// ByteRange digest and obtaining the TST from the TSA.
/// </para>
/// </remarks>
internal static class PadesDocTimeStampWriter
{
    // ---- Spec constants ----

    /// <summary>
    /// Reserved hex length for the /Contents placeholder. 16,384 hex chars = 8 KiB of
    /// binary TST. Real-world RFC 3161 TSTs run 2–8 KiB; this gives generous headroom
    /// without bloating the file. PdfSharp uses 8 KiB for the v1.2 archive TST as well.
    /// </summary>
    private const int ContentsHexLength = 16384;

    /// <summary>
    /// Width of each /ByteRange number as a fixed-width placeholder. 10 chars holds any
    /// signed-32-bit positive offset (up to ~2.1 GB), which is plenty for any PDF a
    /// signing pipeline produces. Right-aligned, space-padded.
    /// </summary>
    private const int ByteRangeNumberWidth = 10;

    // ---- Shared keyword buffers (avoid per-call allocation) ----

    private static readonly byte[] StartxrefKeyword = "startxref"u8.ToArray();
    private static readonly byte[] TrailerKeyword = "trailer"u8.ToArray();
    private static readonly byte[] XrefKeyword = "xref"u8.ToArray();
    private static readonly byte[] DictOpen = "<<"u8.ToArray();

    /// <summary>
    /// Builds the document-timestamp revision, hashes the ByteRange-covered bytes, calls
    /// the TSA, patches the placeholder with the returned TST DER, and returns the full
    /// (original + appended) bytes.
    /// </summary>
    /// <param name="pdfBytes">
    /// The signed PDF, optionally already extended with a DSS revision by
    /// <see cref="PadesIncrementalUpdateWriter"/>. The Document Timestamp covers
    /// everything in here.
    /// </param>
    /// <param name="tsa">RFC 3161 TSA provider that will sign the ByteRange digest.</param>
    /// <param name="hashAlgorithm">
    /// Hash to use for the ByteRange digest. Same algorithm the engine uses for the
    /// rest of the signature so the verifier doesn't have to juggle hash agility.
    /// </param>
    /// <param name="ct">Cancellation token plumbed through to the TSA call.</param>
    public static async Task<byte[]> AppendAsync(
        byte[] pdfBytes,
        ITimestampAuthorityProvider tsa,
        HashAlgorithmName hashAlgorithm,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(tsa);

        // ---- 1. Parse the existing PDF structure ----
        var previousXrefOffset = FindPreviousXrefOffset(pdfBytes);
        var (catalogObjectNumber, previousSize) = ReadTrailerRootAndSize(pdfBytes, previousXrefOffset);
        var catalogOffset = ReadObjectOffsetFromXref(pdfBytes, previousXrefOffset, catalogObjectNumber);
        var catalogDictBody = ReadObjectDictBody(pdfBytes, catalogOffset, catalogObjectNumber);

        // /AcroForm in PdfSharp 6.x output can be either indirect (`/AcroForm N N R`)
        // or inline (`/AcroForm <<...>>`). Resolve to the dict body bytes either way
        // — we always emit a new indirect /AcroForm and revise the catalog to point
        // at it, so the original AcroForm gets orphaned (harmless under PDF
        // incremental-update semantics: /Prev xref still finds the old version but
        // catalog -> AcroForm follows the new chain).
        var existingAcroFormBody = ResolveAcroFormBody(pdfBytes, previousXrefOffset, catalogDictBody);

        // ---- 2. Allocate object numbers ----
        // newAcroFormObjNum: holds the revised /AcroForm dict (new indirect object)
        // widgetObjNum:      holds the invisible /DocTimeStamp widget annotation
        // sigDictObjNum:     holds the actual /DocTimeStamp signature dict (sig field /V)
        // catalog is revised IN PLACE at catalogObjectNumber (same number, new content)
        var newAcroFormObjNum = previousSize;
        var widgetObjNum = previousSize + 1;
        var sigDictObjNum = previousSize + 2;

        // ---- 3. Build the new objects ----

        using var appended = new MemoryStream();
        var leadingNewline = NeedsLeadingNewline(pdfBytes);
        if (leadingNewline)
        {
            appended.WriteByte((byte)'\n');
        }

        var appendedRegionStart = pdfBytes.Length + (leadingNewline ? 1 : 0);

        // Revised catalog: re-emits the catalog at its original object number with
        // /AcroForm replaced by an indirect reference to our new AcroForm object.
        // This handles both inline-form catalogs (where /AcroForm <<...>> existed) and
        // indirect-form catalogs (where /AcroForm pointed to a now-orphaned object).
        var revisedCatalogOffset = appendedRegionStart + (int)appended.Position;
        var revisedCatalog = BuildRevisedCatalogObject(
            catalogObjectNumber,
            catalogDictBody,
            newAcroFormObjNum: newAcroFormObjNum);
        appended.Write(revisedCatalog, 0, revisedCatalog.Length);

        // New /AcroForm indirect object: contains the existing dict body (with /Fields
        // array patched to include our new widget) plus /SigFlags 3 if not already set.
        var revisedAcroFormOffset = appendedRegionStart + (int)appended.Position;
        var revisedAcroForm = BuildRevisedAcroFormObject(
            newAcroFormObjNum,
            existingAcroFormBody,
            newFieldRefObjNum: widgetObjNum);
        appended.Write(revisedAcroForm, 0, revisedAcroForm.Length);

        // Widget annotation (invisible — Rect [0 0 0 0]). Refers to the sig dict via /V.
        var widgetOffset = appendedRegionStart + (int)appended.Position;
        var widget = BuildWidgetAnnotation(widgetObjNum, sigDictObjNum);
        appended.Write(widget, 0, widget.Length);

        // Signature dict — this is the /DocTimeStamp. Built with placeholders for
        // /ByteRange numbers and /Contents hex. We capture the absolute file offsets
        // of those placeholders so we can patch them post-construction.
        var sigDictOffsetInFinal = appendedRegionStart + (int)appended.Position;
        var (sigDictBytes, byteRangePlaceholderOffsetInSigDict, contentsPlaceholderOffsetInSigDict) =
            BuildDocTimeStampSigDict(sigDictObjNum);
        appended.Write(sigDictBytes, 0, sigDictBytes.Length);

        // ---- 4. Append xref + trailer + EOF ----

        var newXrefOffset = appendedRegionStart + (int)appended.Position;
        var newSize = sigDictObjNum + 1;

        var xrefAndTrailer = BuildXrefAndTrailer(
            catalogObjectNumber: catalogObjectNumber,
            catalogOffset: revisedCatalogOffset,
            newAcroFormObjNum: newAcroFormObjNum,
            newAcroFormOffset: revisedAcroFormOffset,
            widgetObjNum: widgetObjNum,
            widgetOffset: widgetOffset,
            sigDictObjNum: sigDictObjNum,
            sigDictOffset: sigDictOffsetInFinal,
            previousXrefOffset: previousXrefOffset,
            newXrefOffset: newXrefOffset,
            newSize: newSize);
        appended.Write(xrefAndTrailer, 0, xrefAndTrailer.Length);

        // ---- 5. Concatenate original + appended ----
        var combined = new byte[pdfBytes.Length + (int)appended.Length];
        Buffer.BlockCopy(pdfBytes, 0, combined, 0, pdfBytes.Length);
        var appendedBytes = appended.ToArray();
        Buffer.BlockCopy(appendedBytes, 0, combined, pdfBytes.Length, appendedBytes.Length);

        // ---- 6. Patch /ByteRange numbers ----

        // The /Contents value occupies <reserved hex> from offset C_start to C_end.
        // /ByteRange = [0 length1 C_end length2] where:
        //   length1 = bytes [0..C_start), i.e., everything BEFORE the < of /Contents
        //   length2 = bytes [C_end..EOF), i.e., everything AFTER the > of /Contents
        // C_start is the absolute file offset of the '<' opening the /Contents value.
        // C_end is the absolute file offset just past the '>' closing it.
        var sigDictAbsoluteOffset = pdfBytes.Length + (leadingNewline ? 1 : 0) +
            (sigDictOffsetInFinal - appendedRegionStart);
        var contentsStartInFile = sigDictAbsoluteOffset + contentsPlaceholderOffsetInSigDict;
        // Placeholder hex sits between '<' and '>'; ContentsHexLength counts only the
        // hex characters, so the closing '>' is at contentsStart + 1 + ContentsHexLength.
        var contentsEndInFile = contentsStartInFile + 1 + ContentsHexLength + 1; // include both delimiters
        // /ByteRange entries cover bytes BEFORE the '<' and AFTER the '>'.
        var byteRange0 = 0L;
        var byteRange1 = contentsStartInFile;
        var byteRange2 = contentsEndInFile;
        var byteRange3 = combined.Length - contentsEndInFile;

        PatchByteRangeNumbers(
            combined,
            byteRangePlaceholderAbsoluteOffset: sigDictAbsoluteOffset + byteRangePlaceholderOffsetInSigDict,
            byteRange0, byteRange1, byteRange2, byteRange3);

        // ---- 7. Compute the ByteRange digest, call TSA, patch /Contents ----

        using var hasher = CreateHasher(hashAlgorithm);
        hasher.TransformBlock(combined, 0, (int)byteRange1, null, 0);
        hasher.TransformFinalBlock(combined, (int)byteRange2, (int)byteRange3);
        var digest = hasher.Hash!;

        // The TSA contract: pass the bytes to be timestamped; the provider hashes them
        // with hashAlgorithm and wraps the digest in an RFC 3161 TimeStampReq. So we
        // need to hand it bytes that, when hashed, produce `digest`. Since we already
        // have `digest`, we'd need to either (a) pass `digest` as raw bytes (the TSA
        // would then double-hash) or (b) re-hand the concatenated bytes. Re-handing is
        // semantically cleaner and matches the existing CMS-archive-TST path.
        var byteRangeBytes = ConcatenateByteRange(combined, byteRange1, byteRange2, byteRange3);
        var tstBytes = await tsa
            .RequestTimestampAsync(byteRangeBytes, hashAlgorithm, ct)
            .ConfigureAwait(false);

        if (tstBytes.Length * 2 > ContentsHexLength)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: TST is {tstBytes.Length} bytes; placeholder reserves "
                + $"{ContentsHexLength / 2} bytes. Increase ContentsHexLength.");
        }

        PatchContentsHex(combined, (int)(contentsStartInFile + 1), tstBytes);

        return combined;
    }

    // ============================================================================
    // Builders
    // ============================================================================

    /// <summary>
    /// Resolves the existing /AcroForm dict body bytes from the catalog. Handles both
    /// indirect (<c>/AcroForm N N R</c>) and inline (<c>/AcroForm &lt;&lt;...&gt;&gt;</c>)
    /// forms. Returns an empty body when no /AcroForm exists — the caller synthesizes
    /// a fresh one with just our new field.
    /// </summary>
    private static byte[] ResolveAcroFormBody(byte[] pdf, long previousXrefOffset, byte[] catalogDictBody)
    {
        // Try indirect first — most common when the original signing pass created an
        // /AcroForm via PdfSharp's Internals.AddObject.
        var indirect = FindIndirectReferenceInDict(catalogDictBody, "/AcroForm");
        if (indirect is not null)
        {
            var offset = ReadObjectOffsetFromXref(pdf, previousXrefOffset, indirect.Value);
            return ReadObjectDictBody(pdf, offset, indirect.Value);
        }

        // Try inline. PdfSharp 6.x is known to emit `/AcroForm<< ... >>` directly in the
        // catalog body. We scan for the key, then for the opening `<<` that follows it,
        // then walk to the matching `>>` using a nesting-aware scanner so an inline
        // /AcroForm dict containing nested dicts (the typical /DR, /Fields nesting) is
        // captured in full rather than truncated at the first `>>`.
        var inlineBody = ExtractInlineDictAfterKey(catalogDictBody, "/AcroForm");
        if (inlineBody is not null)
        {
            return inlineBody;
        }

        // No /AcroForm at all — return empty body so BuildRevisedAcroFormObject
        // synthesizes a minimal one. Rare in practice because the original signing
        // pass always creates an AcroForm, but defensive against synthetic test PDFs.
        return [];
    }

    /// <summary>
    /// Walks <paramref name="dictBody"/> looking for <paramref name="key"/> followed by
    /// a <c>&lt;&lt;</c>, then returns the bytes between that <c>&lt;&lt;</c> and its
    /// matching <c>&gt;&gt;</c>. Returns null if the key isn't found or isn't followed
    /// by an inline dict.
    /// </summary>
    private static byte[]? ExtractInlineDictAfterKey(byte[] dictBody, string key)
    {
        var bodyAscii = Encoding.Latin1.GetString(dictBody);
        var keyIdx = bodyAscii.IndexOf(key, StringComparison.Ordinal);
        if (keyIdx < 0) return null;

        // Skip whitespace after the key.
        var cursor = keyIdx + key.Length;
        while (cursor < bodyAscii.Length && IsWhitespace((byte)bodyAscii[cursor])) cursor++;

        if (cursor + 1 >= bodyAscii.Length || bodyAscii[cursor] != '<' || bodyAscii[cursor + 1] != '<')
        {
            return null;
        }

        // Walk forward looking for matching >>. Use a depth counter to handle nested
        // dicts inside the inline AcroForm (it almost certainly contains /DR <<...>>).
        var depth = 1;
        var scan = cursor + 2;
        while (scan < bodyAscii.Length - 1)
        {
            // Skip literal/hex strings to avoid mistaking embedded '<' or '>' for dict
            // delimiters — same defensive scanning as FindMatchingDictClose.
            var ch = bodyAscii[scan];
            if (ch == '(') { scan = (int)SkipLiteralString(dictBody, scan); continue; }
            if (ch == '<' && bodyAscii[scan + 1] != '<')
            {
                scan = (int)SkipHexString(dictBody, scan);
                continue;
            }
            if (ch == '<' && bodyAscii[scan + 1] == '<') { depth++; scan += 2; continue; }
            if (ch == '>' && bodyAscii[scan + 1] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    var bodyStart = cursor + 2;
                    var bodyLength = scan - bodyStart;
                    var body = new byte[bodyLength];
                    Buffer.BlockCopy(dictBody, bodyStart, body, 0, bodyLength);
                    return body;
                }
                scan += 2;
                continue;
            }
            scan++;
        }
        return null;
    }

    /// <summary>
    /// Re-emits the catalog object at its original number, with /AcroForm replaced by
    /// an indirect reference to <paramref name="newAcroFormObjNum"/>. The original
    /// /AcroForm value (whether inline dict, indirect ref, or absent) is stripped so
    /// the catalog has exactly one /AcroForm entry after revision.
    /// </summary>
    private static byte[] BuildRevisedCatalogObject(int catalogObjNum, byte[] originalDictBody, long newAcroFormObjNum)
    {
        var bodyAscii = Encoding.Latin1.GetString(originalDictBody);

        // Remove any existing /AcroForm entry. Try indirect form first, then inline.
        var indirectPattern = new System.Text.RegularExpressions.Regex(
            @"/AcroForm\s+\d+\s+\d+\s+R\s*");
        if (indirectPattern.IsMatch(bodyAscii))
        {
            bodyAscii = indirectPattern.Replace(bodyAscii, string.Empty, count: 1);
        }
        else
        {
            // Inline — find /AcroForm <<...>> and excise it.
            var keyIdx = bodyAscii.IndexOf("/AcroForm", StringComparison.Ordinal);
            if (keyIdx >= 0)
            {
                // Find the opening << and its matching >>, mirroring ExtractInlineDictAfterKey.
                var cursor = keyIdx + "/AcroForm".Length;
                while (cursor < bodyAscii.Length && IsWhitespace((byte)bodyAscii[cursor])) cursor++;
                if (cursor + 1 < bodyAscii.Length && bodyAscii[cursor] == '<' && bodyAscii[cursor + 1] == '<')
                {
                    var depth = 1;
                    var scan = cursor + 2;
                    while (scan < bodyAscii.Length - 1 && depth > 0)
                    {
                        if (bodyAscii[scan] == '<' && bodyAscii[scan + 1] == '<') { depth++; scan += 2; continue; }
                        if (bodyAscii[scan] == '>' && bodyAscii[scan + 1] == '>') { depth--; scan += 2; continue; }
                        scan++;
                    }
                    // Excise [keyIdx, scan).
                    bodyAscii = bodyAscii[..keyIdx] + bodyAscii[scan..];
                }
            }
        }

        // Append new /AcroForm indirect reference.
        var sb = new StringBuilder();
        sb.Append(catalogObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n<<");
        sb.Append(bodyAscii);
        if (bodyAscii.Length > 0 && !IsWhitespace((byte)bodyAscii[^1]))
        {
            sb.Append('\n');
        }
        sb.Append("/AcroForm ");
        sb.Append(newAcroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Re-emits the /AcroForm indirect object with the new widget appended to /Fields.
    /// Preserves all other dict entries verbatim — the form may already have /SigFlags,
    /// /DA, /DR, /Q, etc. set by the original signing pass.
    /// </summary>
    private static byte[] BuildRevisedAcroFormObject(
        long acroFormObjNum,
        byte[] originalDictBody,
        long newFieldRefObjNum)
    {
        var bodyAscii = Encoding.Latin1.GetString(originalDictBody);

        // Patch /Fields [ ... ] to append our new widget ref. PdfSharp emits compact
        // arrays like "/Fields[1 0 R]" or "/Fields [1 0 R 2 0 R]". Regex matches the
        // array and we insert before its ']'.
        var fieldsPattern = new System.Text.RegularExpressions.Regex(
            @"/Fields\s*\[(?<inner>[^\]]*)\]",
            System.Text.RegularExpressions.RegexOptions.None);

        string patchedBody;
        if (fieldsPattern.IsMatch(bodyAscii))
        {
            patchedBody = fieldsPattern.Replace(bodyAscii, match =>
            {
                var inner = match.Groups["inner"].Value.TrimEnd();
                return $"/Fields [{inner} {newFieldRefObjNum.ToString(CultureInfo.InvariantCulture)} 0 R]";
            }, count: 1);
        }
        else
        {
            // No /Fields yet — synthesize one. Defensive against empty input bodies
            // (no original /AcroForm at all). Also add /SigFlags 3 (1=read-only sig
            // appearance + 2=append-only mode) since the form would otherwise lack
            // the basic PAdES form metadata.
            var sigFlagsPart = bodyAscii.Contains("/SigFlags", StringComparison.Ordinal)
                ? string.Empty
                : "\n/SigFlags 3";
            patchedBody = bodyAscii + sigFlagsPart
                + $"\n/Fields [{newFieldRefObjNum.ToString(CultureInfo.InvariantCulture)} 0 R]";
        }

        var sb = new StringBuilder();
        sb.Append(acroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n<<");
        sb.Append(patchedBody);
        if (patchedBody.Length > 0 && !IsWhitespace((byte)patchedBody[^1]))
        {
            sb.Append('\n');
        }
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Invisible widget annotation referring to the signature dictionary. <c>Rect
    /// [0 0 0 0]</c> means zero area — Acrobat won't render any badge for the document
    /// timestamp, which is the expected UX (Document Timestamps are a security feature,
    /// not a visible signature).
    /// </summary>
    private static byte[] BuildWidgetAnnotation(long widgetObjNum, long sigDictObjNum)
    {
        var sb = new StringBuilder();
        sb.Append(widgetObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n");
        sb.Append("<<\n");
        sb.Append("/Type /Annot\n");
        sb.Append("/Subtype /Widget\n");
        sb.Append("/FT /Sig\n");
        sb.Append("/Rect [0 0 0 0]\n");
        // /T is the field name; PAdES doesn't mandate uniqueness rules beyond per-form
        // distinctness. Use a deterministic prefix + the object number so multiple
        // appends in sequence (future-proofing) don't collide.
        sb.Append("/T (Stampd.DocTimeStamp.");
        sb.Append(widgetObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(")\n");
        sb.Append("/F 132\n"); // Hidden + NoView + Print + Locked flag mix used for invisible sig widgets.
        sb.Append("/V ");
        sb.Append(sigDictObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 R\n");
        sb.Append(">>\nendobj\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the /DocTimeStamp signature dictionary indirect object with /ByteRange and
    /// /Contents placeholders. Returns the bytes PLUS the in-object offsets of each
    /// placeholder so the caller can compute absolute file offsets and patch them.
    /// </summary>
    private static (byte[] Bytes, int ByteRangePlaceholderOffset, int ContentsPlaceholderOffset)
        BuildDocTimeStampSigDict(long sigDictObjNum)
    {
        var sb = new StringBuilder();
        sb.Append(sigDictObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 0 obj\n");
        sb.Append("<<\n");
        sb.Append("/Type /DocTimeStamp\n");
        sb.Append("/Filter /Adobe.PPKLite\n");
        sb.Append("/SubFilter /ETSI.RFC3161\n");
        // /V is the version of the signature handler — PAdES doesn't strictly require
        // it on DocTimeStamps but every well-formed example we've seen includes it,
        // and Adobe Reader's strict validator looks for it.
        sb.Append("/V 0\n");

        // /ByteRange placeholder — fixed-width so we can patch numbers in place.
        // The placeholder hash is "/ByteRange [          0          0          0          0]"
        // (4 numbers, each right-aligned in a 10-char field). After patching, the values
        // become the real offsets.
        sb.Append("/ByteRange [");
        var byteRangeStartInSb = sb.Length;
        AppendByteRangePlaceholder(sb, 0); // placeholder #1
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #2
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #3
        sb.Append(' ');
        AppendByteRangePlaceholder(sb, 0); // placeholder #4
        sb.Append("]\n");

        // /Contents = <00...00> zero-filled hex string of length ContentsHexLength.
        sb.Append("/Contents ");
        var contentsStartInSb = sb.Length;
        sb.Append('<');
        sb.Append('0', ContentsHexLength);
        sb.Append('>');
        sb.Append('\n');

        sb.Append(">>\nendobj\n");

        // Convert builder to bytes; the in-object offsets we captured above are by char
        // index, which equals byte index since we're emitting pure Latin-1.
        var bytes = Encoding.Latin1.GetBytes(sb.ToString());
        return (bytes, byteRangeStartInSb, contentsStartInSb);
    }

    private static void AppendByteRangePlaceholder(StringBuilder sb, long value)
    {
        // Right-align in a ByteRangeNumberWidth-char field, space-padded.
        var s = value.ToString(CultureInfo.InvariantCulture);
        sb.Append(' ', ByteRangeNumberWidth - s.Length);
        sb.Append(s);
    }

    private static byte[] BuildXrefAndTrailer(
        int catalogObjectNumber,
        long catalogOffset,
        long newAcroFormObjNum,
        long newAcroFormOffset,
        long widgetObjNum,
        long widgetOffset,
        long sigDictObjNum,
        long sigDictOffset,
        long previousXrefOffset,
        long newXrefOffset,
        long newSize)
    {
        var sb = new StringBuilder();
        sb.Append("xref\n");

        // Subsection: free head (object 0).
        sb.Append("0 1\n0000000000 65535 f \n");

        // Subsection: revised catalog (single object, same number, new content).
        sb.Append(catalogObjectNumber.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 1\n");
        AppendXrefEntry(sb, catalogOffset);

        // Subsection: new /AcroForm + widget + sig dict, contiguous starting at
        // newAcroFormObjNum. We allocated them sequentially in AppendAsync so a
        // single subsection covers all three.
        sb.Append(newAcroFormObjNum.ToString(CultureInfo.InvariantCulture));
        sb.Append(" 3\n");
        AppendXrefEntry(sb, newAcroFormOffset);
        AppendXrefEntry(sb, widgetOffset);
        AppendXrefEntry(sb, sigDictOffset);

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
        sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture));
        sb.Append(" 00000 n \n");
    }

    // ============================================================================
    // Patching helpers — post-construction byte edits
    // ============================================================================

    /// <summary>
    /// Overwrites the four /ByteRange placeholder numbers in place. Each placeholder is
    /// a 10-char space-padded right-aligned ASCII number; we replace it with the actual
    /// value in the same width, keeping the total byte count (and therefore every other
    /// offset) unchanged.
    /// </summary>
    private static void PatchByteRangeNumbers(
        byte[] buffer,
        long byteRangePlaceholderAbsoluteOffset,
        long v0, long v1, long v2, long v3)
    {
        var p = (int)byteRangePlaceholderAbsoluteOffset;
        WriteByteRangeNumber(buffer, p, v0);
        WriteByteRangeNumber(buffer, p + ByteRangeNumberWidth + 1, v1);
        WriteByteRangeNumber(buffer, p + (ByteRangeNumberWidth + 1) * 2, v2);
        WriteByteRangeNumber(buffer, p + (ByteRangeNumberWidth + 1) * 3, v3);
    }

    private static void WriteByteRangeNumber(byte[] buffer, int offset, long value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        if (s.Length > ByteRangeNumberWidth)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: /ByteRange value {value} doesn't fit in {ByteRangeNumberWidth} chars.");
        }
        var padLen = ByteRangeNumberWidth - s.Length;
        for (var i = 0; i < padLen; i++) buffer[offset + i] = (byte)' ';
        for (var i = 0; i < s.Length; i++) buffer[offset + padLen + i] = (byte)s[i];
    }

    /// <summary>
    /// Replaces the zero-filled /Contents hex placeholder with the hex encoding of
    /// <paramref name="tstBytes"/>. Trailing hex characters that weren't used remain as
    /// '0' so the placeholder slot stays the same length.
    /// </summary>
    private static void PatchContentsHex(byte[] buffer, int hexStartOffset, byte[] tstBytes)
    {
        for (var i = 0; i < tstBytes.Length; i++)
        {
            var b = tstBytes[i];
            buffer[hexStartOffset + (i * 2)] = HexNibble(b >> 4);
            buffer[hexStartOffset + (i * 2) + 1] = HexNibble(b & 0x0F);
        }
        // Remaining hex characters keep their '0' fill from initial construction.
    }

    private static byte HexNibble(int v) => (byte)(v < 10 ? '0' + v : 'A' + (v - 10));

    private static byte[] ConcatenateByteRange(byte[] source, long len1, long start2, long len2)
    {
        var result = new byte[len1 + len2];
        Buffer.BlockCopy(source, 0, result, 0, (int)len1);
        Buffer.BlockCopy(source, (int)start2, result, (int)len1, (int)len2);
        return result;
    }

    private static HashAlgorithm CreateHasher(HashAlgorithmName algo) => algo.Name switch
    {
        "SHA256" => SHA256.Create(),
        "SHA384" => SHA384.Create(),
        "SHA512" => SHA512.Create(),
        _ => throw new NotSupportedException($"Unsupported hash algorithm: {algo.Name}"),
    };

    // ============================================================================
    // PDF parsing helpers — pared down from PadesIncrementalUpdateWriter for reuse.
    // Kept private here so the two writers can evolve independently; consolidation
    // into a shared PadesPdfBytes utility class is a v2.0 cleanup.
    // ============================================================================

    private static long FindPreviousXrefOffset(byte[] pdf)
    {
        var scanStart = Math.Max(0, pdf.Length - 1024);
        var keywordIdx = LastIndexOf(pdf, StartxrefKeyword, scanStart, pdf.Length - scanStart);
        if (keywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: input PDF has no 'startxref' marker in the last 1 KB.");
        }
        long cursor = keywordIdx + StartxrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);
        var (offset, _) = ReadAsciiInteger(pdf, cursor);
        return offset;
    }

    private static (int CatalogObjectNumber, int Size) ReadTrailerRootAndSize(byte[] pdf, long previousXrefOffset)
    {
        var trailerKeywordIdx = IndexOf(pdf, TrailerKeyword, previousXrefOffset, pdf.Length - previousXrefOffset);
        if (trailerKeywordIdx < 0)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: no 'trailer' keyword found after xref. "
                + "Hybrid xref-stream PDFs are not supported.");
        }

        var dictOpenIdx = IndexOf(pdf, DictOpen, trailerKeywordIdx, pdf.Length - trailerKeywordIdx);
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var dictBody = AsciiSubstring(pdf, dictOpenIdx + 2, (int)(dictCloseIdx - (dictOpenIdx + 2)));

        var rootMatch = System.Text.RegularExpressions.Regex.Match(dictBody, @"/Root\s+(\d+)\s+\d+\s+R");
        var sizeMatch = System.Text.RegularExpressions.Regex.Match(dictBody, @"/Size\s+(\d+)");

        if (!rootMatch.Success || !sizeMatch.Success)
        {
            throw new InvalidOperationException(
                "PadesDocTimeStampWriter: trailer missing /Root or /Size.");
        }
        return (
            int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static long ReadObjectOffsetFromXref(byte[] pdf, long xrefOffset, int objectNumber)
    {
        if (!StartsWithAt(pdf, xrefOffset, XrefKeyword))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: expected 'xref' keyword at offset {xrefOffset}.");
        }

        var cursor = xrefOffset + XrefKeyword.Length;
        cursor = SkipWhitespace(pdf, cursor);

        // Chain back through /Prev xrefs if needed. For our test inputs the object is
        // usually in the latest xref, but if it was set in an earlier revision (e.g.
        // /AcroForm created by the original signing pass and not touched since), we'll
        // need to follow /Prev. v1.3 supports a single hop — multi-hop is rare in
        // PdfSharp output but tracked as a future enhancement if it surfaces.
        while (cursor < pdf.Length)
        {
            if (StartsWithAt(pdf, cursor, TrailerKeyword))
            {
                // Try /Prev one hop back.
                var (_, prevOpt) = ReadPrevFromTrailer(pdf, cursor);
                if (prevOpt is not null)
                {
                    return ReadObjectOffsetFromXref(pdf, prevOpt.Value, objectNumber);
                }
                throw new InvalidOperationException(
                    $"PadesDocTimeStampWriter: object {objectNumber} not found in xref chain.");
            }

            var (firstObj, afterFirst) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipWhitespace(pdf, afterFirst);
            var (count, afterCount) = ReadAsciiInteger(pdf, cursor);
            cursor = SkipToLineEnd(pdf, afterCount);
            cursor = SkipLineEnd(pdf, cursor);

            for (var i = 0; i < count; i++)
            {
                var entryStart = cursor;
                var objNum = firstObj + i;
                if (objNum == objectNumber)
                {
                    var offsetStr = AsciiSubstring(pdf, entryStart, 10);
                    var inUseChar = (char)pdf[(int)(entryStart + 17)];
                    if (inUseChar == 'n')
                    {
                        return long.Parse(offsetStr, CultureInfo.InvariantCulture);
                    }
                }
                cursor += 20;
            }
        }

        throw new InvalidOperationException(
            $"PadesDocTimeStampWriter: object {objectNumber} not found in xref table.");
    }

    private static (string DictBody, long? PrevOffset) ReadPrevFromTrailer(byte[] pdf, long trailerOffset)
    {
        var dictOpenIdx = IndexOf(pdf, DictOpen, trailerOffset, pdf.Length - trailerOffset);
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var body = AsciiSubstring(pdf, dictOpenIdx + 2, (int)(dictCloseIdx - (dictOpenIdx + 2)));
        var prevMatch = System.Text.RegularExpressions.Regex.Match(body, @"/Prev\s+(\d+)");
        if (!prevMatch.Success) return (body, null);
        return (body, long.Parse(prevMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static byte[] ReadObjectDictBody(byte[] pdf, long objectOffset, int expectedObjNum)
    {
        var (objNum, afterObjNum) = ReadAsciiInteger(pdf, objectOffset);
        if (objNum != expectedObjNum)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object at offset {objectOffset} has number {objNum}, "
                + $"expected {expectedObjNum}.");
        }

        var cursor = SkipWhitespace(pdf, afterObjNum);
        var (_, afterGen) = ReadAsciiInteger(pdf, cursor);
        cursor = SkipWhitespace(pdf, afterGen);

        if (!StartsWithAt(pdf, cursor, "obj"u8))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object {expectedObjNum} header is malformed.");
        }
        cursor += 3;
        cursor = SkipWhitespace(pdf, cursor);

        if (!StartsWithAt(pdf, cursor, DictOpen))
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: object {expectedObjNum} body does not begin with '<<'.");
        }

        var dictOpenIdx = cursor;
        var dictCloseIdx = FindMatchingDictClose(pdf, dictOpenIdx);
        var bodyStart = dictOpenIdx + 2;
        var bodyLength = (int)(dictCloseIdx - bodyStart);
        var body = new byte[bodyLength];
        Buffer.BlockCopy(pdf, (int)bodyStart, body, 0, bodyLength);
        return body;
    }

    private static int? FindIndirectReferenceInDict(byte[] dictBody, string key)
    {
        var bodyAscii = Encoding.Latin1.GetString(dictBody);
        var pattern = $@"{System.Text.RegularExpressions.Regex.Escape(key)}\s+(\d+)\s+\d+\s+R";
        var match = System.Text.RegularExpressions.Regex.Match(bodyAscii, pattern);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static long FindMatchingDictClose(byte[] pdf, long openOffset)
    {
        var depth = 1;
        var cursor = openOffset + 2;
        while (cursor < pdf.Length - 1)
        {
            var b = pdf[(int)cursor];
            if (b == (byte)'(')
            {
                cursor = SkipLiteralString(pdf, cursor);
                continue;
            }
            if (b == (byte)'<' && cursor + 1 < pdf.Length && pdf[(int)(cursor + 1)] != (byte)'<')
            {
                cursor = SkipHexString(pdf, cursor);
                continue;
            }
            if (pdf[(int)cursor] == (byte)'<' && pdf[(int)(cursor + 1)] == (byte)'<')
            {
                depth++; cursor += 2; continue;
            }
            if (pdf[(int)cursor] == (byte)'>' && pdf[(int)(cursor + 1)] == (byte)'>')
            {
                depth--;
                if (depth == 0) return cursor;
                cursor += 2; continue;
            }
            cursor++;
        }
        throw new InvalidOperationException(
            $"PadesDocTimeStampWriter: unmatched '<<' at offset {openOffset}.");
    }

    private static long SkipLiteralString(byte[] pdf, long openOffset)
    {
        var depth = 1;
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && depth > 0)
        {
            var b = pdf[(int)cursor];
            if (b == (byte)'\\') { cursor += 2; continue; }
            if (b == (byte)'(') depth++;
            else if (b == (byte)')') depth--;
            cursor++;
        }
        return cursor;
    }

    private static long SkipHexString(byte[] pdf, long openOffset)
    {
        var cursor = openOffset + 1;
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'>') cursor++;
        return cursor + 1;
    }

    // ---- Tiny scanner primitives ----

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
            if (MatchesAt(haystack, i, needle)) return i;
        }
        return -1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, long start, long length)
    {
        var end = start + length - needle.Length;
        for (var i = start; i <= end; i++)
        {
            if (MatchesAt(haystack, (int)i, needle)) return (int)i;
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
        while (cursor < pdf.Length && IsWhitespace(pdf[(int)cursor])) cursor++;
        return cursor;
    }

    private static long SkipToLineEnd(byte[] pdf, long cursor)
    {
        while (cursor < pdf.Length && pdf[(int)cursor] != (byte)'\n' && pdf[(int)cursor] != (byte)'\r') cursor++;
        return cursor;
    }

    private static long SkipLineEnd(byte[] pdf, long cursor)
    {
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\r') cursor++;
        if (cursor < pdf.Length && pdf[(int)cursor] == (byte)'\n') cursor++;
        return cursor;
    }

    private static bool IsWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == 0 || b == 0x0C;

    private static (long Value, long AfterCursor) ReadAsciiInteger(byte[] pdf, long cursor)
    {
        var start = cursor;
        while (cursor < pdf.Length && pdf[(int)cursor] >= (byte)'0' && pdf[(int)cursor] <= (byte)'9') cursor++;
        if (cursor == start)
        {
            throw new InvalidOperationException(
                $"PadesDocTimeStampWriter: expected digit at offset {start}.");
        }
        var digits = AsciiSubstring(pdf, start, (int)(cursor - start));
        return (long.Parse(digits, CultureInfo.InvariantCulture), cursor);
    }

    private static string AsciiSubstring(byte[] pdf, long offset, int length)
        => Encoding.Latin1.GetString(pdf, (int)offset, length);
}
