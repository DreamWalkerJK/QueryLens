using System.Text.RegularExpressions;
namespace QueryLens.Core;
public static class SensitiveData
{
    static readonly Regex Secret = new("(?i)(password|pwd|token|secret)\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^;\\r\\n]+)", RegexOptions.Compiled);
    public static string Redact(string value) => Secret.Replace(value, "$1=***");
}
