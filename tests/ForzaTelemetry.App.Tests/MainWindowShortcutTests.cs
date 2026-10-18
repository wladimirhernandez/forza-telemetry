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

    private static List<(string Key, string Modifiers, string Target)> ReadKeyBindings()
    {
        // Walk up from the test binaries to the repository root, where the app's source lives.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "ForzaTelemetry.App", "MainWindow.xaml")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var xaml = XDocument.Load(Path.Combine(directory.FullName, "src", "ForzaTelemetry.App", "MainWindow.xaml"));

        return xaml.Descendants(Presentation + "KeyBinding")
            .Select(b => (
                Key: (string?)b.Attribute("Key") ?? "",
                Modifiers: (string?)b.Attribute("Modifiers") ?? "",
                // CommandParameter looks like {x:Static local:PageId.Laps}; keep the member name.
                Target: ((string?)b.Attribute("CommandParameter") ?? "").Split('.').Last().TrimEnd('}')))
            .ToList();
    }

    [Fact]
    public void Ctrl_1_to_8_work_on_the_digit_row_and_on_the_numeric_keypad_for_the_same_tabs()
    {
        var bindings = ReadKeyBindings();
        var pages = Enum.GetNames<PageId>();

        for (var i = 0; i < pages.Length; i++)
        {
            Assert.Contains(("D" + (i + 1), "Control", pages[i]), bindings);
            Assert.Contains(("NumPad" + (i + 1), "Control", pages[i]), bindings);
        }

        Assert.Equal(pages.Length * 2, bindings.Count); // nothing else hides in there
    }
}
