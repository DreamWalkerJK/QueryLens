using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QueryLens.Core;

/// <summary>A conservative SQL lexer; it does not claim semantic SQL equivalence.</summary>
public static class SqlFingerprinter
{
    public const int NormalizationVersion = 2;

    public static FingerprintResult Fingerprint(string sql, DatabaseDialect dialect = DatabaseDialect.Unknown, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = Tokenize(sql, dialect);
        var statements = ImmutableArray.CreateBuilder<string>();
        var statement = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Normalized == ";")
            {
                if (statement.Count > 0) statements.Add(string.Join(' ', statement));
                statement.Clear();
            }
            else statement.Add(token.Normalized);
        }
        if (statement.Count > 0) statements.Add(string.Join(' ', statement));
        var normalized = string.Join(" ; ", statements);
        var redacted = string.Join(' ', tokens.Select(t => t.Redacted));
        var identity = $"{dialect}|{database?.Length ?? 0}:{database}|v{NormalizationVersion}|{normalized}";
        return new(redacted, normalized, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), dialect, NormalizationVersion, statements.ToImmutable());
    }

    internal readonly record struct SqlToken(string Redacted, string Normalized);

    internal static List<SqlToken> Tokenize(string sql, DatabaseDialect dialect)
    {
        var result = new List<SqlToken>();
        var i = 0;
        void Add(string text, bool preserveCase = false) => result.Add(new(text, preserveCase ? text : text.ToLowerInvariant()));
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if ((c == '-' && i + 1 < sql.Length && sql[i + 1] == '-' && (dialect != DatabaseDialect.MySql || i + 2 == sql.Length || char.IsWhiteSpace(sql[i + 2]))) || (c == '#' && dialect == DatabaseDialect.MySql))
            { while (i < sql.Length && sql[i] is not '\r' and not '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                if (dialect == DatabaseDialect.MySql && i + 2 < sql.Length && sql[i + 2] == '!') throw new FormatException("MySQL executable comments require expansion before fingerprinting.");
                i += 2;
                var depth = 1;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*' && dialect is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.GaussDb) { depth++; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new FormatException("Unterminated SQL block comment.");
                continue;
            }
            if (c == '$' && dialect is DatabaseDialect.PostgreSql or DatabaseDialect.GaussDb or DatabaseDialect.Unknown)
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                if (end < sql.Length && sql[end] == '$' && (end == i + 1 || char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
                {
                    var delimiter = sql[i..(end + 1)];
                    var close = sql.IndexOf(delimiter, end + 1, StringComparison.Ordinal);
                    if (close < 0) throw new FormatException("Unterminated PostgreSQL dollar string.");
                    i = close + delimiter.Length; Add("?"); continue;
                }
            }
            var prefixedString = (c is 'N' or 'n' or 'E' or 'e' or 'B' or 'b' or 'X' or 'x') && i + 1 < sql.Length && sql[i + 1] == '\'';
            if (c == '\'' || prefixedString || (c == '"' && dialect == DatabaseDialect.MySql))
            {
                var backslash = dialect is DatabaseDialect.MySql or DatabaseDialect.Unknown || (prefixedString && c is 'E' or 'e');
                if (prefixedString) i++;
                var quote = sql[i++];
                var closed = false;
                while (i < sql.Length)
                {
                    if (backslash && sql[i] == '\\') { i = Math.Min(i + 2, sql.Length); continue; }
                    if (sql[i++] != quote) continue;
                    if (i < sql.Length && sql[i] == quote) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new FormatException("Unterminated SQL string literal.");
                Add("?"); continue;
            }
            if (c is '"' or '`' || (c == '[' && dialect is DatabaseDialect.SqlServer or DatabaseDialect.Unknown))
            {
                var start = i++;
                var close = c == '[' ? ']' : c;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != close) continue;
                    if (i < sql.Length && sql[i] == close) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new FormatException("Unterminated SQL quoted identifier.");
                Add(sql[start..i], true); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                var word = sql[start..i];
                Add(word.Equals("null", StringComparison.OrdinalIgnoreCase) || word.Equals("true", StringComparison.OrdinalIgnoreCase) || word.Equals("false", StringComparison.OrdinalIgnoreCase) ? "?" : word);
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
            {
                if (c == '0' && i + 1 < sql.Length && sql[i + 1] is 'x' or 'X' or 'b' or 'B')
                { i += 2; while (i < sql.Length && (char.IsAsciiHexDigit(sql[i]) || sql[i] == '_')) i++; }
                else
                {
                    while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '_')) i++;
                    if (i < sql.Length && sql[i] == '.') { i++; while (i < sql.Length && char.IsDigit(sql[i])) i++; }
                    if (i < sql.Length && sql[i] is 'e' or 'E')
                    { i++; if (i < sql.Length && sql[i] is '+' or '-') i++; while (i < sql.Length && char.IsDigit(sql[i])) i++; }
                }
                Add("?"); continue;
            }
            if ((c == ':' && i + 1 < sql.Length && sql[i + 1] != ':' && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_')) || c == '@' || (c == '$' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                i++; if (c == '@' && i < sql.Length && sql[i] == '@') { var start = ++i; while (i < sql.Length && char.IsLetterOrDigit(sql[i])) i++; Add("@@" + sql[start..i]); continue; }
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                Add("?"); continue;
            }
            var multiOperator = new[] { "->>", "#>>", "::", ">=", "<=", "<>", "!=", "||", "&&", "->", "#>", ":=", "!~", "~*", "@>", "<@", "?|", "?&" }.FirstOrDefault(op => sql.AsSpan(i).StartsWith(op, StringComparison.Ordinal));
            if (multiOperator is not null) { Add(multiOperator); i += multiOperator.Length; }
            else { Add(c.ToString()); i++; }
        }
        return result;
    }
}
