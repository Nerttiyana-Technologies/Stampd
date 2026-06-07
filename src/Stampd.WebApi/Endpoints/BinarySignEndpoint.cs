using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

using Stampd.Core;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Endpoints;

internal static class BinarySignEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };


    /// <summary>
    /// Multipart variant of /api/sign:
    /// <list type="bullet">
    ///   <item><c>pdf</c> file part — the source PDF (raw bytes, no base64 overhead).</item>
    ///   <item><c>request</c> form/JSON part — DTO without <c>SourcePdfBase64</c>; field
    ///         values supplied as inline text or as additional file parts named
    ///         <c>field-{index}</c>.</item>
    /// </list>
    /// Returns the signed PDF as <c>application/pdf</c> with a filename in
    /// <c>Content-Disposition</c>. Avoids the ~33% base64 inflation on both directions.
    /// </summary>
    public static IEndpointRouteBuilder MapBinarySign(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/sign/binary", HandleAsync)
            .WithName("SignBinary")
            .WithTags("Sign")
            .WithSummary("Multipart upload of a PDF + JSON sign request; returns raw application/pdf.")
            .DisableAntiforgery();

        return builder;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        [FromServices] IStampdEngine engine,
        [FromServices] Services.PadesDefaults padesDefaults,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.Problem(
                title: "Expected multipart/form-data content.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        var pdfFile = form.Files["pdf"];
        if (pdfFile is null || pdfFile.Length == 0)
        {
            return Results.Problem("Missing 'pdf' file part.", statusCode: 400);
        }

        var requestJson = form["request"].ToString();
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return Results.Problem("Missing 'request' form field (JSON SignRequestBody, sans SourcePdfBase64).", statusCode: 400);
        }

        BinarySignFormBody? body;
        try
        {
            body = JsonSerializer.Deserialize<BinarySignFormBody>(requestJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Results.Problem($"Could not parse 'request' JSON: {ex.Message}", statusCode: 400);
        }

        if (body is null)
        {
            return Results.Problem("'request' deserialized to null.", statusCode: 400);
        }

        byte[] sourcePdf;
        using (var ms = new MemoryStream())
        {
            await using var input = pdfFile.OpenReadStream();
            await input.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            sourcePdf = ms.ToArray();
        }

        var fields = body.Fields.Select(f =>
            new SignatureField(
                f.PageNumber,
                new PercentageRect(f.Bounds.X, f.Bounds.Y, f.Bounds.Width, f.Bounds.Height),
                f.Kind,
                f.SignerId))
            .ToArray();

        var fieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(body.FieldValues?.Count ?? 0);
        if (body.FieldValues is not null)
        {
            foreach (var (idx, value) in body.FieldValues)
            {
                if (value.Text is { } t)
                {
                    fieldValues[idx] = System.Text.Encoding.UTF8.GetBytes(t);
                }
                else if (value.ImageBase64 is { Length: > 0 } b64)
                {
                    fieldValues[idx] = Convert.FromBase64String(b64);
                }
            }
        }

        // Allow image fields via additional file parts: field-0, field-1, etc.
        foreach (var file in form.Files.Where(f => f.Name.StartsWith("field-", StringComparison.Ordinal)))
        {
            if (!int.TryParse(file.Name["field-".Length..], System.Globalization.CultureInfo.InvariantCulture, out var idx))
            {
                continue;
            }

            using var ms = new MemoryStream();
            await using var fs = file.OpenReadStream();
            await fs.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            fieldValues[idx] = ms.ToArray();
        }

        var signRequest = new SignatureRequest
        {
            SourcePdf = sourcePdf,
            Fields = fields,
            FieldValues = fieldValues,
            Sealing = padesDefaults.BuildSealingOptions(),
            Metadata = body.Metadata is null ? null : new SignatureMetadata(
                Reason: body.Metadata.Reason,
                Location: body.Metadata.Location,
                ContactInfo: body.Metadata.ContactInfo,
                SignerName: body.Metadata.SignerName),
        };

        var signed = await engine.SignAsync(signRequest, cancellationToken).ConfigureAwait(false);

        return Results.File(
            fileContents: signed.SignedPdf.ToArray(),
            contentType: "application/pdf",
            fileDownloadName: "signed.pdf");
    }

    private sealed record BinarySignFormBody(
        IReadOnlyList<ApiSignatureField> Fields,
        IReadOnlyDictionary<int, ApiFieldValue>? FieldValues,
        ApiSignatureMetadata? Metadata);
}
