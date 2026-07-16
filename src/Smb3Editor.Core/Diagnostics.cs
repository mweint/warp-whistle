namespace Smb3Editor.Core;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message)
{
    public override string ToString() => $"{Severity}: {Message} ({Code})";
}

public sealed class OperationResult<T>
{
    private OperationResult(T? value, IReadOnlyList<Diagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = diagnostics;
    }

    public T? Value { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool IsSuccess => Value is not null && Diagnostics.All(static d => d.Severity != DiagnosticSeverity.Error);

    public static OperationResult<T> Success(T value, IEnumerable<Diagnostic>? diagnostics = null) =>
        new(value, diagnostics?.ToArray() ?? []);

    public static OperationResult<T> Failure(params Diagnostic[] diagnostics) => new(default, diagnostics);

    internal static OperationResult<T> FailureWithValue(T value, IEnumerable<Diagnostic> diagnostics) =>
        new(value, diagnostics.ToArray());
}

public static class Diagnostics
{
    public static Diagnostic Error(string code, string message) => new(DiagnosticSeverity.Error, code, message);
    public static Diagnostic Warning(string code, string message) => new(DiagnosticSeverity.Warning, code, message);
    public static Diagnostic Info(string code, string message) => new(DiagnosticSeverity.Information, code, message);
}
