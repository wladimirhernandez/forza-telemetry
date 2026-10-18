using System.IO;
using System.Xml.Linq;

namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Guards the declarative tab shortcuts in <c>MainWindow.xaml</c>. A WPF window cannot be created without an STA thread and an
/// <c>Application</c>, so this reads the XAML itself: it is the one place the shortcuts are declared, and the contract is
/// simple (the digit row and the numeric keypad are different keys to WPF, so each shortcut needs both).
/// </summary>
public class MainWindowShortcutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument LoadMainWindowXaml()
    {
        // Walk up from the test binaries to the repository root, where the app's source lives.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "ForzaTelemetry.App", "MainWindow.xaml")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "ForzaTelemetry.App", "MainWindow.xaml"));
    }

    private sealed record Binding(string Key, string Modifiers, string Command, string Parameter);

    private static List<Binding> ReadAllKeyBindings() =>
        LoadMainWindowXaml().Descendants(Presentation + "KeyBinding")
            .Select(b => new Binding(
                (string?)b.Attribute("Key") ?? "",
                (string?)b.Attribute("Modifiers") ?? "",
                (string?)b.Attribute("Command") ?? "",
                // CommandParameter looks like {x:Static local:PageId.Laps}; keep the member name.
                ((string?)b.Attribute("CommandParameter") ?? "").Split('.').Last().TrimEnd('}')))
            .ToList();

    /// <summary>
    /// The complete list of shortcuts MainWindow declares. Adding, removing or retargeting one must be a deliberate edit of this list, so a
    /// stray or duplicated binding cannot slip in unnoticed.
    /// </summary>
    [Fact]
    public void The_window_declares_exactly_the_expected_shortcuts()
    {
        var pages = Enum.GetNames<PageId>();
        var expected = new List<Binding>();
        for (var i = 0; i < pages.Length; i++)
        {
            // The digit row and the numeric keypad are different keys to WPF, so each tab shortcut is declared twice.
            expected.Add(new Binding("D" + (i + 1), "Control", "{Binding NavigateCommand}", pages[i]));
            expected.Add(new Binding("NumPad" + (i + 1), "Control", "{Binding NavigateCommand}", pages[i]));
        }

        expected.Add(new Binding("M", "Control", "{Binding Mini.ToggleCommand}", ""));

        var actual = ReadAllKeyBindings();

        Assert.Equal(expected.OrderBy(b => b.Key), actual.OrderBy(b => b.Key));
    }

    [Fact]
    public void No_two_shortcuts_share_the_same_key_and_modifiers()
    {
        var duplicates = ReadAllKeyBindings()
            .GroupBy(b => (b.Key, b.Modifiers))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Ctrl_1_to_8_work_on_the_digit_row_and_on_the_numeric_keypad_for_the_same_tabs()
    {
        var bindings = ReadAllKeyBindings();
        var pages = Enum.GetNames<PageId>();

        for (var i = 0; i < pages.Length; i++)
        {
            Assert.Contains(bindings, b => b is { Modifiers: "Control", Parameter: var target } && b.Key == "D" + (i + 1) && target == pages[i]);
            Assert.Contains(bindings, b => b is { Modifiers: "Control", Parameter: var target } && b.Key == "NumPad" + (i + 1) && target == pages[i]);
        }
    }

    [Fact]
    public void Ctrl_M_opens_mini_mode_through_the_shell_command()
    {
        var binding = Assert.Single(ReadAllKeyBindings(), b => b.Key == "M");

        Assert.Equal("Control", binding.Modifiers);
        Assert.Equal("{Binding Mini.ToggleCommand}", binding.Command);
    }
}
