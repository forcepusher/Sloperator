using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// Fixes the tool calls that heavily quantized models get slightly wrong, so a near miss runs
/// instead of costing a whole round trip: sloppy JSON, misspelled tool names and arguments
/// under the wrong key.
/// </summary>
public static partial class ToolCallRepair
{
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["click"] = "mouse_click", ["left_click"] = "mouse_click", ["double_click"] = "mouse_click",
        ["right_click"] = "mouse_click", ["mouse_double_click"] = "mouse_click", ["ctrl_click"] = "mouse_click", ["shift_click"] = "mouse_click",
        ["hold_key"] = "press_keys", ["hold_keys"] = "press_keys", ["key_hold"] = "press_keys",
        ["move_mouse"] = "mouse_move", ["hover"] = "mouse_move", ["mouse_hover"] = "mouse_move",
        ["drag"] = "mouse_drag", ["left_click_drag"] = "mouse_drag",
        ["type"] = "type_text", ["typing"] = "type_text", ["input_text"] = "type_text", ["write_text"] = "type_text",
        ["key"] = "press_keys", ["keys"] = "press_keys", ["press_key"] = "press_keys", ["hotkey"] = "press_keys", ["keyboard"] = "press_keys",
        ["take_screenshot"] = "screenshot", ["capture_screen"] = "screenshot", ["screen_capture"] = "screenshot",
        ["sleep"] = "wait",
        ["wait_for_change"] = "wait_until_change", ["wait_until_changed"] = "wait_until_change", ["wait_for_screen_change"] = "wait_until_change",
        ["shell"] = "run_shell", ["bash"] = "run_shell", ["powershell"] = "run_shell", ["run_command"] = "run_shell",
        ["execute_command"] = "run_shell", ["terminal"] = "run_shell", ["cmd"] = "run_shell",
        ["read"] = "read_file", ["cat"] = "read_file", ["open_file"] = "read_file",
        ["write"] = "write_file", ["create_file"] = "write_file", ["save_file"] = "write_file",
        ["edit"] = "edit_file", ["replace_in_file"] = "edit_file", ["str_replace"] = "edit_file",
        ["ls"] = "list_directory", ["list_files"] = "list_directory", ["list_dir"] = "list_directory", ["dir"] = "list_directory",
        ["index"] = "index_folder", ["index_project"] = "index_folder", ["index_directory"] = "index_folder", ["index_codebase"] = "index_folder",
        ["codebase_search"] = "index_folder", ["search_codebase"] = "index_folder", ["search_project"] = "index_folder", ["search_index"] = "index_folder",
        ["git_diff"] = "diff", ["show_diff"] = "diff", ["compare"] = "diff", ["compare_files"] = "diff", ["show_changes"] = "diff", ["git_show"] = "diff",
        ["scp"] = "copy", ["copy_file"] = "copy", ["copy_files"] = "copy", ["upload"] = "copy", ["download"] = "copy",
        ["ssh_run"] = "ssh", ["ssh_command"] = "ssh", ["remote_shell"] = "ssh", ["run_ssh"] = "ssh",
    };

    // Argument names models borrow from other agent frameworks, per canonical parameter.
    private static readonly Dictionary<string, string[]> ArgAliases = new()
    {
        ["text"] = ["content", "value", "string", "input", "message"],
        ["keys"] = ["key", "combo", "shortcut", "hotkey", "keys_combo"],
        ["command"] = ["cmd", "script", "shell_command", "code"],
        ["path"] = ["file", "file_path", "filepath", "filename", "directory", "dir", "folder"],
        ["content"] = ["text", "data", "contents", "body"],
        ["old_text"] = ["old", "old_str", "old_string", "search", "find"],
        ["new_text"] = ["new", "new_str", "new_string", "replace", "replacement"],
        ["seconds"] = ["duration", "time", "secs", "delay"],
        ["direction"] = ["dir", "scroll_direction"],
        ["amount"] = ["clicks", "scroll_amount", "steps", "distance"],
        ["modifiers"] = ["modifier", "keys", "hold_keys", "holding", "with_keys"],
        ["hold_seconds"] = ["hold", "hold_time", "hold_duration", "duration", "seconds"],
        ["screenshot_on_end"] = ["screenshot", "take_screenshot", "screenshot_after"],
        ["from"] = ["source", "src", "local_path", "remote_path"],
        ["to"] = ["destination", "dest", "target_path"],
        ["host"] = ["server", "hostname", "ssh_host"],
        ["query"] = ["q", "search_query", "search_term", "keywords"],
        ["other"] = ["other_path", "path2", "second_path", "file2", "compare_to", "compare_with"],
        ["against"] = ["base", "ref", "since"],
        ["commit"] = ["sha", "hash", "revision"],
    };

    /// <summary>Maps a tool name the model produced to a registered tool, or returns null.</summary>
    public static Tool? ResolveTool(string name, ToolRegistry registry)
    {
        if (registry.Find(name) is { } exact)
            return exact;
        var normalized = Regex.Replace(name.Trim(), @"^(functions|tools|default_api)[.:]", "", RegexOptions.IgnoreCase)
            .Replace('-', '_').Replace(' ', '_').Replace('.', '_').ToLowerInvariant();
        if (registry.Find(normalized) is { } n)
            return n;
        if (NameAliases.TryGetValue(normalized, out var alias) && registry.Find(alias) is { } aliased)
            return aliased;
        // An MCP tool called without its server prefix.
        return registry.FindBySuffix(normalized);
    }

    /// <summary>
    /// Keeps the intent of aliased names like "double_click" or "ctrl_click" that map onto a more
    /// general tool. Only parameters the tool has are filled in, so an MCP tool whose name happens
    /// to contain "double" gets no clicks argument.
    /// </summary>
    public static void ApplyNameHints(string calledName, JsonObject args, Tool tool)
    {
        if (tool.Parameters["properties"] is not JsonObject props)
            return;
        var n = calledName.Trim().ToLowerInvariant().Replace('-', '_');
        if (n.Contains("double")) Hint("clicks", 2);
        if (n.Contains("triple")) Hint("clicks", 3);
        if (!n.Contains("click"))
            return;
        if (n.Contains("right")) Hint("button", "right");
        if (n.Contains("middle")) Hint("button", "middle");
        if (new[] { "ctrl", "shift", "alt" }.Where(k => n.Contains(k)).ToArray() is { Length: > 0 } held)
            Hint("modifiers", new JsonArray(held.Select(k => (JsonNode)k).ToArray()));

        void Hint(string key, JsonNode value)
        {
            if (props.ContainsKey(key) && !args.ContainsKey(key))
                args[key] = value;
        }
    }

    /// <summary>Parses tool arguments, repairing the usual JSON mistakes if strict parsing fails.</summary>
    /// <param name="tool">The tool they are for: where a key comes twice, it says which value the key keeps.</param>
    public static JsonObject ParseArguments(string raw, Tool? tool = null)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return [];
        if (TryParseObject(raw, tool) is { } strict)
            return strict;

        var text = StripWrappers(raw);
        foreach (var candidate in Candidates(text))
        {
            if (TryParseObject(candidate, tool) is { } obj)
                return obj;
        }
        throw new ToolArgumentException("Tool arguments are not valid JSON. Send a single JSON object like {\"x\": 100, \"y\": 200}.");
    }

    /// <summary>
    /// Moves arguments that arrived under a different name to the name the tool expects,
    /// and unpacks coordinate pairs such as "coordinate": [x, y].
    /// </summary>
    public static JsonObject NormalizeArguments(JsonObject args, Tool tool)
    {
        if (tool.Parameters["properties"] is not JsonObject props)
            return args;
        var expected = props.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        // Case differences ("X", "Path").
        foreach (var key in args.Select(a => a.Key).ToList())
        {
            if (expected.Contains(key)) continue;
            var match = expected.FirstOrDefault(e => string.Equals(e, key, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !args.ContainsKey(match))
                Move(args, key, match);
        }

        // Coordinate pairs in all the shapes models like to send.
        if (expected.Contains("x") && !args.ContainsKey("x"))
            UnpackPoint(args, ["coordinate", "coordinates", "coord", "position", "point", "pos", "location", "start", "from", "start_coordinate"], "x", "y");
        if (expected.Contains("to_x") && !args.ContainsKey("to_x"))
            UnpackPoint(args, ["to", "end", "end_coordinate", "target", "destination", "coordinate2"], "to_x", "to_y");

        foreach (var (canonical, aliases) in ArgAliases)
        {
            if (!expected.Contains(canonical) || args.ContainsKey(canonical)) continue;
            var found = aliases.FirstOrDefault(a => args.ContainsKey(a) && !expected.Contains(a));
            if (found is not null)
                Move(args, found, canonical);
        }

        // Last resort: one required argument missing and exactly one unknown argument present.
        var required = (tool.Parameters["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToList() ?? [];
        var missing = required.Where(r => !args.ContainsKey(r)).ToList();
        var unknown = args.Select(a => a.Key).Where(k => !expected.Contains(k)).ToList();
        if (missing.Count == 1 && unknown.Count == 1)
            Move(args, unknown[0], missing[0]);

        return args;
    }

    private static void UnpackPoint(JsonObject args, string[] keys, string xKey, string yKey)
    {
        foreach (var key in keys)
        {
            switch (args[key])
            {
                case JsonArray { Count: >= 2 } arr:
                    args[xKey] = arr[0]?.DeepClone();
                    args[yKey] = arr[1]?.DeepClone();
                    args.Remove(key);
                    return;
                case JsonObject o when o["x"] is not null && o["y"] is not null:
                    args[xKey] = o["x"]!.DeepClone();
                    args[yKey] = o["y"]!.DeepClone();
                    args.Remove(key);
                    return;
                case JsonValue v when v.TryGetValue<string>(out var s) && PointText().Match(s) is { Success: true } m:
                    args[xKey] = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    args[yKey] = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    args.Remove(key);
                    return;
            }
        }
    }

    private static void Move(JsonObject args, string from, string to)
    {
        var value = args[from]?.DeepClone();
        args.Remove(from);
        args[to] = value;
    }

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>A call's arguments as the JSON object they are, without repairs; null when they aren't one. A key given twice keeps its last value.</summary>
    public static JsonObject? AsObject(string arguments) => TryParseObject(arguments.Trim());

    private static JsonObject? TryParseObject(string text, Tool? tool = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Lenient);
            var root = doc.RootElement.Clone();
            return root.ValueKind switch
            {
                JsonValueKind.Object => Build(root, tool),
                // Arguments double-encoded as a JSON string: "{\"x\": 1}"
                JsonValueKind.String when root.GetString() is { } inner && inner.TrimStart().StartsWith('{') => TryParseObject(inner, tool),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The object with each key once. A JsonObject parsed with a key given twice throws at the first
    /// look inside it, and that ended the run: Claude wrote the second part of a file under "append",
    /// then added "append": true. Where the tool says what a key takes, the value that fits keeps the
    /// key, and the other goes to a required argument still missing that it fits (there: content).
    /// Otherwise the last one wins, as JavaScript and Python read it.
    /// </summary>
    private static JsonObject Build(JsonElement element, Tool? tool)
    {
        var props = tool?.Parameters["properties"] as JsonObject;
        var obj = new JsonObject();
        var spare = new List<JsonElement>();
        foreach (var same in element.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
        {
            var values = same.Select(p => p.Value).ToList();
            var keep = values.FindLastIndex(v => Fits(v, props?[same.Key]));
            if (keep < 0)
                keep = values.Count - 1;
            obj[same.Key] = NodeOf(values[keep]);
            spare.AddRange(values.Where((_, i) => i != keep));
        }
        var required = (tool?.Parameters["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).OfType<string>().ToList() ?? [];
        foreach (var value in spare)
        {
            if (required.FirstOrDefault(r => !obj.ContainsKey(r) && props?[r]?["type"] is not null && Fits(value, props[r])) is { } missing)
                obj[missing] = NodeOf(value);
        }
        return obj;
    }

    private static JsonNode? NodeOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => Build(value, null),
        JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(NodeOf).ToArray()),
        _ => JsonValue.Create(value),
    };

    /// <summary>Whether a value is of the type a parameter's schema names; any value fits a schema that names none.</summary>
    private static bool Fits(JsonElement value, JsonNode? schema)
    {
        string[]? types = schema?["type"] switch
        {
            JsonValue t when t.TryGetValue<string>(out var one) => [one],
            JsonArray many => [.. many.Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>()],
            _ => null,
        };
        return types is null || types.Any(t => t switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" or "number" => value.ValueKind == JsonValueKind.Number,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        });
    }

    /// <summary>
    /// Whether tool arguments are a whole JSON object as they came (or nothing), with no repair needed.
    /// A key given twice needs one: sent back as it came, it is the same crash at a later look.
    /// </summary>
    public static bool IsWholeJson(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0)
            return true;
        try
        {
            using var doc = JsonDocument.Parse(t, Lenient);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? !RepeatsKey(doc.RootElement) : TryParseObject(t) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool RepeatsKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1)
                                || value.EnumerateObject().Any(p => RepeatsKey(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(RepeatsKey),
        _ => false,
    };

    private static string StripWrappers(string text)
    {
        // Only a fence around the whole of it: one inside a value (a Markdown file's ```python
        // block, written with write_file) was taken out of the file.
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var open and > 0 && text.LastIndexOf("```", StringComparison.Ordinal) is var close and > 0 && close > open)
            text = text[(open + 1)..close].Trim();
        text = text.Replace("<tool_call>", "").Replace("</tool_call>", "").Trim();
        var start = text.IndexOf('{');
        return start > 0 ? text[start..] : text;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        // Python-style literals and single quotes.
        var pythonish = PythonLiterals(text);
        yield return pythonish;
        if (!pythonish.Contains('"'))
            yield return pythonish.Replace('\'', '"');

        // Unquoted keys: {x: 10, y: 20}
        var quotedKeys = QuoteBareKeys(pythonish);
        yield return quotedKeys;

        // Truncated output: close whatever is still open.
        yield return Balance(quotedKeys);
        // Extra text after the object.
        yield return FirstObject(quotedKeys);
    }

    /// <summary>
    /// Python's True, False and None as JSON's, outside strings only: replaced everywhere, a script
    /// written with write_file got "return null" for "return None" whenever its call needed a repair.
    /// </summary>
    internal static string PythonLiterals(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }
            if (char.IsLetter(c) && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_'))
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                var word = text[i..end];
                sb.Append(word switch { "True" => "true", "False" => "false", "None" => "null", _ => word });
                i = end - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Quotes bare object keys ({x: 1} to {"x": 1}) without touching text inside strings.</summary>
    internal static string QuoteBareKeys(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var inString = false;
        var expectKey = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; expectKey = false; sb.Append(c); continue; }
            if (c is '{' or ',') { expectKey = true; sb.Append(c); continue; }
            if (expectKey && (char.IsLetter(c) || c == '_'))
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var j = i;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                var word = text[start..i];
                if (j < text.Length && text[j] == ':') sb.Append('"').Append(word).Append('"');
                else sb.Append(word);
                i--;
                expectKey = false;
                continue;
            }
            if (!char.IsWhiteSpace(c)) expectKey = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Balance(string text)
    {
        var stack = new Stack<char>();
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']' when stack.Count > 0: stack.Pop(); break;
            }
        }
        var sb = new StringBuilder(text.TrimEnd().TrimEnd(','));
        if (inString) sb.Append('"');
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static string FirstObject(string text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[..(i + 1)];
        }
        return text;
    }

    [GeneratedRegex(@"^\s*\(?\s*(-?\d+(?:\.\d+)?)\s*[, ]\s*(-?\d+(?:\.\d+)?)\s*\)?\s*$")]
    private static partial Regex PointText();
}
