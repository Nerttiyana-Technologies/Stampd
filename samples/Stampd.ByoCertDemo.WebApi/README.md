# Stampd.ByoCertDemo.WebApi

**Customer integration reference.** Same Blazor UI as `Stampd.ByoCertDemo`, but it signs PDFs by calling `Stampd.WebApi` over HTTP. No engine references, no in-process signing — just `HttpClient` + an `Authorization: Bearer` header. Hand this project's source to the customer after the live demo so their developers can see exactly what their integration will look like.

## What's different from the standalone sample

| | `Stampd.ByoCertDemo` (standalone) | `Stampd.ByoCertDemo.WebApi` (this one) |
|---|---|---|
| Where signing happens | In-process — embedded engine | Remote — POST to `/api/sign` on `Stampd.WebApi` |
| Cert lives | Encrypted vault on the demo host | Configured in `Stampd.WebApi`'s sealing provider |
| Project references | Stampd.Engine, LocalCertificate, FreeTSA | **None** — `HttpClient` only |
| Audience | "Look what Stampd can do" — the wow demo | "Here's the integration shape" — the dev handoff |
| Runs alongside | Nothing else | Stampd.WebApi must be running |

This project is the answer to *"OK, that's cool. How does my team actually build against this?"*

## Run it

You need **two processes** running:

```bash
# Terminal 1 — start Stampd.WebApi (default port 5070)
dotnet run --project src/Stampd.WebApi

# Terminal 2 — start the demo client (port 5182)
dotnet run --project samples/Stampd.ByoCertDemo.WebApi
```

Browser opens at <http://localhost:5182>. The page calls `/health` on Stampd.WebApi at startup; if the WebApi isn't running you'll see a red "unreachable" status card with the exact error.

### Override the WebApi URL

```jsonc
// samples/Stampd.ByoCertDemo.WebApi/appsettings.json
{ "Stampd": { "WebApi": { "BaseUrl": "https://stampd.your-company.internal" } } }
```

…or pass `--Stampd:WebApi:BaseUrl=https://...` on the command line. Same Configuration binding the rest of Stampd uses.

## The integration contract — what your customer copies

Open `Services/StampdWebApiClient.cs`. That's the whole integration. Three operations:

| Method | Endpoint | Notes |
|---|---|---|
| `GetHealthAsync()` | `GET /health` | Confirms the WebApi is up. No auth required. |
| `EnsureBearerTokenAsync()` | `POST /api/auth/dev-token` | **Dev only.** In production the customer's existing identity stack (OIDC / SAML / their internal IdP) mints JWTs and Stampd.WebApi validates them. The header form is identical: `Authorization: Bearer <jwt>`. |
| `SignAsync(pdfBytes, signaturePng, useTsa)` | `POST /api/sign` | The actual signing call. JSON body with the PDF + signature image as base64 + field placement + profile (`BB`/`BT`). |

The Razor page (`Components/Pages/Demo.razor`) is just a thin shell around these three calls. Customer dev teams reading this should think:

> *"OK, this is just three HTTP calls. I can build that into our existing app in an afternoon."*

That's the takeaway.

## Demo script (customer-facing)

Use this **after** the standalone `Stampd.ByoCertDemo` has wowed them.

1. *"What you just saw was Stampd's engine running in-process for speed. Let me show you what your production deployment will actually look like."*
2. Open this sample. Show the "Stampd.WebApi connection" card at the top — green ✓ with the endpoint URL.
3. Upload the same PDF, draw the same signature, hit **POST to /api/sign**.
4. *"Look at the result panel — same signed PDF, same Adobe-valid signature. The difference is where the signing happened."*
5. Expand **See the request your code would build** at the bottom. Show the customer the JSON shape.
6. Open `Services/StampdWebApiClient.cs` on screen. Walk through `SignAsync` line by line.
7. *"That's it. Three HTTP calls, no SDK to install, language-agnostic — your team builds the UI any way you want and hits these endpoints. The cert lives where you decide (LocalCertificate / Vault / Azure KV / AWS KMS) and never touches your client code."*

## What this sample doesn't do (and the customer might ask)

- **No cert upload.** This client signs whatever cert Stampd.WebApi is configured with. The standalone `Stampd.ByoCertDemo` shows the BYO-cert flow visually; in production the cert is provisioned via Stampd.WebApi's sealing-provider config (see `docs/deployment-bring-your-own-cert.md`).
- **No multi-step workflow** (templates, signing requests, recipients). Real production flows include those — see `Stampd.UI` for the full designer + sender + recipient experience. This sample is intentionally just the signing endpoint, to make the integration shape obvious.
- **No production auth wiring.** The dev-token endpoint is the simplest path for a demo. Replace with whatever JWT issuer the customer already uses.

## Files

| File | Role |
|---|---|
| `Stampd.ByoCertDemo.WebApi.csproj` | No project references — pure HTTP client + Blazor |
| `Program.cs` | Boots Blazor + registers the typed `HttpClient` |
| `Services/StampdWebApiClient.cs` | **The integration contract.** Three HTTP calls. This is the file the customer copies. |
| `Components/Pages/Demo.razor` | Single-page UI: health card, PDF upload, signature pad, sign button, result viewer |
| `Components/App.razor`, `Routes.razor`, `_Imports.razor`, `Layout/MainLayout.razor` | Blazor shell |
| `wwwroot/css/site.css` | Same palette as the standalone sample, plus a status-card style |
| `wwwroot/js/signature-pad.js` | Vanilla JS canvas-based signature pad (transparent PNG output) |
| `appsettings.json` | `Stampd:WebApi:BaseUrl` override knob |
| `Properties/launchSettings.json` | Boots on `:5182` so it doesn't clash with anything else |

## Related

- `samples/Stampd.ByoCertDemo/` — standalone variant (in-process engine, encrypted vault)
- `samples/Stampd.CertCheck/` — CLI for deployment validation
- `docs/deployment-bring-your-own-cert.md` — full production guide for the four sealing providers
- `src/Stampd.WebApi/` — the API this sample talks to
