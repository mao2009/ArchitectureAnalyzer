using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace ArchitectureAnalyzer.Tests.TestInfrastructure;

/// <summary>
/// Replacement for <c>Microsoft.CodeAnalysis.Testing.Verifiers.XUnit.XUnitVerifier</c>, whose
/// assertion types are binary-incompatible with xunit 2.9 (it throws
/// <see cref="System.MissingMethodException"/> the moment an assertion fails, hiding the real
/// failure). Implementing <see cref="IVerifier"/> directly against xunit's current
/// <c>Assert.Fail</c> keeps failures readable.
/// </summary>
public sealed class XUnit29Verifier : IVerifier
{
    private readonly string? _context;

    /// <summary>Creates a root verifier.</summary>
    public XUnit29Verifier()
    {
    }

    private XUnit29Verifier(string? context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public IVerifier PushContext(string context)
    {
        return new XUnit29Verifier(context);
    }

    /// <inheritdoc />
    public void Empty<T>(string collectionName, IEnumerable<T> collection)
    {
        True(!collection.Any(), $"Expected '{collectionName}' to be empty but was not.");
    }

    /// <inheritdoc />
    public void Equal<T>(T expected, T actual, string? message = null)
    {
        True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message} Expected: {Format(expected)}. Actual: {Format(actual)}.");
    }

    /// <inheritdoc />
    public void True(bool assert, string? message = null)
    {
        if (!assert)
        {
            Fail(message ?? "Assertion failed.");
        }
    }

    /// <inheritdoc />
    public void False(bool assert, string? message = null)
    {
        if (assert)
        {
            Fail(message ?? "Expected false but was true.");
        }
    }

    /// <inheritdoc />
    [DoesNotReturn]
    public void Fail(string? message = null)
    {
        Assert.Fail(AppendContext(message ?? "Verification failed."));
    }

    /// <inheritdoc />
    public void LanguageIsSupported(string language)
    {
        True(language == LanguageNames.CSharp, $"Language '{language}' is not supported.");
    }

    /// <inheritdoc />
    public void NotEmpty<T>(string collectionName, IEnumerable<T> collection)
    {
        True(collection.Any(), $"Expected '{collectionName}' to be non-empty but was empty.");
    }

    /// <inheritdoc />
    public void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        IEqualityComparer<T>? equalityComparer = null,
        string? message = null)
    {
        var comparer = equalityComparer ?? EqualityComparer<T>.Default;
        var expectedItems = expected.ToList();
        var actualItems = actual.ToList();

        var equal = expectedItems.Count == actualItems.Count
            && expectedItems.Zip(actualItems, (e, a) => comparer.Equals(e, a)).All(static entry => entry);

        True(equal, $"{message} Expected: [{string.Join(", ", expectedItems)}]. Actual: [{string.Join(", ", actualItems)}].");
    }

    private static string Format<T>(T value)
    {
        return value is null ? "<null>" : value.ToString() ?? "<null>";
    }

    private string AppendContext(string message)
    {
        return _context is null ? message : $"{message} Context: {_context}";
    }
}
