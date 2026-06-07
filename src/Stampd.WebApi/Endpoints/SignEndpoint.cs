using Microsoft.AspNetCore.Mvc;

using Stampd.Core;
using Stampd.WebApi.Models;

namespace Stampd.WebApi.Endpoints;

internal static class SignEndpoint
{
    public static IEndpointRouteBuilder MapSign(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/api/sign", HandleAsync)
            .WithName("Sign")
            .WithSummary("Stamps signer-supplied content onto a source PDF and applies a PAdES signature. Returns the signed PDF as base64.")
            .Produces<SignResponseBody>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return builder;
    }

    private static async Task<IResult> HandleAsync(
        [FromBody] SignRequestBody body,
        [FromServices] IStampdEngine engine,
        [FromServices] Services.PadesDefaults padesDefaults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.SourcePdfBase64))
        {
            return Results.Problem(
                title: "sourcePdfBase64 is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        byte[] sourcePdf;
        try
        {
            sourcePdf = Convert.FromBase64String(body.SourcePdfBase64);
        }
        catch (FormatException ex)
        {
            return Results.Problem(
                title: "sourcePdfBase64 is not valid base64.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fields = body.Fields.Select(f =>
            new SignatureField(
                f.PageNumber,
                new PercentageRect(f.Bounds.X, f.Bounds.Y, f.Bounds.Width, f.Bounds.Height),
                f.Kind,
                f.SignerId))
            .ToArray();

        var fieldValues = new Dictionary<int, ReadOnlyMemory<byte>>(body.FieldValues.Count);
        foreach (var (index, value) in body.FieldValues)
        {
            if (value.ImageBase64 is { Length: > 0 } imageBase64)
            {
                try
                {
                    fieldValues[index] = Convert.FromBase64String(imageBase64);
                }
                catch (FormatException ex)
                {
                    return Results.Problem(
                        title: $"FieldValues[{index}].ImageBase64 is not valid base64.",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }
            else if (value.Text is { } text)
            {
                fieldValues[index] = System.Text.Encoding.UTF8.GetBytes(text);
            }
            else
            {
                return Results.Problem(
                    title: $"FieldValues[{index}] must set either Text or ImageBase64.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var request = new SignatureRequest
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

        SignedDocument signed;
        try
        {
            signed = await engine.SignAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Signing failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var signedBytes = signed.SignedPdf.ToArray();
        var response = new SignResponseBody(
            SignedPdfBase64: Convert.ToBase64String(signedBytes),
            DocumentHashSha256: signed.DocumentHashSha256,
            SignedAtUtc: signed.SignedAtUtc,
            SignedPdfSizeBytes: signedBytes.Length);

        return Results.Ok(response);
    }
}
