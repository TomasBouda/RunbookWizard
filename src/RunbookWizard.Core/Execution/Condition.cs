using System.Text;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// A parsed <c>when</c> condition (grammar in docs/FORMAT.md section 4.3): <c>||</c>, <c>&amp;&amp;</c>, <c>!</c>,
/// parentheses, <c>==</c> / <c>!=</c> and operands <c>{{Name}}</c>, quoted text or barewords.
/// </summary>
public sealed class Condition
{
    private abstract record Node;
    private sealed record Operand(string Text, string? Variable) : Node;
    private sealed record Compare(Operand Left, Operand? Right, bool Equal) : Node;
    private sealed record Not(Node Inner) : Node;
    private sealed record Binary(Node Left, Node Right, bool IsAnd) : Node;

    private readonly Node _root;

    private Condition(Node root, IReadOnlyList<string> references)
    {
        _root = root;
        References = references;
    }

    /// <summary>Variables the condition reads.</summary>
    public IReadOnlyList<string> References { get; }

    /// <exception cref="FormatException">The text is not a valid condition.</exception>
    public static Condition Parse(string text)
    {
        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new Condition(root, parser.References);
    }

    public static bool TryParse(string text, out Condition? condition, out string? error)
    {
        try
        {
            condition = Parse(text);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            condition = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Evaluates the condition; <paramref name="lookup"/> returns the value of a variable (null = unset, treated as empty).</summary>
    public bool Evaluate(Func<string, string?> lookup) => Eval(_root, lookup);

    private static bool Eval(Node node, Func<string, string?> lookup) => node switch
    {
        Binary b when b.IsAnd => Eval(b.Left, lookup) && Eval(b.Right, lookup),
        Binary b => Eval(b.Left, lookup) || Eval(b.Right, lookup),
        Not n => !Eval(n.Inner, lookup),
        Compare { Right: null } c => Truthy(Value(c.Left, lookup)),
        Compare c => string.Equals(Value(c.Left, lookup), Value(c.Right!, lookup), StringComparison.OrdinalIgnoreCase) == c.Equal,
        Operand o => Truthy(Value(o, lookup)),
        _ => false,
    };

    private static string Value(Operand operand, Func<string, string?> lookup) =>
        operand.Variable is null ? operand.Text : lookup(operand.Variable) ?? string.Empty;

    private static bool Truthy(string value) =>
        value.Length > 0 && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0" && !value.Equals("no", StringComparison.OrdinalIgnoreCase);

    private sealed class Parser
    {
        private readonly List<(string Kind, string Text)> _tokens = new();
        private int _pos;
        private readonly List<string> _references = new();

        public IReadOnlyList<string> References => _references;

        public Parser(string text) => Tokenize(text);

        public Node ParseAll()
        {
            if (_tokens.Count == 0)
                throw new FormatException("The condition is empty.");
            var node = ParseOr();
            if (_pos < _tokens.Count)
                throw new FormatException($"Unexpected '{_tokens[_pos].Text}'.");
            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Accept("||"))
                left = new Binary(left, ParseAnd(), false);
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseNot();
            while (Accept("&&"))
                left = new Binary(left, ParseNot(), true);
            return left;
        }

        private Node ParseNot()
        {
            if (Accept("!"))
                return new Not(ParseNot());
            if (Accept("("))
            {
                var inner = ParseOr();
                if (!Accept(")"))
                    throw new FormatException("Missing ')'.");
                return inner;
            }
            return ParseCompare();
        }

        private Node ParseCompare()
        {
            var left = ParseOperand();
            if (Accept("=="))
                return new Compare(left, ParseOperand(), true);
            if (Accept("!="))
                return new Compare(left, ParseOperand(), false);
            return new Compare(left, null, true);
        }

        private Operand ParseOperand()
        {
            if (_pos >= _tokens.Count)
                throw new FormatException("The condition ends where a value was expected.");
            var (kind, text) = _tokens[_pos];
            if (kind is not ("var" or "text"))
                throw new FormatException($"Expected a value but found '{text}'.");
            _pos++;
            if (kind == "var")
            {
                if (!_references.Contains(text))
                    _references.Add(text);
                return new Operand(text, text);
            }
            return new Operand(text, null);
        }

        private bool Accept(string op)
        {
            if (_pos < _tokens.Count && _tokens[_pos].Kind == "op" && _tokens[_pos].Text == op)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private void Tokenize(string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                        throw new FormatException("A '{{' placeholder is not closed.");
                    var name = text[(i + 2)..close].Trim();
                    if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                        throw new FormatException($"'{name}' is not a valid variable name.");
                    _tokens.Add(("var", name));
                    i = close + 2;
                }
                else if (c is '\'' or '"')
                {
                    var close = text.IndexOf(c, i + 1);
                    if (close < 0)
                        throw new FormatException("A quoted text is not closed.");
                    _tokens.Add(("text", text[(i + 1)..close]));
                    i = close + 1;
                }
                else if (Two(text, i, "&&") || Two(text, i, "||") || Two(text, i, "==") || Two(text, i, "!="))
                {
                    _tokens.Add(("op", text.Substring(i, 2)));
                    i += 2;
                }
                else if (c is '!' or '(' or ')')
                {
                    _tokens.Add(("op", c.ToString()));
                    i++;
                }
                else if (c is '&' or '|' or '=')
                {
                    throw new FormatException($"Unexpected '{c}'; use &&, || and ==.");
                }
                else
                {
                    var sb = new StringBuilder();
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or '!' or '&' or '|' or '=' or '{' or '\'' or '"'))
                        sb.Append(text[i++]);
                    _tokens.Add(("text", sb.ToString()));
                }
            }
        }

        private static bool Two(string text, int i, string pair) => i + 1 < text.Length && text[i] == pair[0] && text[i + 1] == pair[1];
    }
}
