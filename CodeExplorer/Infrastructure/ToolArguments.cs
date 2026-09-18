using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeExplorer;

/// <summary>
///     The reply for a call whose arguments the SDK could not bind. Binding happens before the tool
///     method runs, so neither <see cref="ToolReply" /> nor a per-tool formatter ever sees such a
///     call, and what reached the agent instead was the transport's one sentence — "An error occurred
///     invoking 'glob'." — naming neither the argument that was wrong nor the ones that would have
///     worked (#85). Measured over two real sessions, every recovery from it was trial and error.
///     A call-tool filter rather than nullable parameters on each tool: one registration covers every
///     tool, including the ones no session has mis-called yet, and no tool loses <c>required</c> from
///     the schema it advertises.
///     It names the near-miss spellings and adds none of them: `pattern` where `glob` and `grep`
///     want `glob` and `query`, `path` or `file` where `read_file` wants `paths`, `max_results`
///     where `grep` wants `pageSize`. Each is what an agent's own built-in file tools call the
///     thing, so they keep arriving. Naming them is the whole fix; accepting one would put two
///     spellings on a concept, which CONTEXT.md asks against. (`path` is the real parameter of
///     every tool that takes one file; it is a near miss only at `read_file`, which takes several.)
/// </summary>
internal static partial class ToolArguments
{
    /// <summary>
    ///     What <c>Microsoft.Extensions.AI</c>'s binder names as the parameter at fault when it cannot
    ///     fill a tool's arguments: the dictionary, not the tool parameter inside it.
    /// </summary>
    private const string BinderParameter = "arguments";

    /// <summary>A parameter whose value is a shape its declaration cannot hold.</summary>
    /// <param name="Name">The parameter, spelled as the schema spells it.</param>
    /// <param name="Expected">What the schema says it holds.</param>
    /// <param name="Actual">What arrived instead.</param>
    private sealed record Mistyped(string Name, string Expected, string Actual);

    /// <summary>
    ///     Wraps the call-tool pipeline. The answer is written only where the arguments themselves
    ///     prove the call could not have bound — a required name absent, or a value of a type the
    ///     schema does not allow. Anything else leaves by the path it came in on, so an unreadable
    ///     index still reaches the agent as the infrastructure failure it is (CODING_STANDARDS) and a
    ///     call that bound cleanly is byte-for-byte what it was before this existed.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken);
            }
            // The two shapes the binder fails in: a missing required name is an ArgumentException over
            // the argument dictionary — named, so a tool's own ArgumentException over one of its values
            // is not mistaken for one — and a value it cannot convert is the serializer's JsonException.
            catch (Exception exception) when (exception is JsonException or ArgumentException
                                                  { ParamName: BinderParameter })
            {
                if (Explain(request) is not { } reply) throw;

                // A normal result and not IsError: a call this server can explain is a semantic failure,
                // and every other one of those is a plain string (CODING_STANDARDS).
                return new CallToolResult { Content = [new TextContentBlock { Text = reply }] };
            }
        };

    /// <summary>
    ///     The reply, or null when nothing in the arguments accounts for the failure — in which case
    ///     the call is none of this class's business and the exception belongs to whoever threw it.
    /// </summary>
    private static string? Explain(RequestContext<CallToolRequestParams> request)
    {
        if (request.Params?.Name is not { } tool) return null;
        // Set by the SDK's primitive matching, which runs before this filter; absent only for a call
        // answered by a handler rather than by one of the registered tool types.
        if (request.MatchedPrimitive is not McpServerTool matched) return null;
        var schema = matched.ProtocolTool.InputSchema;
        if (!schema.TryGetProperty("properties", out var properties)) return null;

        var declared = properties.EnumerateObject().ToList();
        var required = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var supplied = request.Params.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var unknown = supplied.Keys
            .Where(name => !declared.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal))).ToList();
        var missing = declared.Where(p => required.Contains(p.Name) && !supplied.ContainsKey(p.Name))
            .Select(p => p.Name).ToList();
        var mistyped = declared
            .Where(p => supplied.TryGetValue(p.Name, out var value) && !Fits(p.Value, value))
            .Select(p => new Mistyped(p.Name, string.Join(" or ", AllowedTypes(p.Value)),
                Kind(supplied[p.Name].ValueKind))).ToList();

        // An unknown name on its own is not proof of anything: a tool whose parameters are all optional
        // binds and answers regardless of it, which is #86 and a different fix. Only a name that had to
        // be there and was not, or a value the schema cannot hold, says the call never ran.
        if (missing.Count == 0 && mistyped.Count == 0) return null;

        var text = new StringBuilder();
        text.Append(Opening(tool, unknown, missing, mistyped)).Append("\n\n");
        foreach (var parameter in declared.Where(p => required.Contains(p.Name)))
            text.Append("Required: ").Append(Described(parameter)).Append('\n');

        var optional = declared.Where(p => !required.Contains(p.Name)).Select(p => Quoted(p.Name)).ToList();
        if (optional.Count > 0) text.Append("Also accepts: ").Append(string.Join(", ", optional)).Append(".\n");
        return text.ToString();
    }

    /// <summary>
    ///     The diagnosis, which is the line the agent acts on. The three faults are reported separately
    ///     because "you spelled one wrongly", "you left one out" and "that value is the wrong shape" are
    ///     different mistakes, and an agent told the wrong one makes a second wasted call.
    /// </summary>
    private static string Opening(string tool, List<string> unknown, List<string> missing, List<Mistyped> mistyped)
    {
        var clauses = new List<string>(2 + mistyped.Count);
        if (unknown.Count > 0)
            clauses.Add($"has no {Names(unknown)} {ToolReply.Plural(unknown.Count, "argument")}");
        if (missing.Count > 0)
            clauses.Add(
                $"was called without its required {Names(missing)} {ToolReply.Plural(missing.Count, "argument")}");
        foreach (var fault in mistyped)
            clauses.Add(
                $"was given {Quoted(fault.Name)} as {Article(fault.Actual)} where it takes {Article(fault.Expected)}");

        return $"`{tool}` {Sentence(clauses)}, so it did not run.";
    }

    /// <summary>
    ///     Joins the clauses with one "and" rather than one per clause: three faults joined the naive
    ///     way read as a list of sentences stuck together, and the diagnosis is the line an agent acts
    ///     on.
    /// </summary>
    private static string Sentence(List<string> clauses) => clauses.Count switch
    {
        1 => clauses[0],
        2 => $"{clauses[0]}, and {clauses[1]}",
        _ => $"{string.Join(", ", clauses[..^1])}, and {clauses[^1]}"
    };

    /// <summary>
    ///     Whether a value the caller sent is one the schema can hold. It has to agree with the binder
    ///     rather than with JSON Schema, because a disagreement here is a sentence blaming an argument
    ///     that was fine. A declaration this does not model is read as no opinion, so an unmodelled
    ///     shape can never be reported as the caller's mistake.
    /// </summary>
    private static bool Fits(JsonElement declared, JsonElement value) =>
        AllowedTypes(declared).Any(name => Accepts(name, value));

    private static bool Accepts(string? schemaType, JsonElement value) => schemaType switch
    {
        "string" => value.ValueKind is JsonValueKind.String,
        // A quoted numeral counts: the SDK deserializes with AllowReadingFromString, so `context: "4"`
        // binds and must not be reported as a mistake by a check that runs over the same arguments.
        "integer" or "number" => value.ValueKind is JsonValueKind.Number ||
                                 (value.ValueKind is JsonValueKind.String &&
                                  double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out _)),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind is JsonValueKind.Array,
        "object" => value.ValueKind is JsonValueKind.Object,
        "null" => value.ValueKind is JsonValueKind.Null,
        _ => true
    };

    /// <summary>
    ///     What the schema says a parameter holds: one name, or the several a nullable parameter is
    ///     written as (<c>["string","null"]</c>). It is read both to judge a value and to name the type
    ///     back to the agent, so it is walked in one place — the two disagreeing would be a reply that
    ///     blamed an argument for holding what it went on to say the argument takes.
    /// </summary>
    private static IEnumerable<string> AllowedTypes(JsonElement declared)
    {
        // Absent means the schema has no opinion, which must read as "anything fits" rather than as an
        // empty set, which would fit nothing and blame every value.
        if (!declared.TryGetProperty("type", out var type)) return ["value"];

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(entry => entry.GetString()).OfType<string>()
            : [type.GetString() ?? "value"];
    }

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "null"
    };

    private static string Article(string noun) =>
        noun.StartsWith('a') || noun.StartsWith('e') || noun.StartsWith('i') || noun.StartsWith('o') ||
        noun.StartsWith('u')
            ? $"an {noun}"
            : $"a {noun}";

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(Quoted));

    private static string Quoted(string name) => $"`{name}`";

    /// <summary>
    ///     A parameter and the prose the agent was already given for it, which is where the example in
    ///     the reply comes from. Taken from the schema rather than written here so that it cannot drift
    ///     from the tool's own <c>[Description]</c>; those are written as blocks, and a reply is read as
    ///     a line, so the line breaks in them are flattened.
    /// </summary>
    private static string Described(JsonProperty parameter)
    {
        if (!parameter.Value.TryGetProperty("description", out var description) ||
            description.GetString() is not { } prose)
            return Quoted(parameter.Name);

        return $"{Quoted(parameter.Name)} — {Whitespace().Replace(prose, " ").Trim()}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
