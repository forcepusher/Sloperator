using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: on a real server, tool schemas as MCP servers write them, sent as they are and then as
/// <see cref="McpTool.NormalizeSchema"/> leaves them. BB_LIVE_URL picks the server, BB_LIVE_KEY its
/// key and BB_LIVE_MODEL the model. About thirty short requests; the refused ones cost nothing.
/// </summary>
public class McpSchemaLiveTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_LIVE_URL");
    private static readonly string Key = Environment.GetEnvironmentVariable("BB_LIVE_KEY") ?? "";
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_LIVE_MODEL") ?? "";

    /// <summary>spine-MCP's spine_control_bones as zod-to-json-schema writes it: the tuple's list under items, and $refs to the keyframe it reuses.</summary>
    private const string SpineBones = """
        {"type": "object", "additionalProperties": false, "$schema": "http://json-schema.org/draft-07/schema#",
         "required": ["sourceJsonPath", "outputJsonPath", "animationName", "boneControls"],
         "properties": {
           "sourceJsonPath": {"type": "string", "minLength": 1, "description": "Source Spine JSON file to read."},
           "outputJsonPath": {"type": "string", "minLength": 1, "description": "Destination JSON file."},
           "animationName": {"type": "string", "minLength": 1, "description": "Animation name to add or overwrite."},
           "boneControls": {"type": "array", "minItems": 1, "description": "Bone transform timelines to write.", "items": {
             "type": "object", "additionalProperties": false, "required": ["boneName"], "properties": {
               "boneName": {"type": "string", "minLength": 1},
               "rotate": {"type": "array", "minItems": 1, "items": {
                 "type": "object", "additionalProperties": false, "required": ["time"], "properties": {
                   "time": {"type": "number", "minimum": 0},
                   "angle": {"type": "number"},
                   "curve": {"description": "Omit for linear, \"stepped\" for stepped, or a 4-number Bezier curve.", "anyOf": [
                     {"type": "string", "const": "stepped"},
                     {"type": "array", "minItems": 4, "maxItems": 4, "items": [{"type": "number"}, {"type": "number"}, {"type": "number"}, {"type": "number"}]}]}}}},
               "translate": {"type": "array", "minItems": 1, "items": {"$ref": "#/properties/boneControls/items/properties/rotate/items"}},
               "scale": {"type": "array", "minItems": 1, "items": {"$ref": "#/properties/boneControls/items/properties/rotate/items"}}}}},
           "overwrite": {"type": "boolean", "default": false}}}
        """;

    /// <summary>What a server may hold in a property, older drafts and slips alike, and things only the server can say it takes.</summary>
    private static readonly (string Name, string Property)[] Cases =
    [
        ("tuple", """{"type": "array", "items": [{"type": "number"}, {"type": "number"}]}"""),
        ("mixed tuple", """{"type": "array", "items": [{"type": "string"}, {"type": "number"}], "additionalItems": {"type": "boolean"}}"""),
        ("$ref into a tuple", """{"type": "array", "items": [{"type": "object", "properties": {"a": {"type": "string"}}}, {"$ref": "#/properties/v/items/0"}]}"""),
        ("exclusiveMinimum true", """{"type": "number", "minimum": 0, "exclusiveMinimum": true}"""),
        ("required true", """{"type": "string", "required": true}"""),
        ("type any", """{"type": "any"}"""),
        ("description null", """{"type": "string", "description": null}"""),
        ("empty anyOf", """{"anyOf": []}"""),
        ("minLength -1", """{"type": "string", "minLength": -1}"""),
        ("additionalProperties text", """{"type": "object", "additionalProperties": "no"}"""),
        ("array without items", """{"type": "array"}"""),
        ("$ref to nowhere", """{"$ref": "#/nowhere"}"""),
        ("broken pattern", """{"type": "string", "pattern": "(["}"""),
        ("$id with a fragment", """{"type": "string", "$id": "a#b"}"""),
        ("$anchor with a space", """{"type": "string", "$anchor": "a b"}"""),
        ("$vocabulary", """{"type": "string", "$vocabulary": 1}"""),
        ("$schema inside", """{"type": "string", "$schema": "http://json-schema.org/draft-07/schema#"}"""),
    ];

    [Fact]
    public async Task Spines_bones_tool_is_refused_as_written_and_works_once_made_valid()
    {
        Assert.SkipWhen(Url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        var (http, recorder, server) = await ConnectAsync(ct);
        using var _ = http;

        // Its draft-07 $schema gets it through, and it is taken out before sending, as some servers
        // refuse the field, so the tuple was then read as 2020-12: the request as it went.
        var written = JsonNode.Parse(SpineBones)!.AsObject();
        var (accepted, _) = await SendAsync(http, recorder, server, new RawTool("spine__spine_control_bones", (JsonObject)written.DeepClone()), "Reply with the single word done.", ct);
        TestContext.Current.SendDiagnosticMessage($"with its $schema: {accepted.Status} {accepted.Error}");
        written.Remove("$schema");
        var (refused, _) = await SendAsync(http, recorder, server, new RawTool("spine__spine_control_bones", written), "Reply with the single word done.", ct);
        TestContext.Current.SendDiagnosticMessage($"as written: {refused.Status} {refused.Error}");
        Assert.Equal(400, refused.Status);
        Assert.Contains("2020-12", refused.Error);

        JsonElement? curve = null;
        var tool = new McpTool("spine", "spine__spine_control_bones", ToolOf(SpineBones), (arguments, _) =>
        {
            curve = arguments["boneControls"] is JsonElement bones ? bones[0].GetProperty("rotate")[0].GetProperty("curve") : null;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "written" }] });
        });
        var (taken, observer) = await SendAsync(http, recorder, server, tool,
            "Call spine__spine_control_bones once: source a.json, output b.json, animation wave, bone arm with one rotate key at time 0, angle 30, with the Bezier curve 0.25, 0, 0.75, 1. Then reply with the single word done.", ct);
        TestContext.Current.SendDiagnosticMessage($"made valid: {taken.Status} {taken.Error}, curve {curve}");
        Assert.Equal(200, taken.Status);
        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(JsonValueKind.Array, curve?.ValueKind);
        Assert.Equal(4, curve!.Value.GetArrayLength());
    }

    [Fact]
    public async Task What_servers_write_goes_through_once_made_valid()
    {
        Assert.SkipWhen(Url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        var (http, recorder, server) = await ConnectAsync(ct);
        using var _ = http;

        var failed = new List<string>();
        foreach (var (name, property) in Cases)
        {
            var schema = """{"type": "object", "properties": {"v": """ + property + "}}";
            var (raw, _) = await SendAsync(http, recorder, server, new RawTool("probe", JsonNode.Parse(schema)!.AsObject()), "Reply with the single word done.", ct);
            var tool = new McpTool("probe", "probe", ToolOf(schema), (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] }));
            var (made, _) = await SendAsync(http, recorder, server, tool, "Reply with the single word done.", ct);
            TestContext.Current.SendDiagnosticMessage($"{name}: as written {raw.Status}, made valid {made.Status}: {tool.Parameters.ToJsonString()} {raw.Error}{made.Error}");
            if (made.Status != 200)
                failed.Add($"{name}: {made.Error}");
        }
        Assert.Empty(failed);
    }

    private static ModelContextProtocol.Protocol.Tool ToolOf(string schema) =>
        new() { Name = "spine_control_bones", Description = "Write timelines for bones into a copy of a Spine JSON file.", InputSchema = JsonSerializer.Deserialize<JsonElement>(schema) };

    private static async Task<(HttpClient, Recorder, ServerSettings)> ConnectAsync(CancellationToken ct)
    {
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(recorder) { Timeout = TimeSpan.FromMinutes(5) };
        var server = new ServerSettings { BaseUrl = Url!, ApiKey = Key, Model = Model };
        var probe = await ServerProbe.CheckAsync(http, server, ct);
        Assert.True(probe.Reachable, probe.Message);
        server.BaseUrl = probe.BaseUrl!;
        server.Model = probe.Model!;
        server.EffortLevels = probe.Efforts ?? [];
        var facts = await ServerProbe.EnsureLoadedAsync(http, server, null, ct, known: probe.Facts);
        server.SetDetectedContext(facts.Context);
        return (http, recorder, server);
    }

    private static async Task<(Sent Last, RecordingObserver Observer)> SendAsync(HttpClient http, Recorder recorder, ServerSettings server, Tools.Tool tool, string task, CancellationToken ct)
    {
        recorder.Sent.Clear();
        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, new ToolRegistry([tool]), ctx,
            "You are a test assistant.", contextTokens: connection.ContextTokens);
        var observer = new RecordingObserver();
        await session.RunAsync(task, observer, ct);
        return (recorder.Sent[^1], observer);
    }

    private sealed class RawTool(string name, JsonObject parameters) : Tools.Tool
    {
        public override string Name => name;
        public override string Description => "A test tool.";
        public override ToolRisk Risk => ToolRisk.ReadOnly;
        public override JsonObject Parameters => parameters;
        public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) => Task.FromResult(new ToolResult("ok"));
    }

    private sealed record Sent(int Status, string Error);

    private sealed class Recorder : DelegatingHandler
    {
        public List<Sent> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject;
            var response = await base.SendAsync(request, ct);
            if (body?["messages"] is null)
                return response;
            var error = "";
            if (!response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(ct);
                error = await response.Content.ReadAsStringAsync(ct);
            }
            lock (Sent) Sent.Add(new Sent((int)response.StatusCode, error));
            return response;
        }
    }
}
