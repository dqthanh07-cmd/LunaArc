using System.Net;
using System.Text.Json;

namespace LunaArc.Studio.Services;

internal sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string TagName,
    string ReleaseNotes,
    Uri ReleasePage,
    bool UpdateAvailable);

internal sealed class GitHubUpdateChecker
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxRedirects = 3;
    private const string RepositoryPath = "dqthanh07-cmd/LunaArc";
    private const string GitHubApiHost = "api.github.com";
    private static readonly Uri LatestReleaseApi = new(
        $"https://{GitHubApiHost}/repos/{RepositoryPath}/releases/latest");
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendFollowingGitHubRedirectsAsync(LatestReleaseApi, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode} while checking releases.");

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var content = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await responseStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (content.Length + read > MaxResponseBytes)
                throw new InvalidDataException("The GitHub release response exceeded the allowed size.");
            content.Write(buffer, 0, read);
        }

        using JsonDocument document = JsonDocument.Parse(content.ToArray());
        JsonElement root = document.RootElement;
        string tagName = RequiredString(root, "tag_name");
        if (!Version.TryParse(tagName.StartsWith('v') ? tagName[1..] : tagName, out Version? latestVersion) ||
            !System.Text.RegularExpressions.Regex.IsMatch(tagName, @"^v?\d+\.\d+\.\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");

        string notes = root.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? string.Empty
            : string.Empty;
        if (notes.Length > 4000) notes = notes[..4000] + "...";

        var releasePage = new Uri($"https://github.com/{RepositoryPath}/releases/tag/{Uri.EscapeDataString(tagName)}");
        return new UpdateCheckResult(currentVersion, latestVersion, tagName, notes, releasePage,
            latestVersion.CompareTo(currentVersion) > 0);
    }

    private static async Task<HttpResponseMessage> SendFollowingGitHubRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Uri uri = initialUri;
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("LunaArc-Update-Check/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            {
                if (response.RequestMessage?.RequestUri is not { } finalUri || !IsAllowedUri(finalUri))
                {
                    response.Dispose();
                    throw new InvalidDataException("The GitHub release request left the approved HTTPS host.");
                }
                if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                {
                    response.Dispose();
                    throw new InvalidDataException("The GitHub release response exceeded the allowed size.");
                }
                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (redirects >= MaxRedirects || location == null)
                throw new HttpRequestException("GitHub redirected the release request too many times.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsAllowedUri(uri))
                throw new InvalidDataException("GitHub redirected the release request to an unapproved URL.");
        }
    }

    private static bool IsAllowedUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals(GitHubApiHost, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The GitHub release response is missing '{propertyName}'.");
        return property.GetString()!;
    }
}
