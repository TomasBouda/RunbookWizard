using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RunbookWizard.Core.Parsing;

/// <summary>
/// YAML to JSON tree with YAML 1.2 core-schema typing of plain scalars (true/false, null, integers, floats);
/// quoted scalars stay strings. A few keys are text by definition (a <c>version: 1.0</c> must not turn into a number).
/// </summary>
internal static class YamlJson
{
    private static readonly HashSet<string> TextKeys = new(StringComparer.Ordinal)
    {
        "version", "author", "title", "description", "why", "confirm", "warning", "label", "id", "pattern",
        "script", "when", "name", "shell", "workingDirectory", "outputMatches", "outputNotMatches", "variable",
        "choices", "tags", "platforms", "env",
    };

    private static readonly Regex IntRegex = new(@"^[-+]?[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FloatRegex = new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);

    /// <exception cref="YamlException">The text is not valid YAML.</exception>
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode, null);
    }

    private static JsonNode? Convert(YamlNode node, string? key)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (k, v) in map.Children)
                {
                    var name = (k as YamlScalarNode)?.Value ?? throw new YamlException("Mapping keys must be plain text");
                    obj[name] = Convert(v, key == "env" ? "env" : name);
                }
                return obj;
            case YamlSequenceNode seq:
                var arr = new JsonArray();
                foreach (var item in seq.Children)
                    arr.Add(Convert(item, key));
                return arr;
            case YamlScalarNode scalar:
                return Scalar(scalar, key);
            default:
                return null;
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar, string? key)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain || (key is not null && TextKeys.Contains(key)))
            return JsonValue.Create(value);

        if (value.Length == 0 || value == "~" || value is "null" or "Null" or "NULL")
            return null;
        if (value is "true" or "True" or "TRUE")
            return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE")
            return JsonValue.Create(false);
        if (IntRegex.IsMatch(value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (FloatRegex.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(value);
    }
}
