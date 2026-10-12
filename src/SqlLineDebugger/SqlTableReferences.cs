using System.Text;

namespace SqlLineDebugger;

internal static class SqlTableReferences
{
    // Tokenization keeps comments, literals and quoted identifiers from being
    // mistaken for variable references. No SQL is rewritten or evaluated.
    internal static IReadOnlyList<string> Tokens(string sql)
    {
        var result = new List<string>();
        for (int i = 0; i < sql.Length;)
        {
            char ch = sql[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int depth = 1; i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            if (ch == '\'')
            {
                i++;
                while (i < sql.Length) if (sql[i++] == '\'') { if (i < sql.Length && sql[i] == '\'') i++; else break; }
                continue;
            }
            if (ch is '[' or '"')
            {
                char end = ch == '[' ? ']' : '"'; var name = new StringBuilder(); i++;
                while (i < sql.Length)
                {
                    char next = sql[i++];
                    if (next == end) { if (i < sql.Length && sql[i] == end) { name.Append(end); i++; } else break; }
                    else name.Append(next);
                }
                result.Add("\0" + name); continue;
            }
            if (char.IsLetterOrDigit(ch) || ch is '@' or '#' or '_' or '$')
            {
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '@' or '#' or '_' or '$')) i++;
                result.Add(sql[start..i]); continue;
            }
            result.Add(sql[i++].ToString());
        }
        return result;
    }

    internal static HashSet<string> Variables(string sql, IEnumerable<string>? known = null)
    {
        var tokens = Tokens(sql);
        var names = new HashSet<string>(known ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].StartsWith('@') || tokens[i].StartsWith("@@")) continue;
            if (names.Contains(tokens[i]) || (i > 0 && new[] { "FROM", "JOIN", "INTO", "INSERT", "UPDATE", "MERGE", "APPLY" }.Contains(tokens[i - 1], StringComparer.OrdinalIgnoreCase)) ||
                (i > 0 && tokens[i - 1].Equals("DECLARE", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count && tokens[i + 1].Equals("TABLE", StringComparison.OrdinalIgnoreCase)))
                found.Add(tokens[i]);
        }
        return found;
    }
}
