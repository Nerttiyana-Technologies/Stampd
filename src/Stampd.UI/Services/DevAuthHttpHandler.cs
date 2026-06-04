using System.Net.Http.Headers;

namespace Stampd.UI.Services;

/// <summary>
/// HttpMessageHandler that injects a dev-minted JWT bearer token on every outbound
/// request that doesn't already carry an Authorization header. Lets the Stampd UI talk
/// to authenticated WebApi endpoints in Development without the user pasting a token.
/// </summary>
/// <remarks>
/// Wired in via <c>AddHttpClient&lt;DesignerApiClient&gt;().AddHttpMessageHandler&lt;DevAuthHttpHandler&gt;()</c>.
/// If a caller has already attached its own Authorization header — for example because the
/// browser-side auth bar set one — we leave that header alone.
/// </remarks>
public sealed class DevAuthHttpHandler : DelegatingHandler
{
    private readonly DevTokenProvider _tokenProvider;

    public DevAuthHttpHandler(DevTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
