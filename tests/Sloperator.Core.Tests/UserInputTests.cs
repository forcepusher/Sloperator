#if WINDOWS
using System.Runtime.InteropServices;
using Sloperator.Platform.Windows;
using Xunit;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in (set BB_DESKTOP_TESTS=1, uses the real mouse and keyboard): the lock on the user's mouse
/// and keyboard keeps out input that isn't Sloperator's, sent here untagged as a user's comes, lets
/// the agent's mouse through, and Pause ends it. The agent's clicks under the lock: <see cref="ClickLandsTests"/>.
/// </summary>
[Collection(RealScreen.Name)]
public class UserInputTests
{
    private const ushort VK_SHIFT = 0x10;

    [Fact]
    public void The_lock_keeps_the_users_mouse_and_keys_out_and_lets_the_agents_mouse_through()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_DESKTOP_TESTS") is null, "Set BB_DESKTOP_TESTS=1 to run (uses the real mouse and keyboard).");
        var platform = new WindowsPlatform();
        var user = platform.UserInput;
        var input = platform.Input;
        var cursorBefore = input.GetCursorPosition();
        var screen = platform.Screen.GetScreens()[0];
        var (x, y) = (screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        try
        {
            // As in the app, the stop key is there first, so the user's input is looked at before it:
            // it swallows the Pause press that ends the lock, as a press that stops a run.
            using var stopKey = platform.StopKey.Watch(() => true);
            using var watch = user.Watch();
            input.MoveMouse(x, y);
            Thread.Sleep(1200);
            // The agent's own mouse moves aren't the user's.
            input.MoveMouse(x + 20, y);
            Assert.True(user.SinceLastInput > TimeSpan.FromSeconds(1), $"the agent's move counted as the user's ({user.SinceLastInput})");

            // Unlocked, the user's mouse moves and a held key is seen held.
            UserMove(30);
            Thread.Sleep(50);
            Assert.NotEqual((x + 20, y), input.GetCursorPosition());
            Assert.True(user.SinceLastInput < TimeSpan.FromSeconds(1));
            var presses = user.Presses;
            UserKey(VK_SHIFT, up: false);
            Thread.Sleep(50);
            Assert.True(user.Holding);
            UserKey(VK_SHIFT, up: true);
            Thread.Sleep(300);
            Assert.False(user.Holding);
            Assert.Equal(presses + 1, user.Presses);

            using (user.Lock())
            {
                input.MoveMouse(x, y);
                Assert.Equal((x, y), input.GetCursorPosition());
                UserMove(30);
                Thread.Sleep(50);
                Assert.Equal((x, y), input.GetCursorPosition()); // the user's move went nowhere
                UserKey(VK_SHIFT, up: false);
                Thread.Sleep(50);
                Assert.False((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0, "the user's key got through the lock");
                UserKey(VK_SHIFT, up: true);
                Assert.Equal(presses + 2, user.Presses); // counted all the same

                // Pause/Break gives the user it all back.
                UserKey((ushort)VK_PAUSE, up: false);
                UserKey((ushort)VK_PAUSE, up: true);
                UserMove(30);
                Thread.Sleep(50);
                Assert.NotEqual((x, y), input.GetCursorPosition());
            }

            // An action that only presses keys keeps the user's keys out, but their mouse stays free.
            using (user.Lock(mouse: false))
            {
                input.MoveMouse(x, y);
                UserMove(30);
                Thread.Sleep(50);
                Assert.NotEqual((x, y), input.GetCursorPosition());
                UserKey(VK_SHIFT, up: false);
                Thread.Sleep(50);
                Assert.False((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0, "the user's key got through the lock");
                UserKey(VK_SHIFT, up: true);
            }
        }
        finally
        {
            input.MoveMouse(cursorBefore.X, cursorBefore.Y);
        }
    }

    /// <summary>A relative mouse move, untagged as the user's are.</summary>
    private static void UserMove(int dx) => Send(new INPUT
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dwFlags = 0x0001 } }, // MOUSEEVENTF_MOVE
    });

    private static void UserKey(ushort vk, bool up) => Send(new INPUT
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    });

    private static void Send(INPUT input) => Assert.Equal(1u, SendInput(1, [input], Marshal.SizeOf<INPUT>()));
}
#endif
