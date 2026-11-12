using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Sloperator.Core.Tests;

// Failed tries with no look-up in between point the model to web_search (it didn't search on its own).
public class GuessworkTests
{
    private static readonly ShellTool Shell = new();
    private static readonly ReadFileTool Read = new();

    // As run_shell returned it in the Excel run, four times with only the script line changing.
    private static ToolResult ChartError(int line) => ToolResult.Error($"""
        Exit code 1
        Method invocation failed because [System.__ComObject] does not contain a method named 'Add'.
        At C:\Repos\Sloperator\.dev\blindspots\work\make_budget.ps1:{line} char:1
        + $chart = $ws.ChartObjects.Add(300, 10, 450, 250)
        + ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            + CategoryInfo          : InvalidOperation: (:) [], RuntimeException
            + FullyQualifiedErrorId : MethodNotFound
        """);

    // What mcp-for-blender answers when a script fails in Blender: an ordinary result, not an MCP error.
    private const string BlenderError = """
        Error executing code: AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'

        Traceback (most recent call last):
          File "C:\Repos\Sloperator\.dev\autopick\Data\tools\blender_mcp.py", line 2248, in execute_code
            exec(code, namespace)
          File "<string>", line 4, in <module>
        AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'
        """;

    [Fact]
    public void An_error_naming_what_isnt_there_gets_a_note_at_once_and_coming_back_another()
    {
        var guesswork = new Guesswork();

        Assert.Equal(Guesswork.NameNote, guesswork.Check(Shell, ChartError(41)));
        Assert.Equal(Guesswork.RepeatNote, guesswork.Check(Shell, ChartError(44)));
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Shell, ChartError(47)));
    }

    [Fact]
    public void A_program_that_isnt_a_command_here_is_said_to_be_missing_not_misnamed()
    {
        // Haiku 5.5 on a PC with no Python (2026-11-09) was told names change between versions.
        var guesswork = new Guesswork();
        var note = guesswork.Check(Shell, ToolResult.Error("Exit code 1\npython : The term 'python' is not recognized as the name of a cmdlet, function, script file, or operable program."));
        Assert.Equal(Guesswork.ProgramNote("python"), note);
        Assert.StartsWith("[Sloperator] python isn't a command here: it isn't installed, or isn't on the PATH.", note);
        // A cmdlet's misspelt or newer name is still a name to look up.
        Assert.Equal(Guesswork.NameNote, new Guesswork().Check(Shell, ToolResult.Error("Exit code 1\nGet-Thing : The term 'Get-Thing' is not recognized as the name of a cmdlet, function, script file, or operable program.")));
    }

    [Fact]
    public void Other_errors_get_a_note_from_the_third_failure_since_the_last_look_up()
    {
        var guesswork = new Guesswork();

        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nAccess to the path 'C:\\Windows\\x.txt' is denied.")));
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nall good"))); // what went fine never counts
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 1\nThe disk is full: the write failed.")));
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Shell, ToolResult.Error("Exit code 2\nThe process cannot access the file because another process uses it.")));
        Assert.Equal(Guesswork.TriesNote(4), guesswork.Check(Shell, ToolResult.Error("Exit code 3\nThe operation timed out at the third step.")));

        // A look-up starts the count again; an error from before still counts as one seen.
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. Some page\n   https://example.com/")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 4\nAnother thing failed.")));
        Assert.Equal(Guesswork.RepeatNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nThe disk is full: the write failed.")));
    }

    [Fact]
    public void Failures_long_ago_and_declined_calls_dont_count()
    {
        var guesswork = new Guesswork();
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nFirst failure of the task.")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 1\nSecond failure of the task.")));
        for (var i = 0; i < 20; i++)
            Assert.Null(guesswork.Check(Read, new ToolResult("fine")));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error(AgentSession.DeclinedText)));
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Exit code 1\nThird failure, long after the others.")));
    }

    [Fact]
    public void The_first_failed_script_of_a_task_gets_a_note_unless_something_was_looked_up()
    {
        // Once in a task, and only for what runs code: a file that isn't there is no matter for a search.
        var guesswork = new Guesswork();
        Assert.Null(guesswork.Check(Read, ToolResult.Error("File not found: C:\\x.txt")));
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
        Assert.Null(guesswork.Check(new ShellTool(), new ToolResult("Exit code 0\nfine")));

        var searched = new Guesswork();
        Assert.Null(searched.Check(new FetchPageTool(), new ToolResult("# Docs")));
        Assert.Null(searched.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
    }

    [Fact]
    public void A_tool_that_runs_code_has_its_errors_read_from_its_output()
    {
        var blender = new ToolResult(BlenderError);
        Assert.Equal("AttributeError: 'GeometryNodeTree' object has no attribute 'inputs'", Guesswork.ErrorLine(blender, errorsInText: true));
        Assert.True(Guesswork.NamesWhatIsNotThere(Guesswork.ErrorLine(blender, errorsInText: true)!));
        // A file that says "Error" is no failed call.
        Assert.Null(Guesswork.ErrorLine(blender));

        // Blender run with a script exits with 0 and prints the script's traceback among its own lines.
        var headless = new ToolResult("""
            Exit code 0
            Blender 5.2.2 (hash 8c3b8b2bb6e4 built 2026-09-29 01:38:00)
            Traceback (most recent call last):
              File "C:\Users\user\Sloperator\Data\scratch\scatter.py", line 7, in <module>
                tree.interface.new_socket("Size", in_out="INPUT", socket_type="NodeSocketFloatDistance")
            TypeError: NodeTreeInterface.new_socket(): error with keyword argument "socket_type" -  enum "NodeSocketFloatDistance" not found in ('NodeSocketBool', 'NodeSocketFloat')

            Blender quit
            """);
        var line = Guesswork.ErrorLine(headless, errorsInText: true)!;
        Assert.StartsWith("TypeError: NodeTreeInterface.new_socket()", line);
        Assert.True(Guesswork.NamesWhatIsNotThere(line));

        // How the server answered in a run: the add-on's error came back inside JSON, on one line.
        var json = new ToolResult("""
            Error executing code: Communication error with Blender: {"exception_type": "TypeError", "message": "bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float", "traceback": "Traceback (most recent call last):\n  File \"C:\\Repos\\Sloperator\\.dev\\autopick\\Data\\tools\\blender_mcp.py\", line 2248, in execute_code\n    exec(code, namespace)\n    ~~~~^^^^^^^^^^^^^^^^^\n  File \"<string>\", line 54, in <module>\nTypeError: bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float\n"}
            """);
        Assert.Equal("TypeError: bpy_struct: item.attr = val: NodeSocketInt.default_value expected an int type, not float", Guesswork.ErrorLine(json, errorsInText: true));

        // Every Python error starts with the same line; they are told apart by the one under the stack.
        var other = ToolResult.Error("Exit code 1\nTraceback (most recent call last):\n  File \"x.py\", line 1, in <module>\nZeroDivisionError: division by zero");
        Assert.NotEqual(Guesswork.Signature(headless, errorsInText: true), Guesswork.Signature(other));
    }

    [Theory]
    [InlineData("KeyError: 'bpy_prop_collection[key]: key \"Geometry\" not found'", true)]
    [InlineData("RuntimeError: Error: Node type GeometryNodeScatterPoints undefined", true)]
    [InlineData("TypeError: Mesh.from_pydata() got an unexpected keyword argument 'faces_list'", true)]
    [InlineData("error CS0117: 'KeyCode' does not contain a definition for 'Left'", true)]
    [InlineData("The term 'python' is not recognized as the name of a cmdlet, function, script file, or operable program.", true)]
    [InlineData("A parameter cannot be found that matches parameter name 'Recurse'.", true)]
    [InlineData("ffmpeg: Unrecognized option 'crf-max'.", true)]
    [InlineData("File not found: C:\\x.txt", false)]
    [InlineData("Access to the path 'C:\\Windows\\x.txt' is denied.", false)]
    [InlineData("ZeroDivisionError: division by zero", false)]
    [InlineData("RuntimeError: Operator bpy.ops.object.mode_set.poll() failed, context is incorrect", false)]
    [InlineData("AttributeError: 'NoneType' object has no attribute 'modifiers'", false)]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'length')", false)]
    public void Errors_naming_what_isnt_there_are_told_apart(string line, bool missing) =>
        Assert.Equal(missing, Guesswork.NamesWhatIsNotThere(line));

    [Fact]
    public async Task In_a_run_the_note_comes_only_when_the_model_has_web_search()
    {
        foreach (var web in new[] { true, false })
        {
            var execute = new McpTool("blender", "blender__execute_blender_code", new ModelContextProtocol.Protocol.Tool { Name = "execute_blender_code" },
                (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = BlenderError }] }));
            var server = new ScriptedServer(
                Sse.ToolCall("r1", "blender__execute_blender_code", """{"code":"tree.inputs.clear()"}"""),
                Sse.Text("It isn't there."));
            var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
            var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
                ToolRegistry.CreateDefault(includeScreenTools: false, includeWebTools: web, extra: [execute]), ctx, "sys");

            var observer = new RecordingObserver();
            await session.RunAsync("read it", observer, CancellationToken.None);

            // Said after the result, in a message of its own.
            var r1 = session.History.FindIndex(m => m.ToolCallId == "r1");
            Assert.Equal(web, session.History[r1 + 1].Role == ChatRole.User && session.History[r1 + 1].Content!.Contains("with web_search"));
            Assert.DoesNotContain(session.History, m => m.Role == ChatRole.Tool && m.Content!.Contains("web_search"));
            // The chat shows a line where the note was sent.
            Assert.Equal(web, observer.Notices.Contains("The error says a name doesn't exist here; asked the model to look up the right one for the version in use."));
        }
    }

    [Fact]
    public async Task A_script_an_MCP_server_reports_as_failed_gets_the_note()
    {
        var execute = new McpTool("blender", "blender__execute_blender_code", new ModelContextProtocol.Protocol.Tool { Name = "execute_blender_code" },
            (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = BlenderError }] }));
        var server = new ScriptedServer(
            Sse.ToolCall("b1", "blender__execute_blender_code", """{"code":"tree.inputs.new('NodeSocketGeometry', 'Geometry')"}"""),
            Sse.Text("Done."));
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false, extra: [execute]), ctx, "sys");

        await session.RunAsync("Build a geometry nodes graph in Blender", new RecordingObserver(), CancellationToken.None);

        var result = session.History.FindIndex(m => m.Role == ChatRole.Tool);
        Assert.Equal(Guesswork.NameNote, session.History[result + 1].Content);
    }

    private static readonly WriteFileTool Write = new();
    private static readonly ToolResult Wrote = new("Wrote C:\\Users\\user\\Sloperator\\Data\\scratch\\motion.json");
    private static ChatMessage Reply(string thinking, string text = "") => new() { Role = ChatRole.Assistant, Reasoning = thinking, Content = text };

    [Theory]
    // What a model asks itself about how something is done, what it is called or whether it is there.
    [InlineData("Hmm, how do I make LTX 2.5 follow the motion of a reference video?", "Hmm, how do I make LTX 2.5 follow the motion of a reference video?")]
    [InlineData("Which node does the pose conditioning in the LTX workflow?", "Which node does the pose conditioning in the LTX workflow?")]
    [InlineData("Is there a node for depth control in LTX 2.5?", "Is there a node for depth control in LTX 2.5?")]
    [InlineData("But maybe they're built into newer ComfyUI?", "But maybe they're built into newer ComfyUI?")]
    [InlineData("What's the correct way to add a socket to a node group in Blender 5.2?", "What's the correct way to add a socket to a node group in Blender 5.2?")]
    [InlineData("I'm not sure how the IC-LoRA takes the control video.", "I'm not sure how the IC-LoRA takes the control video.")]
    [InlineData("The template runs. I don't know which sampler Nova Furry XL wants, so I'll keep euler.", "I don't know which sampler Nova Furry XL wants, so I'll keep euler.")]
    // From qwen3.8's thinking in earlier runs: the part of the sentence that asks.
    [InlineData("In ComfyUI, VAEDecode uses the VAE's dtype; is there a node input for fp32?", "is there a node input for fp32?")]
    [InlineData("Actually, does ComfyUI have a \"Remove Background\" node?", "Actually, does ComfyUI have a \"Remove Background\" node?")]
    [InlineData("I don't know if the original s3 was good or not.", null)]
    [InlineData("I need to check the node names in Blender 5.2. Let me do a quick web search for Blender 5.2's geometry nodes Python API. Actually, let me think about what I know.",
        "Let me do a quick web search for Blender 5.2's geometry nodes Python API.")]
    [InlineData("Let me search for the templates that make video first.", null)]
    [InlineData("Let me search the web for this specific problem.", null)]
    [InlineData("Let me search the web for this specific problem: \"Blender 5.2 modifier input not updating\".",
        "Let me search the web for this specific problem: \"Blender 5.2 modifier input not updating\".")]
    // A check it makes on this PC (inspect.signature on the installed moviepy), not on the web.
    [InlineData("Also, let's check the signature of crossfadein/crossfadeout in moviepy 2.x. Let me verify.", null)]
    [InlineData("Let me check the output folder for the new file.", null)]
    [InlineData("So how do we determine whether it's Juggernaut XL or SnowPony?", null)]
    [InlineData("Now, how does the video tool handle lip-sync?", null)]
    [InlineData("Hmm, write_videofile: does it have an audio parameter?", null)]
    // qwen3.8 in Excel, then answering itself wrongly: "No, the default is None, and Excel displays it."
    [InlineData("Also, maybe I should set delete=False on the x-axis (openpyxl's default axis delete is… in some versions, the default for the axis's `delete` attribute is True?",
        "Also, maybe I should set delete=False on the x-axis (openpyxl's default axis delete is… in some versions, the default for the axis's `delete` attribute is True?")]
    [InlineData("What does openpyxl's TextAxis.delete default to?", "What does openpyxl's TextAxis.delete default to?")]
    [InlineData("Does LoadAudio support flac files?", "Does LoadAudio support flac files?")]
    // Questions the web can't answer: the screen, the user, how a result looks, or nothing named.
    [InlineData("Did the click on the Export button work?", null)]
    [InlineData("How do I get to the button in the top right of the screen?", null)]
    [InlineData("What does the user want here, a portrait or a full body?", null)]
    [InlineData("How does the second render look?", null)]
    [InlineData("How should I proceed with this task now?", null)]
    [InlineData("Maybe I should crop just one view?", null)]
    [InlineData("I'm not sure which crop looks best.", null)]
    [InlineData("Let me make the frame first, then the shot.", null)]
    [InlineData("Wait, but how does the default value work now?", null)]
    public void Questions_about_how_things_are_done_are_told_apart(string thinking, string? question) =>
        Assert.Equal(question, Guesswork.QuestionIn(thinking));

    [Fact]
    public void A_question_the_model_goes_on_without_looking_up_gets_a_note_once_until_it_looks_something_up()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        guesswork.Read(Reply("How do I make LTX follow the motion of a reference video? I'll try the depth guide first."), [Write]);
        Assert.Equal(Guesswork.QuestionNote, guesswork.Check(Write, Wrote));
        guesswork.Read(Reply("Which node does the pose conditioning in the LTX workflow?"), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));

        // It looks something up and reads what it found: a question then gets no note for a few calls.
        guesswork.Read(Reply("Let me search for LTX motion control."), [new WebSearchTool()]);
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. Motion Control | LTX Documentation")));
        guesswork.Read(Reply("Is there a node for depth control in LTX 2.5?"), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));
        for (var i = 0; i < Guesswork.SearchGap; i++)
            Assert.Null(guesswork.Check(Read, new ToolResult("text")));
        guesswork.Read(Reply("Does LoadAudio support flac files?"), [Write]);
        Assert.Equal(Guesswork.QuestionNote, guesswork.Check(Write, Wrote));
    }

    [Theory]
    [InlineData(@"With Blender 5.2 (C:\Program Files\Blender Foundation\Blender 5.2\blender.exe), make scatter.blend", "Blender 5.2")]
    [InlineData("Make the bunny dance with LTX-2.5 like in dance.mp4", "LTX-2.5")]
    [InlineData("Use the latest moviepy 2.x for a title", "moviepy 2.x")]
    [InlineData("Make 3 cubes and a 6-second video", null)]
    public void The_program_a_request_names_is_found(string request, string? subject) =>
        Assert.Equal(subject, Guesswork.SubjectIn(request));

    [Fact]
    public void Going_on_past_an_error_it_was_told_to_look_up_makes_the_next_failure_say_so()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));

        // It went on with another script: one that works needs no note, the next failure gets one.
        guesswork.Read(Reply("Let me try it another way."), [Shell]);
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0")));
        Assert.Equal(Guesswork.IgnoredNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));

        // One that looks it up itself is left to it.
        var searching = new Guesswork();
        searching.StartRun("Make scatter.blend with Blender 5.2");
        Assert.Equal(Guesswork.FirstNote, searching.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
        searching.Read(Reply("Let me search for it."), [new WebSearchTool()]);
        Assert.Null(searching.Check(new WebSearchTool(), new ToolResult("1. int() - Python documentation")));
        searching.Read(Reply("Now the script."), [Shell]);
        Assert.Null(searching.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));

        // A command line's own slip is no matter for a search, and uses up no note.
        var slip = new Guesswork();
        Assert.Null(slip.Check(Shell, ToolResult.Error("Exit code 1\nUnexpected token 'version' in expression or statement.")));
        slip.Read(Reply("Let me quote it."), [Shell]);
        Assert.Equal(Guesswork.FirstNote, slip.Check(Shell, ToolResult.Error("Exit code 1\nZeroDivisionError: division by zero")));
    }

    [Fact]
    public void Output_after_an_exit_code_is_no_error_unless_it_says_so()
    {
        // What a command printed, then "Exit code 1" from its last statement: the same first line of two
        // C# files read through the shell was "an error that came up before".
        Assert.Null(Guesswork.ErrorLine(ToolResult.Error("Exit code 1\nusing System;\nusing System.Net.Http;\nnamespace Game.Backend\n")));
        Assert.Null(Guesswork.ErrorLine(ToolResult.Error("Exit code 1\n    LogExtension.Error($\"Popup not found\");\n    /// Fails with error 5 in a job object.\n")));
        Assert.Null(Guesswork.ErrorLine(ToolResult.Error("Exit code 1\n"))); // git grep finding nothing, a check with -ErrorAction SilentlyContinue
        Assert.Equal("fatal: not a git repository (or any of the parent directories): .git",
            Guesswork.ErrorLine(ToolResult.Error("Exit code 128\nfatal: not a git repository (or any of the parent directories): .git\n")));
        Assert.Equal("Program.cs(3,1): error CS0103: The name 'x' does not exist in the current context",
            Guesswork.ErrorLine(ToolResult.Error("Exit code 1\n  Determining projects to restore...\nProgram.cs(3,1): error CS0103: The name 'x' does not exist in the current context\n")));

        // A record's own line, after what the command printed, in any language.
        const string Record = "At line:1 char:1\n+ Get-Content x\n+ ~~~~~~~~~~~~~\n    + CategoryInfo          : ObjectNotFound: (:) [], ItemNotFoundException\n    + FullyQualifiedErrorId : PathNotFound\n";
        Assert.Equal("Get-Content : Cannot find path 'C:\\x' because it does not exist.",
            Guesswork.ErrorLine(ToolResult.Error("Exit code 1\nusing System;\nGet-Content : Cannot find path 'C:\\x' because it does not exist.\n" + Record)));
        Assert.Equal("Set-Location : Не удается найти позиционный параметр, принимающий аргумент \"Games\".",
            Guesswork.ErrorLine(new ToolResult("Exit code 0\nSet-Location : Не удается найти позиционный параметр, принимающий аргумент \"Games\".\n" + Record)));

        // A record with no cmdlet before it, under output that speaks of failures of its own: a test run's summary.
        const string Thrown = "Exception calling \"ReadAllText\" with \"1\" argument(s): \"Could not find file 'C:\\Users\\user\\sample.txt'.\"";
        var tests = new ToolResult("Exit code 0\nPASS  Top 1\nFAIL  MinLength 6 gives nothing (got 1, expected 0)\n1 failed\n" + Thrown +
            "\nAt C:\\proj\\stats.ps1:4 char:1\n+ $text = [IO.File]::ReadAllText($Path).ToLowerInvariant()\n+ ~~~~~~~~\n    + CategoryInfo          : NotSpecified: (:) [], MethodInvocationException\n    + FullyQualifiedErrorId : FileNotFoundException\n");
        Assert.Equal(Thrown, Guesswork.ErrorLine(tests));
        Assert.False(Guesswork.Answerable(Shell, Guesswork.ErrorLine(tests)!));

        // A long path is wrapped to a line of its own.
        var wrapped = new ToolResult("Exit code 0\n1 failed\nException calling \"ReadAllText\" with \"1\" argument(s): \"Could not find a part of the path \n'C:\\Repos\\app\\tasks\\chat.json'.\"" +
            "\nAt C:\\proj\\timeline.ps1:3 char:1\n+ $j = [IO.File]::ReadAllText($Chat) | ConvertFrom-Json\n+ ~~~~~~~~\n    + CategoryInfo          : NotSpecified: (:) [], MethodInvocationException\n    + FullyQualifiedErrorId : DirectoryNotFoundException\n");
        Assert.False(Guesswork.Answerable(Shell, Guesswork.ErrorLine(wrapped)!));
    }

    [Theory]
    [InlineData("ValueError: invalid literal for int() with base 10: 'a'", true)]
    [InlineData("error CS0117: 'KeyCode' does not contain a definition for 'Left'", true)]
    [InlineData("Method invocation failed because [System.__ComObject] does not contain a method named 'Add'.", true)]
    [InlineData("unity : The term 'unity' is not recognized as the name of a cmdlet, function, script file, or operable program.", true)]
    [InlineData("Timed out after 60s.", false)]
    [InlineData("Get-Content : Cannot find path 'C:\\x' because it does not exist.", false)]
    [InlineData("test-stack.ps1 : A positional parameter cannot be found that accepts argument '{{.Status}}'.", false)]
    [InlineData("c=sqlite3.connect : The term 'c=sqlite3.connect' is not recognized as the name of a cmdlet, function, script file, or operable program.", false)]
    [InlineData("Unexpected token 'version' in expression or statement.", false)]
    [InlineData("Exception calling \"ReadAllText\" with \"1\" argument(s): \"Could not find file 'C:\\Users\\user\\sample.txt'.\"", false)]
    [InlineData("FileNotFoundError: [Errno 2] No such file or directory: 'data/input.csv'", false)]
    [InlineData("Error: ENOENT: no such file or directory, open 'C:\\site\\index.html'", false)]
    [InlineData("FileNotFoundError: [Errno 2] No such file or directory: 'ffmpeg'", true)]
    public void Only_failures_a_page_could_answer_count(string line, bool answerable) =>
        Assert.Equal(answerable, Guesswork.Answerable(Shell, line));

    [Fact]
    public void Sloperators_own_tools_failing_is_no_matter_for_a_search()
    {
        var guesswork = new Guesswork();
        for (var i = 0; i < 2 * Guesswork.Tries; i++)
        {
            Assert.Null(guesswork.Check(Read, ToolResult.Error("File not found: C:\\x.txt")));
            Assert.Null(guesswork.Check(Write, ToolResult.Error("Missing required argument 'content'. Parameters for write_file: {}")));
        }
        // Nor does running out of time say anything, and it uses up no failed try.
        Assert.Null(guesswork.Check(Shell, ToolResult.Error("Timed out after 60s.\nE:\\Repos\\Game\\Backend")));
        Assert.Equal(Guesswork.FirstNote, guesswork.Check(Shell, ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'")));
    }

    [Fact]
    public void A_reply_that_looks_something_up_gets_no_note_for_what_failed_beside_it()
    {
        var guesswork = new Guesswork();
        var failed = ToolResult.Error("Exit code 1\nValueError: invalid literal for int() with base 10: 'a'");
        guesswork.Read(Reply("Let me check the page while this runs."), [Shell, new WebSearchTool()]);
        Assert.Null(guesswork.Check(Shell, failed));
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. int() - Python documentation")));

        // The next reply is told as before.
        guesswork.Read(Reply("Try again."), [Shell]);
        Assert.Equal(Guesswork.RepeatNote, guesswork.Check(Shell, failed));
    }

    [Theory]
    // From the chats of 2026-11-03 and 04.
    [InlineData("cd E:\\Repos\\Game; git status --short | Select-Object -First 20; git log --oneline -3", true)]
    [InlineData("cd E:\\Repos\\Game\\Assets\\Source; Get-Content Scripts\\Backend\\CloudSave.cs | select -first 22; Select-String -Path Tests\\*.cs -Pattern \"Base|Url\"", true)]
    [InlineData("$l=\"$env:LOCALAPPDATA\\Unity\\Editor\\Editor.log\"; (Get-Item $l).LastWriteTime; Select-String -Path $l -Pattern \"Reloading\" | Select-Object -Last 3", true)]
    [InlineData("foreach($f in 'A.cs','B.cs'){ Select-String -Path $f -Pattern 'class ' | select -first 1 }", true)]
    [InlineData("Get-Process Unity -ErrorAction SilentlyContinue | measure | % Count", true)]
    [InlineData("sleep 1; Select-String -Path \"$env:LOCALAPPDATA\\Unity\\Editor\\Editor.log\" -Pattern \"Paywall\" 2>&1", true)]
    [InlineData("cat /etc/nginx/nginx.conf | grep server_name; journalctl -u nginx -n 20", true)]
    [InlineData("cd Chat; [System.IO.File]::ReadAllLines(\"$PWD\\ChatTexts.cs\",[System.Text.Encoding]::UTF8)[6..12]", true)]
    [InlineData("git status | select -first 25; git branch --show-current; git log --oneline -5; git stash list", true)]
    [InlineData("git stash pop", false)]
    [InlineData("git add -A; git commit -m \"Chat\"", false)]
    [InlineData("cd E:\\Repos\\Game; powershell -NoProfile -File Backend\\test-stack.ps1 ensure", false)]
    [InlineData("python scatter.py", false)]
    [InlineData("blender -b --python scatter.py", false)]
    [InlineData("Get-Content a.txt | Set-Content b.txt", false)]
    [InlineData("Get-Content a.txt > b.txt", false)]
    [InlineData("Get-ChildItem *.meta | ForEach-Object { Remove-Item $_ }", false)]
    [InlineData("$p = python scatter.py; $p", false)]
    [InlineData("[IO.File]::WriteAllText('a.txt', 'x')", false)]
    [InlineData("find . -name '*.tmp' -delete", false)]
    [InlineData("", false)]
    public void Commands_that_only_read_are_told_apart(string command, bool reads) =>
        Assert.Equal(reads, ReadOnlyCommand.Is(command));

    [Fact]
    public void Commands_that_only_read_make_it_no_time_to_look()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Add a chat button");
        var ok = new ToolResult("Exit code 0\nAssets/Source/Chat.cs:12:    private Button _send;");
        const string Grep = """{"command":"git grep -n Button -- *.cs"}""";
        for (var i = 0; i < 3 * Guesswork.CheckBurst; i++)
            Assert.Null(guesswork.Check(Shell, ok, Grep));
    }

    [Theory]
    // What Nemotron's Blender run failed with: the command line's own slips aren't ones to look up.
    [InlineData("Unexpected token 'version' in expression or statement.", false)]
    [InlineData("The filename, directory name, or volume label syntax is incorrect.", false)]
    [InlineData(@"ls : Cannot find path 'C:\Program' because it does not exist.", false)]
    [InlineData("Start-Process : A positional parameter cannot be found that accepts argument 'extension].enum_items])'.", false)]
    [InlineData("AttributeError: 'NodeTreeInterfaceSocketFloat' object has no attribute 'bl_idname'", true)]
    [InlineData("TypeError: 'bpy_prop_collection' object is not callable", true)]
    [InlineData("error CS0117: 'KeyCode' does not contain a definition for 'Left'", true)]
    [InlineData("Method invocation failed because [System.__ComObject] does not contain a method named 'Add'.", true)]
    public void Only_errors_a_search_answers_are_ones_to_look_up(string line, bool searchable) =>
        Assert.Equal(searchable, Guesswork.Searchable(line));

    [Fact]
    public void A_try_the_model_calls_wrong_counts_as_a_failed_one()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        Assert.Null(guesswork.Check(Write, Wrote));

        // Nothing failed; the video just didn't do what was asked. Its note comes with the next result.
        guesswork.Read(Reply("The video still doesn't follow the dance. Let me raise the guide strength."), [Write]);
        Assert.Equal(Guesswork.DoubtNote, guesswork.Check(Write, Wrote));
        // Said once; then, as for errors, from the third failure since the last look-up.
        guesswork.Read(Reply("No change in the motion at all."), [Write]);
        Assert.Null(guesswork.Check(Write, Wrote));
        guesswork.Read(Reply("That didn't work either; I need a different approach."), [Write]);
        Assert.Equal(Guesswork.TriesNote(3), guesswork.Check(Write, Wrote));

        // Words of doubt with no try before them, or in a reply that looks something up, don't count.
        var fresh = new Guesswork();
        fresh.StartRun("Make the bunny dance");
        fresh.Read(Reply("If this still doesn't work, I'll look it up."), [Write]);
        Assert.Null(fresh.Check(Write, Wrote));
        fresh.Read(Reply("It still isn't moving."), [new WebSearchTool()]);
        Assert.Null(fresh.Check(new WebSearchTool(), new ToolResult("1. LTX motion control")));
        fresh.Read(Reply("It still isn't moving."), [Write]);
        Assert.Null(fresh.Check(Write, Wrote)); // looked up in this task: one more failure is no matter yet
    }

    [Fact]
    public void The_user_saying_the_result_isnt_right_counts_as_a_failed_try()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make a picture of Anton from his refsheet");
        Assert.Null(guesswork.Check(Write, Wrote));

        guesswork.StartRun("Dude it's still a kid, not an adult");
        Assert.Equal(Guesswork.UserDoubtNote, guesswork.Check(new ReadFileTool(), new ToolResult("refsheet.png")));

        // What a video does wrong, in the model's words after a look or a measurement.
        Assert.True(Guesswork.SaysItFailed("The measurement says the ball moves right to left, the opposite of what was asked."));
        Assert.True(Guesswork.SaysItFailed("The bunny barely moves in the whole clip."));
        Assert.True(Guesswork.SaysItFailed("The ball rolls the wrong way."));
        Assert.False(Guesswork.SaysItFailed("The ball rolls from left to right, as asked, and the camera stays still."));

        // Saying what it should have done is saying it isn't right.
        Assert.True(Guesswork.UserDoubts("The text fades in from black; it should fade from transparent, with the blue showing through."));
        Assert.False(Guesswork.UserDoubts("Great, now make a version 10 seconds long."));
        // A change asked for with "instead of" (2026-11-13), and a result said to be wrong with it.
        Assert.False(Guesswork.UserDoubts("Make the table slide's numbers a bar chart instead of a table."));
        Assert.False(Guesswork.UserDoubts("Thanks. Now use euros instead of dollars, and put the total in bold."));
        Assert.True(Guesswork.UserDoubts("The logo moves left instead of right."));
        Assert.True(Guesswork.UserDoubts("Make it longer. It fades out instead of in."));

        // Without a try before it, a message is a new task, whatever it says.
        var fresh = new Guesswork();
        fresh.StartRun("My printer doesn't work, fix it");
        Assert.Null(fresh.Check(Shell, new ToolResult("Exit code 0\nPrinter: HP LaserJet")));
    }

    [Fact]
    public void Runs_that_worked_never_say_it_is_time_to_look()
    {
        // Qwen 3.8's tests had all passed when it was told it was time to look things up (the user's pick, 2026-11-09).
        var now = new DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc);
        var guesswork = new Guesswork(() => now);
        guesswork.StartRun("Make the bunny dance like in dance.mp4");
        var ran = new ToolResult("Exit code 0\nqueued");
        for (var i = 0; i < 3 * Guesswork.CheckBurst; i++)
        {
            now += TimeSpan.FromMinutes(4);
            Assert.Null(guesswork.Check(Shell, ran));
        }
        guesswork.Heard("Go on");
        for (var i = 0; i < Guesswork.CheckBurst; i++)
            Assert.Null(guesswork.Check(Shell, ran));
    }

    [Fact]
    public void A_try_the_model_calls_wrong_after_runs_with_nothing_looked_up_says_it_is_time_to_look()
    {
        var now = new DateTime(2026, 10, 21, 8, 34, 0, DateTimeKind.Utc);
        var guesswork = new Guesswork(() => now);
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        var probed = new ToolResult("Exit code 0\nprobing");
        for (var i = 0; i < Guesswork.CheckBurst; i++)
        {
            now += TimeSpan.FromSeconds(20);
            Assert.Null(guesswork.Check(Shell, probed));
            // Edits between the runs are the work, not tries to count.
            Assert.Null(guesswork.Check(new EditFileTool(), new ToolResult("Edited scatter.py")));
        }
        // The first try it calls wrong, before anything was looked up, says so; the next, with runs enough since, that it is time to look.
        guesswork.Read(Reply("The cubes are still missing."), [Shell]);
        Assert.Equal(Guesswork.DoubtNote, guesswork.Check(Shell, probed));
        guesswork.Read(Reply("The scatter still doesn't work."), [Shell]);
        Assert.StartsWith("[Sloperator] Time to look things up: 5 tries", guesswork.Check(Shell, probed));
        Assert.StartsWith("[Sloperator] Time to look things up: 4 tries and 1 minute since", Guesswork.CheckNote(TimeSpan.FromSeconds(80), 4));
        // A look-up starts the count again.
        Assert.Null(guesswork.Check(new WebSearchTool(), new ToolResult("1. Geometry nodes")));
        guesswork.Heard("And now the camera");
        Assert.Null(guesswork.Check(Shell, probed));
        guesswork.Read(Reply("The camera still doesn't move."), [Shell]);
        Assert.Null(guesswork.Check(Shell, probed));
    }

    [Fact]
    public void Looking_things_up_for_long_with_no_try_says_to_ask_the_installed_version()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Make scatter.blend with Blender 5.2");
        var page = new ToolResult("# nodeitems_builtins.py");
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        Assert.Equal(Guesswork.LongSearchNote(8), guesswork.Check(new FetchPageTool(), page));
        for (var i = 9; i < 14; i++)
            Assert.Null(guesswork.Check(new WebSearchTool(), page));
        Assert.Equal(Guesswork.LongSearchNote(14), guesswork.Check(new FetchPageTool(), page));

        // A try starts the count again.
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\n['GeometryNodeGroup', 'NodeGroupInput', 'NodeGroupOutput']")));
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        // So does asking the installed version, as the note says to, and the user's next message.
        Assert.Null(guesswork.Check(Shell, new ToolResult("Exit code 0\nHelp on module bpy"), """{"command": "blender -b --python-expr \"help(bpy.types)\""}"""));
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
        guesswork.StartRun("Now add a camera to it");
        for (var i = 1; i < Guesswork.LongSearch; i++)
            Assert.Null(guesswork.Check(new FetchPageTool(), page));
    }

    [Fact]
    public void A_request_that_is_all_research_gets_no_note_to_ask_the_installed_version()
    {
        var guesswork = new Guesswork();
        guesswork.StartRun("Compare these three laptops and tell me which is best for video editing");
        for (var i = 0; i < 20; i++)
            Assert.Null(guesswork.Check(i % 2 == 0 ? new WebSearchTool() : new FetchPageTool(), new ToolResult("A review")));
    }

    [Theory]
    // The search it means to make, not what came before its colon (which named the screen).
    [InlineData("The modifier value on screen did not change: let me search the web for Blender 5.2 geometry nodes modifier input python.",
                "let me search the web for Blender 5.2 geometry nodes modifier input python")]
    public void A_search_the_model_means_to_make_is_found_after_what_came_before_it(string thinking, string expected) =>
        Assert.Equal(expected, Guesswork.QuestionIn(thinking)?.TrimEnd('.'));

    [Theory]
    [InlineData("run", true)]
    [InlineData("templates", false)]
    [InlineData("nodes", false)]
    [InlineData("status", false)]
    public void Only_a_comfyui_job_is_a_try(string action, bool isTry) =>
        Assert.Equal(isTry, Guesswork.IsTry(new ComfyTool(), Guesswork.ActionOf($$"""{"action": "{{action}}"}""")));

    [Fact]
    public async Task In_a_run_a_question_the_model_goes_on_with_gets_a_note_and_nothing_is_searched_for_it()
    {
        var server = new ScriptedServer(
            Sse.Thought("How do I make LTX follow the motion of a reference video? I'll try the depth guide first.",
                Sse.ToolCall("w1", "write_file", """{"path":"motion.json","content":"{}"}""")),
            Sse.Thought("Hmm, which node does the pose conditioning in the LTX workflow?",
                Sse.ToolCall("w2", "write_file", """{"path":"motion.json","content":"{}"}""")),
            Sse.Text("Done."));
        var dir = Directory.CreateTempSubdirectory().FullName;
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = dir };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false, includeWebTools: true), ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("Make the bunny dance like in dance.mp4", observer, CancellationToken.None);

        // The note says to search; it neither quotes the model's thinking nor brings search results.
        // It follows the first result in a message of its own: inside a tool result, Claude took
        // such a note for an injection.
        var w1 = session.History.FindIndex(m => m.ToolCallId == "w1");
        Assert.Equal(ChatRole.User, session.History[w1 + 1].Role);
        Assert.Equal(Guesswork.QuestionNote, session.History[w1 + 1].Content);
        Assert.Single(session.History, m => m.Content == Guesswork.QuestionNote);
        Assert.DoesNotContain(session.History, m => m.Role == ChatRole.Tool && m.Content!.Contains("[Sloperator]"));
        Assert.DoesNotContain(observer.Notices, n => n.Contains("Looked up"));
    }

    [Fact]
    public void The_chat_says_what_each_note_asked_for()
    {
        // One line stood for every note: after a folder listing a note on the model's own doubt went out with, and for the
        // note that says to stop searching and try.
        const string old = "Asked the model to look this up with web_search before its next try.";
        string[] notes = [Guesswork.QuestionNote, Guesswork.IgnoredNote, Guesswork.RepeatNote, Guesswork.NameNote, Guesswork.FirstNote, Guesswork.DoubtNote,
            Guesswork.UserDoubtNote, Guesswork.LongSearchNote(8), Guesswork.ProgramNote("python"), Guesswork.TriesNote(3), Guesswork.CheckNote(TimeSpan.FromMinutes(12), 4)];
        var lines = notes.Select(Guesswork.NoticeFor).ToList();
        Assert.DoesNotContain(old, lines);
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.Equal("Many look-ups in a row; told the model to try it on the installed version now.", Guesswork.NoticeFor(Guesswork.LongSearchNote(8)));
    }
}
