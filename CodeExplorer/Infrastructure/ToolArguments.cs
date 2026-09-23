using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     What a tool owes a caller about the arguments it was called with, in the two places the tool
///     itself cannot say it.
///     A call the SDK could not bind is the first. Binding happens before the tool method runs, so
///     neither <see cref="ToolReply" /> nor a per-tool formatter ever sees such a call, and what
///     reached the agent instead was the transport's one sentence — "An error occurred invoking
///     'glob'." — naming neither the argument that was wrong nor the ones that would have worked
///     (#85). Measured over two real sessions, every recovery from it was trial and error.
///     A call that bound in spite of a name the tool does not have is the second, and the quieter one:
///     where every parameter is optional there is no failure at all, only a confident answer to a
///     question nobody asked (#86). Both are read off the same two things — the arguments that arrived
///     and the schema the agent was given — which is why they are one class.
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
    private const string _anyType = "value";

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
    ///     A call that bound and ran is where the third fault lives: a tool whose parameters are all
    ///     optional binds regardless of a name it does not have, runs with its defaults, and answers a
    ///     question it was never asked (#86). That answer is kept — it is the tool's real answer to the
    ///     arguments it could read — and the stray name is said in front of it.
    ///     Everything else leaves by the path it came in on: an unreadable index still reaches the
    ///     agent as the infrastructure failure it is (CODING_STANDARDS), and a call carrying nothing
    ///     stray is byte-for-byte what it was before this existed.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (LacksARequiredArgument(request) && Explain(request) is { } absent) return Reply(absent);

            try
            {
                return Caveated(await next(request, cancellationToken), request);
            }
            catch (JsonException)
            {
                if (Explain(request) is not { } mistyped) throw;

                return Reply(mistyped);
            }
        };

    /// <summary>
    ///     The reply a call that ran is read with, when it carried a name the tool does not have.
    ///     Prepended to the tool's own text rather than added as a second content block, because a
    ///     caveat an agent might read second is a caveat it acts after.
    ///     git_log, which built this sentence for itself until this filter took it over, put it inside
    ///     the answer <see cref="ToolReply.Cap" /> measures, so that a capped reply could not say
    ///     "truncated at 40 KB" while being larger than that. From out here that is not available: the
    ///     tool has already rendered and capped, and re-capping would cut the tail, which is exactly
    ///     where the advice for getting the rest of a truncated answer lives. So the ceiling is
    ///     exceeded by this one sentence instead — under a percent of it, on the rare call that carries
    ///     a stray name — which is what the cap is for (a context spent on one reply) rather than what
    ///     it promises to the byte. The alternative was every tool declaring its own sham parameters to
    ///     catch the four spellings someone had already seen, which is what #86 is removing.
    ///     A failed result is left alone. Its text is an infrastructure failure on its way to becoming
    ///     an <c>McpException</c>, and a stray argument is not why it failed — saying so in front of it
    ///     would offer a spelling to fix against a project that will not answer either way.
    /// </summary>
    private static CallToolResult Caveated(CallToolResult result, RequestContext<CallToolRequestParams> request)
    {
        if (result.IsError == true) return result;
        if (Ignored(request) is not { } caveat) return result;

        int first = -1;
        for (int i = 0; i < result.Content.Count && first < 0; i++)
            if (result.Content[i] is TextContentBlock) first = i;
        if (first < 0) return result;

        // A new list rather than an assignment into the SDK's: nothing promises the one it handed back
        // is resizable or writable, and an IList that refuses would throw out of a call that had
        // already succeeded — reaching the agent as the one shape it cannot read an explanation from.
        var content = result.Content.ToList();
        content[first] = new TextContentBlock { Text = caveat + ((TextContentBlock)content[first]).Text };
        result.Content = content;
        return result;
    }

    /// <summary>
    ///     What a call carried that its tool does not have, said in front of the answer, or null when
    ///     it carried nothing stray.
    ///     The sentence says only what was ignored and nothing about what follows it, because what
    ///     follows may be a log, an empty page, a project with no history or an unbuilt index — and a
    ///     caveat that called one of those "the unfiltered answer" would be an absence dressed as a
    ///     result, which is the failure this whole filter exists to stop (CODING_STANDARDS, Errors).
    ///     It names what the tool does take from the schema, so a caller learns the right spelling in
    ///     the same breath. None of the near misses is accepted anywhere: one spelling per concept
    ///     (CONTEXT.md).
    /// </summary>
    private static string? Ignored(RequestContext<CallToolRequestParams> request)
    {
        if (Called(request) is not { } called) return null;
        (string tool, var schema) = called;

        if (Stray(request.Params?.Arguments, schema) is not { } unknown) return null;
        var declared = Properties(schema).ConvertAll(p => p.Name);

        // "Ignored" is the whole claim, and a clause spelling out that nothing below was narrowed by
        // it restates it.
        string them = unknown.Count == 1 ? "it was" : "they were";
        // A tool that takes nothing declares no properties at all, and it is one of the tools this is
        // most needed at: there is no name a stray one could have been meant as.
        string takes = declared.Count == 0 ? "It takes no arguments" : $"It takes {And(declared)}";
        return $"`{tool}` has no {Or(unknown)} argument; {them} ignored. {takes}.\n\n";
    }

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
        foreach (var name in required.EnumerateArray())
            if (name.GetString() is { } parameter && supplied?.ContainsKey(parameter) != true)
                return true;
        return false;
    }

    /// <summary>
    ///     The reply, or null when nothing in the arguments accounts for the failure — in which case
    ///     the call is none of this class's business and the exception belongs to whoever threw it.
    /// </summary>
    private static string? Explain(RequestContext<CallToolRequestParams> request)
    {
        if (Called(request) is not { } called) return null;
        (string tool, var schema) = called;

        var declared = Properties(schema);
        var required = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var supplied = request.Params?.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var missing = declared.Where(p => required.Contains(p.Name) && !supplied.ContainsKey(p.Name))
            .Select(p => p.Name).ToList();
        var mistyped = Mistyped(declared, supplied);

        // An unknown name on its own is not proof of anything: a tool whose parameters are all optional
        // binds and answers regardless of it, which is #86 and a different fix. Only a name that had to
        // be there and was not, or a value the schema cannot hold, says the call never ran.
        if (missing.Count == 0 && mistyped.Count == 0) return null;

        // Below the bail, because an unknown name is named in the reply and never causes one.
        var unknown = Stray(request.Params?.Arguments, schema) ?? [];

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
            clauses.Add($"has no {Or(unknown)} argument");
        if (missing.Count > 0)
            clauses.Add(
                $"was called without its required {And(missing)} {ToolReply.Plural(missing.Count, "argument")}");
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
        if (!declared.TryGetProperty("type", out var type)) return [_anyType];

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList()
            : [type.GetString() ?? _anyType];
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

    /// <summary>
    ///     The tool a call matched and the schema it advertises, or null for a call that matched no
    ///     registered tool — one answered by a handler rather than by a tool type, which is none of
    ///     this class's business. <c>MatchedPrimitive</c> is set by the SDK's primitive matching, which
    ///     runs before this filter.
    ///     Both replies start here, because the two reading the request differently would be one of
    ///     them naming a tool or a parameter the other had not seen.
    /// </summary>
    private static (string Tool, JsonElement Schema)? Called(RequestContext<CallToolRequestParams> request) =>
        request.Params?.Name is { } tool && request.MatchedPrimitive is McpServerTool matched
            ? (tool, matched.ProtocolTool.InputSchema)
            : null;

    /// <summary>
    ///     The parameters a schema declares, and none rather than a failure when it declares no
    ///     <c>properties</c> at all — which is how a tool that takes no arguments is advertised.
    /// </summary>
    private static List<JsonProperty> Properties(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) ? properties.EnumerateObject().ToList() : [];

    /// <summary>
    ///     The names a call carried that its tool does not declare. Read in one place because both
    ///     replies name them and the two disagreeing about what counts as stray would be one of them
    ///     offering a spelling the other had just refused.
    ///     Null rather than an empty list for a call that carried nothing stray, and asked of the
    ///     schema directly rather than of a list of its names, because this runs on every call that
    ///     succeeds and nearly all of them are clean: that answer costs no list at all. The lookup is
    ///     ordinal, as the binder's is.
    /// </summary>
    internal static List<string>? Stray(IDictionary<string, JsonElement>? arguments, JsonElement schema)
    {
        if (arguments is null || arguments.Count == 0) return null;

        bool declares = schema.TryGetProperty("properties", out var properties);
        List<string>? stray = null;
        foreach (string name in arguments.Keys)
            if (!declares || !properties.TryGetProperty(name, out _))
                (stray ??= []).Add(name);
        return stray;
    }

    /// <summary>A list of things the sentence is about together — "`repo`, `limit` and `page`".</summary>
    private static string And(List<string> names) => Joined(names, "and");

    /// <summary>
    ///     A list of things the sentence is about severally — "`author`, `grep` or `commit`" — which is
    ///     what a list of faults is: the reply says what none of them did, not what they did together.
    /// </summary>
    private static string Or(List<string> names) => Joined(names, "or");

    private static string Joined(List<string> names, string conjunction)
    {
        string commas = string.Join(", ", names.Take(Math.Max(names.Count - 1, 0)).Select(Quoted));
        return names.Count <= 1 ? string.Join("", names.Select(Quoted)) : $"{commas} {conjunction} {Quoted(names[^1])}";
    }

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
