using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoinBot.Infrastructure.Administration;

public static class SensitivePayloadMasker
{
    private static readonly string[] SensitiveNameFragments =
    [
        "apikey",
        "authorization",
        "authheader",
        "bearer",
        "clientsecret",
        "cookie",
        "csrf",
        "password",
        "privatekey",
        "refreshtoken",
        "secret",
        "session",
        "signature",
        "token"
    ];

    public static string? Mask(string? value, int maxLength = 4096)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        var maskedValue = TryMaskJson(normalizedValue, out var maskedJson)
            ? maskedJson
            : MaskDelimitedSegments(normalizedValue);

        return maskedValue.Length <= maxLength
            ? maskedValue
            : maskedValue[..maxLength];
    }

    public static string? MaskFingerprint(string? fingerprint)
    {
        var normalizedFingerprint = fingerprint?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedFingerprint))
        {
            return null;
        }

        if (normalizedFingerprint.Length <= 10)
        {
            return $"{normalizedFingerprint[..2]}***";
        }

        return $"{normalizedFingerprint[..6]}***{normalizedFingerprint[^4..]}";
    }

    private static bool TryMaskJson(string rawValue, out string maskedJson)
    {
        maskedJson = string.Empty;

        try
        {
            var node = JsonNode.Parse(rawValue);

            if (node is null)
            {
                return false;
            }

            MaskJsonNode(node);
            maskedJson = node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void MaskJsonNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToList())
                {
                    if (property.Value is null)
                    {
                        continue;
                    }

                    if (IsSensitiveKey(property.Key))
                    {
                        jsonObject[property.Key] = "***REDACTED***";
                        continue;
                    }

                    if (MaskJsonStringValue(property.Value, out var maskedValue))
                    {
                        jsonObject[property.Key] = maskedValue;
                        continue;
                    }

                    MaskJsonNode(property.Value);
                }

                break;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var item = jsonArray[index];

                    if (item is null)
                    {
                        continue;
                    }

                    if (MaskJsonStringValue(item, out var maskedValue))
                    {
                        jsonArray[index] = maskedValue;
                        continue;
                    }

                    MaskJsonNode(item);
                }

                break;
        }
    }

    private static bool MaskJsonStringValue(JsonNode node, out string maskedValue)
    {
        maskedValue = string.Empty;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var stringValue))
        {
            return false;
        }

        maskedValue = MaskDelimitedSegments(stringValue);
        return true;
    }

    private static string MaskDelimitedSegments(string rawValue)
    {
        if (LooksLikeConnectionString(rawValue))
        {
            return MaskConnectionStringSegments(rawValue);
        }

        var querySeparatorIndex = rawValue.IndexOf('?');
        var path = querySeparatorIndex >= 0
            ? rawValue[..(querySeparatorIndex + 1)]
            : string.Empty;
        var payload = querySeparatorIndex >= 0
            ? rawValue[(querySeparatorIndex + 1)..]
            : rawValue;
        var ampersandSegments = payload.Split('&');

        for (var index = 0; index < ampersandSegments.Length; index++)
        {
            ampersandSegments[index] = MaskSegment(ampersandSegments[index]);
        }

        return path + string.Join("&", ampersandSegments);
    }

    private static string MaskConnectionStringSegments(string rawValue)
    {
        var segments = rawValue.Split(';');

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];

            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var normalizedKey = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (normalizedKey is "userid" or "uid" or "username" or "user")
            {
                segments[index] = $"{key}=***REDACTED***";
                continue;
            }

            segments[index] = MaskSegment(segment);
        }

        return string.Join(";", segments);
    }

    private static string MaskSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return segment;
        }

        var separatorIndex = segment.IndexOf('=');

        if (separatorIndex <= 0)
        {
            var colonIndex = segment.IndexOf(':');

            if (colonIndex > 0)
            {
                var headerName = segment[..colonIndex];

                if (IsSensitiveKey(headerName))
                {
                    return $"{headerName}: ***REDACTED***";
                }
            }

            var bearerIndex = segment.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase);

            if (bearerIndex >= 0)
            {
                return $"{segment[..(bearerIndex + "Bearer ".Length)]}***REDACTED***";
            }

            if (ContainsSensitiveFragment(segment))
            {
                return "***REDACTED***";
            }

            return segment;
        }

        var key = segment[..separatorIndex];
        var value = segment[(separatorIndex + 1)..];

        return IsSensitiveKey(key)
            ? $"{key}=***REDACTED***"
            : $"{key}={value}";
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalizedKey = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();

        return SensitiveNameFragments.Any(fragment => normalizedKey.Contains(fragment, StringComparison.Ordinal));
    }

    private static bool ContainsSensitiveFragment(string value)
    {
        return SensitiveNameFragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeConnectionString(string value)
    {
        return value.Contains(';', StringComparison.Ordinal) &&
               (value.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Pwd=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("User Id=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Uid=", StringComparison.OrdinalIgnoreCase));
    }
}
