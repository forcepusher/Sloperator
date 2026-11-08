using System.Text.Json.Nodes;
using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>Auto in the Side panel dropdown: which panel it shows for each call the agent makes.</summary>
public sealed class AutoRuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-auto-panel-").FullName;
    private string Pictures => Path.Combine(_dir, "pictures");
    private string Videos => Path.Combine(_dir, "videos");
    private string Sounds => Path.Combine(_dir, "sounds");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonObject Args(string json) => AutoRule.Args(json);

    private AutoTarget? AtStart(string tool, string args = "{}") => AutoRule.AtStart(tool, Args(args), new AutoPlaces(Pictures, Videos, SoundsDir: Sounds));

    private AutoTarget? AtEnd(string tool, string args = "{}", string? written = null, IReadOnlyList<string>? made = null, string? lastPage = null, bool failed = false,
        bool isNew = false, string? editor = null) =>
        AutoRule.AtEnd(tool, Args(args), failed, written is null ? null : new AutoWritten(written, isNew), made, new AutoPlaces(Pictures, Videos, lastPage, editor, Sounds));

    [Fact]
    public void A_command_shows_the_terminal_as_it_starts()
    {
        Assert.Equal(new AutoTarget(SidePanelNames.Terminal), AtStart("run_shell", """{"command":"npm test"}"""));
        Assert.Equal(new AutoTarget(SidePanelNames.Terminal), AtStart("ssh", """{"host":"web1","command":"uptime"}"""));
        // Nothing more once it has ended: the terminal showed it as it went.
        Assert.Null(AtEnd("run_shell", """{"command":"npm test"}"""));
    }

    [Theory]
    [InlineData("ssh web1 uptime")]
    [InlineData("scp app.zip web1:/srv")]
    [InlineData("sudo systemctl restart nginx")]
    [InlineData("sudo -u postgres psql")]
    [InlineData("docker compose up -d")]
    [InlineData("Restart-Service W3SVC")]
    [InlineData("pip install requests")]
    [InlineData("winget install Git.Git")]
    [InlineData("dotnet build")]
    [InlineData("make -j8")]
    [InlineData("cd src; dotnet build")]
    [InlineData("cd src && npm run build")]
    [InlineData("$out = dotnet build 2>&1; $out | Select-Object -Last 5")]
    [InlineData("CC=clang make")]
    [InlineData("if (Test-Path app.sln) { dotnet test }")]
    [InlineData("Get-Content list.txt\r\nnpm test\r\n")]
    [InlineData("& \"C:\\Program Files\\dotnet\\dotnet.exe\" publish")]
    [InlineData(".\\gradlew assembleRelease")]
    [InlineData(".\\build.ps1")]
    [InlineData("./deploy.sh --prod")]
    [InlineData("powershell -NoProfile -ExecutionPolicy Bypass -File C:\\tools\\setup.ps1")]
    [InlineData("powershell -Command \"dotnet build\"")]
    [InlineData("bash -c 'cd app && make'")]
    [InlineData("cmd /c \"npm test\"")]
    [InlineData("Start-Process dotnet -ArgumentList run")]
    [InlineData("python train.py")]
    [InlineData("node server.js")]
    [InlineData("git clone https://example.com/a.git")]
    [InlineData("git -C repo pull")]
    public void Server_build_and_dev_work_opens_the_terminal(string command) =>
        Assert.Equal(new AutoTarget(SidePanelNames.Terminal), AtStart("run_shell", new JsonObject { ["command"] = command }.ToJsonString()));

    [Theory]
    [InlineData("cd src")]
    [InlineData("Set-Location C:\\Repos")]
    [InlineData("ls -la")]
    [InlineData("dir /b")]
    [InlineData("Get-ChildItem -Recurse -Filter *.cs")]
    [InlineData("cat README.md")]
    [InlineData("type Makefile")]
    [InlineData("Get-Content build.ps1 -TotalCount 20")]
    [InlineData("Select-String -Path *.cs -Pattern dotnet")]
    [InlineData("grep -rn docker src | head")]
    [InlineData("cd src; ls; cat Program.cs")]
    [InlineData("Test-Path C:\\tools\\ssh")]
    [InlineData("Get-Process | Where-Object { $_.Name -eq 'node' }")]
    [InlineData("echo \"dotnet build; npm test\"")]
    [InlineData("Write-Output 'run make'")]
    [InlineData("# dotnet build")]
    [InlineData("Start-Process notepad")]
    [InlineData("powershell -Command \"Get-Content a.txt\"")]
    [InlineData("sudo cat /etc/nginx/nginx.conf")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git diff HEAD~1")]
    [InlineData("dotnet --version")]
    [InlineData("node -v 2>&1")]
    [InlineData("ssh -V")]
    [InlineData("pip list")]
    [InlineData("npm view react version")]
    [InlineData("python -c \"print(2 + 2)\"")]
    [InlineData("where.exe python")]
    [InlineData("")]
    public void Looking_around_leaves_the_panel_as_it_is(string command) =>
        Assert.Null(AtStart("run_shell", new JsonObject { ["command"] = command }.ToJsonString()));

    [Fact]
    public void A_command_on_a_server_shows_the_terminal_whatever_it_is() =>
        Assert.Equal(new AutoTarget(SidePanelNames.Terminal), AtStart("ssh", """{"host":"web1","command":"ls"}"""));

    [Fact]
    public void A_file_changed_shows_in_the_panel_for_its_kind()
    {
        var game = Path.Combine(_dir, "game");
        var page = Path.Combine(game, "index.html");
        Assert.Equal(new AutoTarget(SidePanelNames.WebPreview, page), AtEnd("write_file", written: page));
        // A file the page loads, beside it: the page reloads, which is where to see it.
        Assert.Equal(new AutoTarget(SidePanelNames.WebPreview, page), AtEnd("edit_file", written: Path.Combine(game, "js", "game.js"), lastPage: page));
        Assert.Equal(new AutoTarget(SidePanelNames.WebPreview, page), AtEnd("write_file", written: Path.Combine(game, "style.css"), lastPage: page));
        // Code elsewhere, or not a file a page loads: its diff.
        var script = Path.Combine(_dir, "tools", "build.js");
        Assert.Equal(new AutoTarget(SidePanelNames.FileChanges, script), AtEnd("write_file", written: script, lastPage: page));
        Assert.Equal(new AutoTarget(SidePanelNames.FileChanges, Path.Combine(game, "notes.md")), AtEnd("write_file", written: Path.Combine(game, "notes.md"), lastPage: page));
        Assert.Equal(SidePanelNames.FileChanges, AtEnd("edit_file", written: Path.Combine(_dir, "Program.cs"))!.Panel);
        Assert.Equal(SidePanelNames.FileChanges, AtEnd("write_file", written: Path.Combine(_dir, "data.csv"))!.Panel);
        // What only looks right drawn: a picture, a document.
        Assert.Equal(new AutoTarget(SidePanelNames.FileInspector, Path.Combine(_dir, "mockup.svg")), AtEnd("write_file", written: Path.Combine(_dir, "mockup.svg")));
        Assert.Equal(SidePanelNames.FileInspector, AtEnd("write_file", written: Path.Combine(_dir, "report.docx"))!.Panel);
        Assert.Equal(SidePanelNames.FileInspector, AtEnd("write_file", written: Path.Combine(_dir, "budget.xlsx"))!.Panel);
        // Shown once written, not as the call starts; a write that failed, or with no file of its own in the list, shows nothing.
        Assert.Null(AtStart("write_file", """{"path":"index.html"}"""));
        Assert.Null(AtEnd("write_file", written: page, failed: true));
        Assert.Null(AtEnd("write_file", """{"path":"index.html","host":"web1"}"""));
    }

    [Fact]
    public void Code_the_agent_wrote_new_shows_in_the_code_editor_and_changes_to_files_that_were_there_in_file_changes()
    {
        var project = Path.Combine(_dir, "space-rocks");
        var game = Path.Combine(project, "src", "game.py");
        // A file the agent made in this chat, in the folder the Code editor shows: there, whole, lit as it is written.
        Assert.Equal(new AutoTarget(SidePanelNames.CodeEditor, game), AtEnd("write_file", written: game, isNew: true, editor: project));
        Assert.Equal(new AutoTarget(SidePanelNames.CodeEditor, game), AtEnd("edit_file", written: game, isNew: true, editor: project + Path.DirectorySeparatorChar));
        Assert.Equal(SidePanelNames.CodeEditor, AtEnd("write_file", written: Path.Combine(project, "README.md"), isNew: true, editor: project)!.Panel);
        // One that was there before (the user's): what was taken out beside what was put in.
        Assert.Equal(new AutoTarget(SidePanelNames.FileChanges, game), AtEnd("edit_file", written: game, isNew: false, editor: project));
        Assert.Equal(new AutoTarget(SidePanelNames.FileChanges, game), AtEnd("write_file", written: game, isNew: false, editor: project));
        // New, but outside the folder the Code editor shows, or with no folder yet: File changes, which shows every file.
        var elsewhere = Path.Combine(_dir, "tools", "make_levels.py");
        Assert.Equal(new AutoTarget(SidePanelNames.FileChanges, elsewhere), AtEnd("write_file", written: elsewhere, isNew: true, editor: project));
        Assert.Equal(SidePanelNames.FileChanges, AtEnd("write_file", written: game, isNew: true)!.Panel);
        Assert.Equal(SidePanelNames.FileChanges, AtEnd("write_file", written: Path.Combine(_dir, "space-rocks-old", "a.py"), isNew: true, editor: project)!.Panel);
        // New pages, pictures and documents still go where they are drawn.
        Assert.Equal(SidePanelNames.WebPreview, AtEnd("write_file", written: Path.Combine(project, "index.html"), isNew: true, editor: project)!.Panel);
        Assert.Equal(SidePanelNames.FileInspector, AtEnd("write_file", written: Path.Combine(project, "logo.svg"), isNew: true, editor: project)!.Panel);
        Assert.Equal(SidePanelNames.FileInspector, AtEnd("write_file", written: Path.Combine(project, "plan.docx"), isNew: true, editor: project)!.Panel);
    }

    [Fact]
    public void A_picture_made_shows_in_the_image_editor_or_the_inspector()
    {
        var lighthouse = Path.Combine(Pictures, "Lighthouse");
        Directory.CreateDirectory(lighthouse);
        var run = $$"""{"action":"run","name":"sloperator/text_to_image","save_to":{{JsonValue.Create(lighthouse).ToJsonString()}}}""";
        // A version of an Image editor picture: the editor, as the run starts.
        Assert.Equal(new AutoTarget(SidePanelNames.ImageEditor, lighthouse), AtStart("comfyui", run));
        Assert.Equal(new AutoTarget(SidePanelNames.ImageEditor, lighthouse), AtEnd("comfyui", run, made: [Path.Combine(lighthouse, "Sloperator_00002_.png")]));
        // Anything else it made: the first of it in the inspector, once made.
        var output = Path.Combine(_dir, "ComfyUI", "output", "Sloperator_00007_.png");
        Assert.Null(AtStart("comfyui", """{"action":"run","name":"sloperator/text_to_image"}"""));
        Assert.Equal(new AutoTarget(SidePanelNames.FileInspector, output), AtEnd("comfyui", """{"action":"run"}""", made: [output, output + ".mp4"]));
        // A folder in the pictures' folder's folders is no picture of the editor's.
        Assert.Null(AtStart("comfyui", $$"""{"action":"run","save_to":{{JsonValue.Create(Path.Combine(lighthouse, "old")).ToJsonString()}}}"""));
        // Looking things up shows nothing.
        Assert.Null(AtStart("comfyui", """{"action":"templates","search":"video"}"""));
        Assert.Null(AtEnd("comfyui", """{"action":"models"}"""));
    }

    [Fact]
    public void A_sound_made_or_edited_shows_in_the_audio_editor_or_the_inspector()
    {
        var rain = Path.Combine(Sounds, "Rain");
        Directory.CreateDirectory(rain);
        var run = $$"""{"action":"run","name":"audio_stable_audio_example","save_to":{{JsonValue.Create(rain).ToJsonString()}}}""";
        // A version of an Audio editor sound: the editor, as the run starts and once it has ended.
        Assert.Equal(new AutoTarget(SidePanelNames.AudioEditor, rain), AtStart("comfyui", run));
        Assert.Equal(new AutoTarget(SidePanelNames.AudioEditor, rain), AtEnd("comfyui", run, made: [Path.Combine(rain, "ComfyUI_00001_.mp3")]));
        // An edit of one is its next version there; an edit of any other sound, in the inspector.
        Assert.Equal(new AutoTarget(SidePanelNames.AudioEditor, rain),
            AtEnd("edit_sound", """{"edit":"cut","from":1,"to":2}""", made: [Path.Combine(rain, "Cut 1.00-2.00 s.wav")]));
        var elsewhere = Path.Combine(_dir, "Music", "song (+3 dB).wav");
        Assert.Equal(new AutoTarget(SidePanelNames.FileInspector, elsewhere), AtEnd("edit_sound", """{"edit":"volume","db":3}""", made: [elsewhere]));
        // A failed edit shows nothing.
        Assert.Null(AtEnd("edit_sound", """{"edit":"cut"}""", failed: true));
    }

    [Fact]
    public void A_video_call_shows_its_video_in_the_video_editor()
    {
        var boat = Video("Paper Boat");
        Thread.Sleep(20);
        var fox = Video("Fox");
        Assert.Equal(new AutoTarget(SidePanelNames.VideoEditor, boat), AtStart("video", """{"action":"shot","path":"Paper Boat","id":"s1"}"""));
        Assert.Equal(new AutoTarget(SidePanelNames.VideoEditor, boat), AtEnd("video", $$"""{"action":"check","path":{{JsonValue.Create(boat).ToJsonString()}}}"""));
        // No path, or a new video's not made yet: the video changed last, which the tool goes on with.
        Assert.Equal(new AutoTarget(SidePanelNames.VideoEditor, fox), AtStart("video", """{"action":"frame","id":"f1"}"""));
        Assert.Equal(new AutoTarget(SidePanelNames.VideoEditor, fox), AtStart("video", """{"action":"storyboard","path":"Tea Ad"}"""));
    }

    [Fact]
    public void Calls_with_no_panel_keep_the_panel_as_it_is()
    {
        foreach (var tool in new[] { "screenshot", "mouse_click", "type_text", "read_file", "list_directory", "web_search", "fetch_page", "find_text", "wait", "wait_until_change", "blender/run" })
        {
            Assert.Null(AtStart(tool));
            Assert.Null(AtEnd(tool, made: [Path.Combine(_dir, "shot.png")]));
        }
        // Arguments that don't read: as none.
        Assert.Empty(AutoRule.Args("{not json"));
        Assert.Empty(AutoRule.Args("[1,2]"));
    }

    [Fact]
    public void Auto_names_only_panels_of_the_dropdown()
    {
        var names = new[] { "File changes", "File inspector", "Code editor", "Image editor", "Audio editor", "Video editor", "Web preview", "Terminal" };
        Assert.All(AutoRule.Panels, p => Assert.Contains(p, names));
    }

    private string Video(string name)
    {
        var folder = Path.Combine(Videos, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".json"), """{"kind":"video"}""");
        return folder;
    }
}
