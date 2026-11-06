using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Mcp;

public sealed partial class McpTool
{
    // What JSON Schema draft 2020-12 lets each keyword hold. Going by keyword, a property that
    // happens to be called "items" or "required" is left alone, and so is what enum and default hold.
    private static readonly string[] OneSchema = ["additionalProperties", "propertyNames", "items", "contains", "if", "then", "else", "not", "unevaluatedItems", "unevaluatedProperties", "contentSchema"];
    private static readonly string[] SchemaLists = ["allOf", "anyOf", "oneOf", "prefixItems"];
    private static readonly string[] SchemaMaps = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"];
    private static readonly string[] Numbers = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"];
    private static readonly string[] Counts = ["minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "minContains", "maxContains"];
    private static readonly string[] Texts = ["title", "description", "$comment", "format", "pattern", "$schema", "$ref", "$dynamicRef", "$recursiveRef", "contentEncoding", "contentMediaType"];
    private static readonly string[] Anchors = ["$anchor", "$dynamicAnchor", "$recursiveAnchor"];
    private static readonly string[] Flags = ["uniqueItems", "readOnly", "writeOnly", "deprecated"];
    private static readonly string[] Lists = ["enum", "examples"];
    private static readonly HashSet<string> TypeNames = ["array", "boolean", "integer", "null", "number", "object", "string"];

    /// <summary>
    /// Makes a schema, at every depth, one that draft 2020-12 accepts. Anthropic checks a tool's
    /// schema against it and refuses the whole request, so one tool with a schema from an older
    /// draft (zod's tuples) or with a slip in it failed every message of the chat. What older drafts
    /// said differently is said the new way; a keyword holding what it can't hold is left out, as
    /// the server still checks the arguments it gets.
    /// </summary>
    private static void ToDraft2020(JsonObject schema)
    {
        if (schema["items"] is JsonArray tuple)
            TupleToItems(schema, tuple);
        schema.Remove("additionalItems");
        NumericBound(schema, "exclusiveMinimum", "minimum");
        NumericBound(schema, "exclusiveMaximum", "maximum");

        Keep(Numbers, IsNumber);
        Keep(Counts, IsCount);
        Keep(["multipleOf"], n => IsNumber(n) && n!.GetValue<double>() > 0);
        Keep(Texts, IsString);
        Keep(Anchors, n => IsString(n) && Anchor().IsMatch(n!.GetValue<string>()));
        Keep(["$id"], n => IsString(n) && !n!.GetValue<string>().TrimEnd('#').Contains('#'));
        schema.Remove("$vocabulary");
        Keep(Flags, IsFlag);
        Keep(Lists, n => n is JsonArray);
        ToNames(schema, "required");
        ToTypes(schema);

        foreach (var key in OneSchema)
            if (schema.TryGetPropertyValue(key, out var value) && !IsSchema(value))
                schema.Remove(key);
        foreach (var key in SchemaLists)
            if (schema.TryGetPropertyValue(key, out var value))
            {
                if (value is JsonArray list)
                    foreach (var item in list.ToList())
                        if (!IsSchema(item))
                            list.Remove(item);
                if (value is not JsonArray { Count: > 0 })
                    schema.Remove(key);
            }
        foreach (var key in SchemaMaps)
            if (schema.TryGetPropertyValue(key, out var value))
            {
                if (value is JsonObject map)
                    foreach (var (name, item) in map.ToList())
                        if (!IsSchema(item))
                            map.Remove(name);
                if (value is not JsonObject)
                    schema.Remove(key);
            }
        if (schema.TryGetPropertyValue("dependentRequired", out var needed))
        {
            if (needed is JsonObject lists)
                foreach (var (name, _) in lists.ToList())
                    ToNames(lists, name);
            else
                schema.Remove("dependentRequired");
        }
        if (schema.TryGetPropertyValue("dependencies", out var dependencies))
        {
            if (dependencies is JsonObject map)
                foreach (var (name, item) in map.ToList())
                    if (item is JsonArray)
                        ToNames(map, name);
                    else if (!IsSchema(item))
                        map.Remove(name);
            if (dependencies is not JsonObject)
                schema.Remove("dependencies");
        }

        // Left out and empty mean the same, and OpenAI refuses an array without items.
        if (Declares(schema, "array") && !schema.ContainsKey("items"))
            schema["items"] = new JsonObject();

        void Keep(IEnumerable<string> keys, Func<JsonNode?, bool> fits)
        {
            foreach (var key in keys)
                if (schema.TryGetPropertyValue(key, out var value) && !fits(value))
                    schema.Remove(key);
        }
    }

    /// <summary>A schema is an object, true or false; an object's own keywords are put right on the way.</summary>
    private static bool IsSchema(JsonNode? node)
    {
        if (node is JsonObject schema)
            ToDraft2020(schema);
        return node is JsonObject || IsFlag(node);
    }

    private static bool IsFlag(JsonNode? node) => node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static bool IsNumber(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number;

    private static bool IsString(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String;

    private static bool IsCount(JsonNode? node)
    {
        if (!IsNumber(node))
            return false;
        var count = node!.GetValue<double>();
        return count >= 0 && count == Math.Floor(count);
    }

    private static bool Declares(JsonObject schema, string type) =>
        Strings(schema["type"] as JsonArray ?? [schema["type"]?.DeepClone()]).Contains(type);

    /// <summary>
    /// Before 2020-12 a list under items gave each place of a tuple its schema, and zod still writes
    /// that. 2020-12's own prefixItems isn't taken everywhere (OpenAI asks for items), so items now
    /// says what any place may hold; the lengths stay as they are.
    /// </summary>
    private static void TupleToItems(JsonObject schema, JsonArray tuple)
    {
        var kinds = new List<JsonNode>();
        foreach (var kind in tuple.Append(schema["additionalItems"]).OfType<JsonObject>())
            if (!kinds.Any(k => JsonNode.DeepEquals(k, kind)))
                kinds.Add(kind.DeepClone());
        schema["items"] = kinds.Count == 1 ? kinds[0] : kinds.Count == 0 ? new JsonObject() : new JsonObject { ["anyOf"] = new JsonArray([.. kinds]) };
    }

    /// <summary>Draft 4's exclusiveMinimum is a flag on minimum; since then it is the number itself.</summary>
    private static void NumericBound(JsonObject schema, string exclusive, string bound)
    {
        if (!IsFlag(schema[exclusive]))
            return;
        var strict = schema[exclusive]!.GetValueKind() == JsonValueKind.True;
        schema.Remove(exclusive);
        if (!strict || !IsNumber(schema[bound]))
            return;
        var number = schema[bound]!.DeepClone();
        schema.Remove(bound);
        schema[exclusive] = number;
    }

    /// <summary>A list of different strings, or nothing at all (draft 3's required was a flag on the property).</summary>
    private static void ToNames(JsonObject holder, string key)
    {
        if (!holder.ContainsKey(key))
            return;
        if (holder[key] is JsonArray names)
            holder[key] = new JsonArray([.. Strings(names).Distinct().Select(n => JsonValue.Create(n))]);
        else
            holder.Remove(key);
    }

    private static void ToTypes(JsonObject schema)
    {
        if (!schema.ContainsKey("type"))
            return;
        var type = schema["type"];
        var known = (type is JsonArray types ? Strings(types) : IsString(type) ? [type!.GetValue<string>()] : []).Where(TypeNames.Contains).Distinct().ToList();
        if (known.Count == 0)
            schema.Remove("type");
        else if (type is JsonArray)
            schema["type"] = new JsonArray([.. known.Select(n => JsonValue.Create(n))]);
    }

    private static IEnumerable<string> Strings(IEnumerable<JsonNode?> nodes) =>
        nodes.Where(IsString).Select(n => n!.GetValue<string>());

    [GeneratedRegex(@"\A[A-Za-z_][-A-Za-z0-9._]*\z")]
    private static partial Regex Anchor();
}
