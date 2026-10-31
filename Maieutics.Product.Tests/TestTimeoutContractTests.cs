using System.Reflection;

namespace Maieutics.Product.Tests;

public sealed class TestTimeoutContractTests
{
    [Fact]
    public void EveryAsyncTestDeclaresATimeout()
    {
        var missing = typeof(TestTimeoutContractTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType) ||
                             method.ReturnType == typeof(ValueTask) ||
                             method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is { Timeout: <= 0 })
            .Select(method => $"{method.DeclaringType?.Name}.{method.Name} lacks a declarative xUnit timeout")
            .ToArray();

        Assert.Empty(missing);
    }
}
