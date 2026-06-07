using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using PdfSharp.Drawing;
using PdfSharp.Pdf;

using Stampd.Engine.Rendering;

using Xunit;

namespace Stampd.WebApi.Tests;

/// <summary>
/// End-to-end exercise of the recipient workflow over HTTP. Proves: auth + template upload
/// + dispatch + recipient-token resolution + submission + sign + persistence + download.
/// </summary>
public sealed class WorkflowHappyPathTests : IClassFixture<StampdWebApplicationFactory>
{
    private readonly StampdWebApplicationFactory _factory;

    public WorkflowHappyPathTests(StampdWebApplicationFactory factory)
    {
        _factory = factory;
        PlatformFontResolver.Register();
    }

    [Fact]
    public async Task TemplateDispatchRecipientDownload_FullFlow()
    {
        var client = _factory.CreateClient();
        var token = await AuthEndpointTests.IssueDevTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // ---- 1. Upload a template ----
        var pdfBytes = BuildSyntheticPdf();
        var pdfB64 = Convert.ToBase64String(pdfBytes);

        var createResponse = await client.PostAsJsonAsync("/api/templates", new
        {
            name = "Workflow Test Contract",
            sourcePdfBase64 = pdfB64,
            roles = new[] { new { name = "Customer", routingOrder = 1 } },
            fields = new[]
            {
                new
                {
                    pageNumber = 1,
                    bounds = new { x = 10.0, y = 80.0, width = 40.0, height = 5.0 },
                    kind = "Text",
                    assignedRoleName = "Customer",
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var templateDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var templateId = templateDoc.RootElement.GetProperty("id").GetGuid();

        // ---- 2. Dispatch a signing request ----
        var dispatchResponse = await client.PostAsJsonAsync("/api/signing-requests", new
        {
            documentTemplateId = templateId,
            subject = "Please sign",
            recipients = new[] { new { roleName = "Customer", email = "alice@example.com", name = "Alice" } },
        });
        Assert.Equal(HttpStatusCode.Created, dispatchResponse.StatusCode);

        var dispatchDoc = JsonDocument.Parse(await dispatchResponse.Content.ReadAsStringAsync());
        var accessUrl = dispatchDoc.RootElement
            .GetProperty("recipients")[0]
            .GetProperty("accessUrl")
            .GetString()!;
        var accessToken = accessUrl[(accessUrl.LastIndexOf('/') + 1)..];

        // ---- 3. Recipient fetches their fields (no JWT — uses access token) ----
        var anonClient = _factory.CreateClient();
        var fieldsResponse = await anonClient.GetAsync(
            new Uri($"/api/sign/{accessToken}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, fieldsResponse.StatusCode);

        // ---- 4. Recipient submits their values ----
        var submitResponse = await anonClient.PostAsJsonAsync(
            $"/api/sign/{accessToken}",
            new { fieldValues = new Dictionary<string, object> { ["0"] = new { text = "Alice signed" } } });
        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

        var submitDoc = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync());
        Assert.Equal("Signed", submitDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Completed", submitDoc.RootElement.GetProperty("workflowStatus").GetString());

        var signedDocId = submitDoc.RootElement.GetProperty("signedDocumentId").GetGuid();

        // ---- 5. Download the signed PDF (auth required) ----
        var downloadResponse = await client.GetAsync(
            new Uri($"/api/signed-documents/{signedDocId}/download", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/pdf", downloadResponse.Content.Headers.ContentType?.MediaType);

        var signedPdf = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(signedPdf, 0, 5));
        Assert.True(signedPdf.Length > pdfBytes.Length); // grew due to signature dict
    }

    private static byte[] BuildSyntheticPdf()
    {
        using var document = new PdfDocument();
        document.Info.Title = "Workflow Test";

        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.Letter;

        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawString(
            "Workflow Test Document",
            new XFont("Helvetica", 18, XFontStyleEx.Bold),
            XBrushes.Black,
            new XRect(0, 50, page.Width.Point, 30),
            XStringFormats.TopCenter);

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }
}
