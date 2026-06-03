using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Stampd.WebApi.Observability.HealthChecks;

internal static class HealthCheckJsonResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteString("totalDurationMs",
                report.TotalDuration.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

            writer.WriteStartObject("checks");
            foreach (var (name, entry) in report.Entries)
            {
                writer.WriteStartObject(name);
                writer.WriteString("status", entry.Status.ToString());
                writer.WriteString("durationMs",
                    entry.Duration.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(entry.Description))
                {
                    writer.WriteString("description", entry.Description);
                }
                if (entry.Exception is not null)
                {
                    writer.WriteString("exceptionType", entry.Exception.GetType().FullName);
                    writer.WriteString("exceptionMessage", entry.Exception.Message);
                }
                if (entry.Data.Count > 0)
                {
                    writer.WriteStartObject("data");
                    foreach (var (k, v) in entry.Data)
                    {
                        writer.WritePropertyName(k);
                        JsonSerializer.Serialize(writer, v);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(stream.ToArray()).ConfigureAwait(false);
    }
}
