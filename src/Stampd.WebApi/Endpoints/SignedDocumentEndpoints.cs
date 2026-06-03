using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Stampd.Core.Storage;
using Stampd.Infrastructure;

namespace Stampd.WebApi.Endpoints;

internal static class SignedDocumentEndpoints
{
    public static IEndpointRouteBuilder MapSignedDocuments(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/signed-documents").WithTags("SignedDocuments");

        group.MapGet("/{id:guid}", GetMetadataAsync)
            .WithName("GetSignedDocument")
            .WithSummary("Returns the metadata for a sealed document (storage key, hash, signing time, PAdES level).");

        group.MapGet("/{id:guid}/download", DownloadAsync)
            .WithName("DownloadSignedDocument")
            .WithSummary("Streams the sealed PDF bytes back as application/pdf.");

        return builder;
    }

    private static async Task<IResult> GetMetadataAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        CancellationToken ct)
    {
        var record = await db.SignedDocumentRecords
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            id = record.Id,
            signingRequestId = record.SigningRequestId,
            storageKey = record.StorageKey,
            contentSha256 = record.ContentSha256,
            signedAtUtc = record.SignedAtUtc,
            sealingProviderName = record.SealingProviderName,
            sealingKeyIdentifier = record.SealingKeyIdentifier,
            timestampAuthorityUrl = record.TimestampAuthorityUrl,
            padesLevel = record.PAdESLevel,
        });
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        [FromServices] StampdDbContext db,
        [FromServices] IDocumentStorageProvider storage,
        CancellationToken ct)
    {
        var record = await db.SignedDocumentRecords
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return Results.NotFound();
        }

        var bytes = await storage.RetrieveAsync(record.StorageKey, ct).ConfigureAwait(false);

        return Results.File(
            fileContents: bytes,
            contentType: "application/pdf",
            fileDownloadName: $"signed-{record.Id:N}.pdf");
    }
}
