using System.Globalization;
using System.Text;
using System.Text.Json;
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
internal static class ToolArguments
{
    /// <summary>
    ///     Stands in for the type of a parameter whose declaration names none, so that "the schema has
    ///     no opinion" travels as a type that fits everything rather than as an empty set, which would
    ///     fit nothing and blame every value.
    /// </summary>
    private const string AnyType = "value";

    /// <summary>
    ///     Wraps the call-tool pipeline, in two halves for two degrees of certainty.
    ///     A required name that did not arrive cannot bind, whatever the SDK does with it, so that
    ///     call is answered without being invoked. It costs this class no knowledge of the binder:
    ///     catching the refusal instead would mean recognising it among every other
    ///     <see cref="ArgumentException" /> by a parameter name the SDK keeps private.
    ///     A value of a shape the declaration cannot hold is the other half, and there the judgement
    ///     is this class's rather than the binder's — <see cref="Accepts" /> could be wrong about what
    ///     binds. So that one is only ever read off a call that has already failed, where the worst a
    ///     wrong guess can do is fail to improve the sentence the caller was getting anyway.
    ///     Everything else leaves by the path it came in on: an unreadable index still reaches the
    ///     agent as the infrastructure failure it is (CODING_STANDARDS), and a call that bound cleanly
    ///     is byte-for-byte what it was before this existed.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (LacksARequiredArgument(request) && Explain(request) is { } absent) return Reply(absent);

            try
            {
                return await next(request, cancellationToken);
            }
            catch (JsonException)
            {
                if (Explain(request) is not { } mistyped) throw;

                return Reply(mistyped);
            }
        };

    /// <summary>
    ///     A normal result and not <c>IsError</c>: a call this server can explain is a semantic failure,
    ///     and every other one of those is a plain string (CODING_STANDARDS).
    /// </summary>
    private static CallToolResult Reply(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    /// <summary>
    ///     The cheap half of the question, asked on every call and so deliberately not the full
    ///     <see cref="Explain" /> walk: the <c>required</c> list of these tools holds one name or none.
    /// </summary>
    private static bool LacksARequiredArgument(RequestContext<CallToolRequestParams> request)
    {
        if (request.MatchedPrimitive is not McpServerTool matched) return false;
        if (!matched.ProtocolTool.InputSchema.TryGetProperty("required", out var required)) return false;

        var supplied = request.Params?.Arguments;
        return required.EnumerateArray()
            .Any(name => name.GetString() is { } required_ && supplied?.ContainsKey(required_) != true);
    }

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

        var missing = declared.Where(p => required.Contains(p.Name) && !supplied.ContainsKey(p.Name))
            .Select(p => p.Name).ToList();
        var mistyped = Mistyped(declared, supplied);

        // An unknown name on its own is not proof of anything: a tool whose parameters are all optional
        // binds and answers regardless of it, which is #86 and a different fix. Only a name that had to
        // be there and was not, or a value the schema cannot hold, says the call never ran.
        if (missing.Count == 0 && mistyped.Count == 0) return null;

        // Below the bail, because an unknown name is named in the reply and never causes one.
        var unknown = supplied.Keys
            .Where(name => !declared.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal))).ToList();

        var text = new StringBuilder();
        text.Append(Opening(tool, unknown, missing, mistyped)).Append("\n\n");
        foreach (var parameter in declared.Where(p => required.Contains(p.Name)))
            text.Append("Required: ").Append(Described(parameter)).Append('\n');

        var optional = declared.Where(p => !required.Contains(p.Name)).Select(p => Quoted(p.Name)).ToList();
        if (optional.Count > 0) text.Append("Also accepts: ").Append(string.Join(", ", optional)).Append(".\n");
        return text.ToString();
    }

    /// <summary>
    ///     One clause per argument whose value is a shape its declaration cannot hold. The clause is
    ///     built here rather than the fault being carried out as data, because what the schema allows
    ///     is read once to judge the value and to name the type back, and the two coming from separate
    ///     walks would be a reply that blamed an argument for holding what it went on to say it takes.
    /// </summary>
    private static List<string> Mistyped(List<JsonProperty> declared, IDictionary<string, JsonElement> supplied)
    {
        var clauses = new List<string>();
        foreach (var parameter in declared)
        {
            if (!supplied.TryGetValue(parameter.Name, out var value)) continue;

            var allowed = AllowedTypes(parameter.Value);
            if (allowed.Exists(name => Accepts(name, value))) continue;

            clauses.Add($"was given {Quoted(parameter.Name)} as {Article(Kind(value.ValueKind))} "
                        + $"where it takes {Article(string.Join(" or ", allowed))}");
        }

        return clauses;
    }

    /// <summary>
    ///     The diagnosis, which is the line the agent acts on. The three faults are reported separately
    ///     because "you spelled one wrongly", "you left one out" and "that value is the wrong shape" are
    ///     different mistakes, and an agent told the wrong one makes a second wasted call.
    /// </summary>
    private static string Opening(string tool, List<string> unknown, List<string> missing, List<string> mistyped)
    {
        var clauses = new List<string>(2 + mistyped.Count);
        if (unknown.Count > 0)
            clauses.Add($"has no {Names(unknown)} {ToolReply.Plural(unknown.Count, "argument")}");
        if (missing.Count > 0)
            clauses.Add(
                $"was called without its required {Names(missing)} {ToolReply.Plural(missing.Count, "argument")}");
        clauses.AddRange(mistyped);

        return $"`{tool}` {Sentence(clauses)}, so it did not run.";
    }

    /// <summary>
    ///     Joins the clauses with one "and" rather than one per clause: three faults joined the naive
    ///     way read as a list of sentences stuck together, and the diagnosis is the line an agent acts
    ///     on.
    /// </summary>
    private static string Sentence(List<string> clauses) =>
        clauses.Count == 1 ? clauses[0] : $"{string.Join(", ", clauses[..^1])}, and {clauses[^1]}";

    /// <summary>
    ///     Whether a value the caller sent is one that type can hold. It has to agree with the binder
    ///     rather than with JSON Schema, because a disagreement here is a sentence blaming an argument
    ///     that was fine — so <see cref="Kind" /> is the one table and this names only the two places
    ///     the binder is looser than it. A type this does not model is read as no opinion.
    /// </summary>
    private static bool Accepts(string schemaType, JsonElement value) => schemaType switch
    {
        // A quoted numeral counts: the SDK deserializes with AllowReadingFromString, so `context: "4"`
        // binds and must not be reported as a mistake by a check that runs over the same arguments.
        "integer" or "number" => value.ValueKind is JsonValueKind.Number ||
                                 (value.ValueKind is JsonValueKind.String &&
                                  double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out _)),
        "string" or "boolean" or "array" or "object" or "null" => schemaType == Kind(value.ValueKind),
        _ => true
    };

    /// <summary>
    ///     What the schema says a parameter holds: one type, or the several a nullable parameter is
    ///     written as (<c>["string","null"]</c>).
    /// </summary>
    private static List<string> AllowedTypes(JsonElement declared)
    {
        if (!declared.TryGetProperty("type", out var type)) return [AnyType];

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList()
            : [type.GetString() ?? AnyType];
    }

    /// <summary>The schema's name for what arrived. The one table the two directions are read from.</summary>
    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "null"
    };

    private static string Article(string noun) => $"{(noun.Length > 0 && "aeiou".Contains(noun[0]) ? "an" : "a")} {noun}";

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

        return $"{Quoted(parameter.Name)} — {string.Join(' ', prose.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}";
    }
}
