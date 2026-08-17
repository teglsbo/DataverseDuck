using System.Text;
using PrettyPrompt.Consoles;

namespace DataverseDuck.Tests;

/// <summary>
/// A headless <see cref="IConsole"/> for driving PrettyPrompt end to end in
/// tests: no real terminal, no tmux, no hand-typing. Keystrokes are queued up
/// front and replayed on <see cref="ReadKey"/>; everything PrettyPrompt draws
/// is captured rather than sent anywhere, so a test can assert on it without
/// a screen.
///
/// This is deliberately not the recording/replay console PrettyPrompt itself
/// uses for its own tests (that one still opens a real terminal to capture
/// key presses, and replays through one too) -- there is no terminal here to
/// open, on a CI runner or otherwise.
/// </summary>
internal sealed class ScriptedConsole : IConsole
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    private readonly StringBuilder _rendered = new();

    public int CursorTop { get; set; }
    public int BufferWidth { get; set; } = 120;
    public int WindowHeight { get; set; } = 40;
    public int WindowTop { get; set; }
    /// <summary>
    /// Always false, even with keys still queued. PrettyPrompt batches
    /// keystrokes it finds already waiting into a single "paste" event
    /// (<c>KeyPress.ReadForever</c>) -- appropriate for a real terminal
    /// buffering ahead of a slow reader, wrong here: a script is a stand-in
    /// for someone typing one key at a time, and Enter or Tab arriving fused
    /// into a paste block would never be seen as the key it is.
    /// </summary>
    public bool KeyAvailable => false;
    public bool IsErrorRedirected => false;
    public bool CaptureControlC { get; set; }

#pragma warning disable CS0067 // never raised: nothing here sends a real SIGINT.
    public event ConsoleCancelEventHandler? CancelKeyPress;
#pragma warning restore CS0067

    /// <summary>Everything PrettyPrompt rendered, concatenated in order.</summary>
    public string Rendered => _rendered.ToString();

    /// <summary>Queues a run of ordinary character keys, e.g. text being typed.</summary>
    public ScriptedConsole Type(string text)
    {
        foreach (var c in text)
            _keys.Enqueue(new ConsoleKeyInfo(c, CharToKey(c), false, false, false));

        return this;
    }

    public ScriptedConsole Key(ConsoleKey key, bool shift = false, bool alt = false, bool control = false)
    {
        _keys.Enqueue(new ConsoleKeyInfo('\0', key, shift, alt, control));
        return this;
    }

    public ScriptedConsole Enter() => Key(ConsoleKey.Enter);

    public ScriptedConsole Tab() => Key(ConsoleKey.Tab);

    public ScriptedConsole UpArrow() => Key(ConsoleKey.UpArrow);

    public ScriptedConsole DownArrow() => Key(ConsoleKey.DownArrow);

    /// <summary>
    /// Dismisses an open completion popup. Needed before Enter whenever the
    /// last character typed was a letter -- otherwise Enter accepts
    /// whatever is highlighted in the popup instead of submitting the line,
    /// same as it would for a person typing into a real terminal.
    /// </summary>
    public ScriptedConsole Escape() => Key(ConsoleKey.Escape);

    /// <summary>
    /// Ctrl-C is delivered to PrettyPrompt as a key it recognises during
    /// input, not as an OS signal -- there is no process to signal here.
    /// </summary>
    public ScriptedConsole ControlC() => Key(ConsoleKey.C, control: true);

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        if (_keys.Count == 0)
        {
            // The script is exhausted and PrettyPrompt is still asking for
            // more -- treat it the way a closed pipe would: end the read as
            // if the user hit Ctrl-D / EOF, not as a hang.
            throw new ScriptExhaustedException();
        }

        return _keys.Dequeue();
    }

    public void Write(string? value) => _rendered.Append(value);

    public void WriteLine(string? value) => _rendered.Append(value).Append('\n');

    public void WriteError(string? value) => _rendered.Append(value);

    public void WriteErrorLine(string? value) => _rendered.Append(value).Append('\n');

    public void Write(ReadOnlySpan<char> value) => _rendered.Append(value);

    public void WriteLine(ReadOnlySpan<char> value) => _rendered.Append(value).Append('\n');

    public void WriteError(ReadOnlySpan<char> value) => _rendered.Append(value);

    public void WriteErrorLine(ReadOnlySpan<char> value) => _rendered.Append(value).Append('\n');

    public void Clear() => _rendered.Clear();

    public void ShowCursor()
    {
    }

    public void HideCursor()
    {
    }

    public void InitVirtualTerminalProcessing()
    {
    }

    private static ConsoleKey CharToKey(char c) => c switch
    {
        ' ' => ConsoleKey.Spacebar,
        ';' => ConsoleKey.Oem1,
        >= 'a' and <= 'z' => Enum.Parse<ConsoleKey>(char.ToUpperInvariant(c).ToString()),
        >= 'A' and <= 'Z' => Enum.Parse<ConsoleKey>(c.ToString()),
        >= '0' and <= '9' => Enum.Parse<ConsoleKey>("D" + c),
        _ => ConsoleKey.Oem1, // Punctuation PrettyPrompt only cares about via KeyChar.
    };
}

/// <summary>
/// Signals that a test's scripted keystrokes ran out while PrettyPrompt was
/// still reading -- the equivalent of Ctrl-D on a real terminal.
/// </summary>
internal sealed class ScriptExhaustedException : Exception;
