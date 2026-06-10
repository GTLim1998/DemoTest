using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace WebApplication1.Services;

public interface IConversationApiClient
{
    Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken);

    Task<object?> CallConversationAsync(
        string conversationId,
        string text,
        JsonElement states,
        string agentId,
        CancellationToken cancellationToken);
}

public sealed class ConversationApiClient : IConversationApiClient
{
    private const string AuthUrl = "https://meshstage.lessen.com/auth/token";
    private const string ConversationBaseUrl = "https://meshstage.lessen.com/onebrain/conversation";
    private const string Username = "oneom";
    private const string Password = "AIHackthonTeam2@";
    private const string TokenCacheKey = "onebrain-bearer-token";
    private static readonly SemaphoreSlim TokenRefreshLock = new(1, 1);
    private static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);

    private readonly IMemoryCache _cache;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ConversationApiClient> _logger;

    public ConversationApiClient(
        IMemoryCache cache,
        HttpClient httpClient,
        ILogger<ConversationApiClient> logger)
    {
        _cache = cache;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue<TokenResult>(TokenCacheKey, out var cachedToken) &&
            cachedToken is not null &&
            IsTokenUsable(cachedToken))
        {
            return cachedToken with { FromCache = true };
        }

        await TokenRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue<TokenResult>(TokenCacheKey, out cachedToken) &&
                cachedToken is not null &&
                IsTokenUsable(cachedToken))
            {
                return cachedToken with { FromCache = true };
            }

            return await RefreshTokenAsync(cancellationToken);
        }
        finally
        {
            TokenRefreshLock.Release();
        }
    }

    public async Task<object?> CallConversationAsync(
        string conversationId,
        string text,
        JsonElement states,
        string agentId,
        CancellationToken cancellationToken)
    {
        var tokenResult = await GetTokenAsync(cancellationToken);
        if (tokenResult is null)
        {
            return null;
        }

        try
        {
            using var firstResponse = await SendConversationRequestAsync(
                tokenResult.Token,
                conversationId,
                text,
                states,
                agentId,
                cancellationToken);

            if (firstResponse.StatusCode != HttpStatusCode.Unauthorized)
            {
                firstResponse.EnsureSuccessStatusCode();
                return await ReadJsonOrTextAsync(firstResponse, cancellationToken);
            }

            _cache.Remove(TokenCacheKey);
            var refreshedToken = await RefreshTokenAsync(cancellationToken);
            if (refreshedToken is null)
            {
                return null;
            }

            using var retryResponse = await SendConversationRequestAsync(
                refreshedToken.Token,
                conversationId,
                text,
                states,
                agentId,
                cancellationToken);
            retryResponse.EnsureSuccessStatusCode();

            return await ReadJsonOrTextAsync(retryResponse, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Conversation API call failed.");
            return null;
        }
    }

    private async Task<TokenResult?> RefreshTokenAsync(CancellationToken cancellationToken)
    {
        var credentials = $"{Username}:{Password}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

        using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = ExtractToken(responseContent);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var tokenResult = new TokenResult(
                token,
                ParseJsonOrText(responseContent),
                ResolveExpiresAt(token, responseContent),
                FromCache: false);

            CacheToken(tokenResult);

            return tokenResult;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogError(ex, "Token request failed.");
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendConversationRequestAsync(
        string token,
        string conversationId,
        string text,
        JsonElement states,
        string agentId,
        CancellationToken cancellationToken)
    {
        var requestUrl = BuildConversationUrl(agentId, conversationId);
        var payload = new JsonObject
        {
            ["text"] = text,
            ["states"] = BuildStatesNode(states)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static string BuildConversationUrl(string agentId, string conversationId)
    {
        return string.Join(
            '/',
            ConversationBaseUrl,
            Uri.EscapeDataString(agentId),
            Uri.EscapeDataString(conversationId));
    }

    private void CacheToken(TokenResult tokenResult)
    {
        var cacheDuration = tokenResult.ExpiresAt - DateTimeOffset.UtcNow - RefreshSkew;
        if (cacheDuration <= TimeSpan.Zero)
        {
            return;
        }

        _cache.Set(TokenCacheKey, tokenResult, cacheDuration);
    }

    private static bool IsTokenUsable(TokenResult? tokenResult)
    {
        return tokenResult is not null &&
            tokenResult.ExpiresAt > DateTimeOffset.UtcNow.Add(RefreshSkew);
    }

    private static JsonNode BuildStatesNode(JsonElement states)
    {
        if (states.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new JsonArray();
        }

        return JsonNode.Parse(states.GetRawText()) ?? new JsonArray();
    }

    private static async Task<object> ReadJsonOrTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseJsonOrText(responseText);
    }

    private static object ParseJsonOrText(string responseText)
    {
        try
        {
            return JsonNode.Parse(responseText) ?? responseText;
        }
        catch (JsonException)
        {
            return responseText;
        }
    }

    private static string? ExtractToken(string responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        try
        {
            var tokenData = JsonNode.Parse(responseContent);
            return tokenData?["token"]?.ToString()
                ?? tokenData?["access_token"]?.ToString()
                ?? responseContent.Trim();
        }
        catch (JsonException)
        {
            return responseContent.Trim();
        }
    }

    private static DateTimeOffset ResolveExpiresAt(string token, string responseContent)
    {
        return TryGetExpiresAtFromResponse(responseContent, out var responseExpiresAt)
            ? responseExpiresAt
            : TryGetExpiresAtFromJwt(token, out var jwtExpiresAt)
                ? jwtExpiresAt
                : DateTimeOffset.UtcNow.Add(FallbackTokenLifetime);
    }

    private static bool TryGetExpiresAtFromResponse(
        string responseContent,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;

        try
        {
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;

            if (TryReadDateTimeOffset(root, "expires_at", out expiresAt) ||
                TryReadDateTimeOffset(root, "expiresAt", out expiresAt))
            {
                return true;
            }

            if (TryReadExpiresIn(root, "expires_in", out expiresAt) ||
                TryReadExpiresIn(root, "expiresIn", out expiresAt))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadDateTimeOffset(
        JsonElement root,
        string propertyName,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;

        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out expiresAt);
    }

    private static bool TryReadExpiresIn(
        JsonElement root,
        string propertyName,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        double seconds;
        if (property.ValueKind == JsonValueKind.Number)
        {
            seconds = property.GetDouble();
        }
        else if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), out var parsedSeconds))
        {
            seconds = parsedSeconds;
        }
        else
        {
            return false;
        }

        expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return true;
    }

    private static bool TryGetExpiresAtFromJwt(string token, out DateTimeOffset expiresAt)
    {
        expiresAt = default;

        var tokenParts = token.Split('.');
        if (tokenParts.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(tokenParts[1]));
            using var document = JsonDocument.Parse(payloadJson);

            if (!document.RootElement.TryGetProperty("exp", out var expProperty) ||
                !expProperty.TryGetInt64(out var unixTimeSeconds))
            {
                return false;
            }

            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

public sealed record TokenResult(
    string Token,
    object RawResponse,
    DateTimeOffset ExpiresAt,
    bool FromCache);
