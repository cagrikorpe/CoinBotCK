using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace CoinBot.Infrastructure.Observability;

public sealed class SensitiveDataRedactionPolicy : IDestructuringPolicy
{
    private static readonly string[] SensitiveNameFragments =
    [
        "apikey",
        "authorization",
        "bearer",
        "clientsecret",
        "cookie",
        "password",
        "privatekey",
        "refreshtoken",
        "secret",
        "setcookie",
        "token"
    ];

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        if (!ShouldDestructure(value.GetType()))
        {
            result = null!;
            return false;
        }

        var properties = value.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

        if (properties.Length == 0)
        {
            result = null!;
            return false;
        }

        var structuredProperties = new List<LogEventProperty>(properties.Length);

        foreach (var property in properties)
        {
            var propertyValue = ResolvePropertyValue(value, property);
            var sanitizedValue = IsSensitiveProperty(property.Name)
                ? new ScalarValue("***REDACTED***")
                : propertyValueFactory.CreatePropertyValue(propertyValue, destructureObjects: true);

            structuredProperties.Add(new LogEventProperty(property.Name, sanitizedValue));
        }

        result = new StructureValue(structuredProperties, value.GetType().Name);
        return true;
    }

    private static bool ShouldDestructure(Type type)
    {
        return type != typeof(string) &&
               !type.IsPrimitive &&
               !type.IsEnum &&
               type != typeof(decimal) &&
               type != typeof(Guid) &&
               type != typeof(DateTime) &&
               type != typeof(DateTimeOffset) &&
               type != typeof(TimeSpan);
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var normalizedPropertyName = string.Concat(propertyName.Where(char.IsLetterOrDigit))
            .ToLowerInvariant();

        return SensitiveNameFragments.Any(fragment => normalizedPropertyName.Contains(fragment, StringComparison.Ordinal));
    }

    private static object? ResolvePropertyValue(object value, PropertyInfo property)
    {
        try
        {
            return property.GetValue(value);
        }
        catch
        {
            return null;
        }
    }
}
