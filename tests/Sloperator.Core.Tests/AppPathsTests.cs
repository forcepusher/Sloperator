using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The SloperatorData folder next to the exe, and the Data folder of 1.3.0 and before moved into it.</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-paths-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string Old => Path.Combine(_root, "Data");

    private string New => Path.Combine(_root, "SloperatorData");

    [Fact]
    public void A_fresh_folder_gets_SloperatorData()
    {
        var paths = AppPaths.Open(_root);
        Assert.Equal(New, paths.DataDir);
        Assert.Equal(Path.Combine(New, "settings.json"), paths.SettingsFile);
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void An_earlier_versions_Data_folder_is_moved_with_its_chats_and_settings()
    {
        Directory.CreateDirectory(Path.Combine(Old, "chats"));
        File.WriteAllText(Path.Combine(Old, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Old, "chats", "a.json"), "chat");

        var paths = AppPaths.Open(_root);

        Assert.Equal(New, paths.DataDir);
        Assert.False(Directory.Exists(Old));
        Assert.Equal("{}", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("chat", File.ReadAllText(Path.Combine(paths.ChatsDir, "a.json")));
    }

    [Fact]
    public void Another_programs_Data_folder_is_left_alone()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "save.dat"), "x");

        Assert.Equal(New, AppPaths.Open(_root).DataDir);
        Assert.True(File.Exists(Path.Combine(Old, "save.dat")));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void A_SloperatorData_folder_already_there_wins()
    {
        Directory.CreateDirectory(Old);
        File.WriteAllText(Path.Combine(Old, "settings.json"), "old");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "settings.json"), "new");

        var paths = AppPaths.Open(_root);

        Assert.Equal("new", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Old, "settings.json")));
    }

    [Fact]
    public void While_an_earlier_version_holds_Data_the_start_keeps_to_it_and_a_later_one_moves_it()
    {
        Directory.CreateDirectory(Path.Combine(Old, "runtime"));
        var library = Path.Combine(Old, "runtime", "libSkiaSharp.dll");
        File.WriteAllText(library, "");

        using (new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(Old, AppPaths.Open(_root).DataDir);

        Assert.Equal(New, AppPaths.Open(_root).DataDir);
        Assert.True(File.Exists(Path.Combine(New, "runtime", "libSkiaSharp.dll")));
    }
}
