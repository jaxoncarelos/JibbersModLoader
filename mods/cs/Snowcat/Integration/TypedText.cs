using Jibbers.Sdk;

namespace Snowcat.Integration;

/// <summary>
/// Typing into a little box of our own (the mod settings have no text fields): letters, digits and a
/// few symbols read straight from the keyboard each frame, Backspace, Enter to finish, Escape to cancel.
/// </summary>
internal sealed class TypedText
{
    const int Shift = 0x10, Back = 0x08, Enter = 0x0D, Escape = 0x1B, Space = 0x20;
    readonly bool[] was = new bool[256];
    public string Text { get; private set; } = "";
    public int MaxLength { get; init; } = 64;

    /// <summary>Starts empty, ignoring keys already held (such as the one that opened the box).</summary>
    public void Start()
    {
        Text = "";
        for (var vk = 1; vk < 256; vk++) was[vk] = Input.IsDown(vk);
    }

    /// <summary>One frame of typing: true when Enter finished it, false on Escape, null while typing.</summary>
    public bool? Frame()
    {
        var shift = Input.IsDown(Shift);
        bool? result = null;
        for (var vk = 1; vk < 256; vk++)
        {
            var down = Input.IsDown(vk);
            var pressed = down && !was[vk];
            was[vk] = down;
            if (!pressed) continue;
            if (vk == Enter) result = true;
            else if (vk == Escape) result = false;
            else if (vk == Back) { if (Text.Length > 0) Text = Text[..^1]; }
            else if (Char(vk, shift) is { } c && Text.Length < MaxLength) Text += c;
        }
        return result;
    }

    static char? Char(int vk, bool shift) => vk switch
    {
        >= 0x41 and <= 0x5A => (char)(shift ? vk : vk + 32),
        >= 0x30 and <= 0x39 => shift ? ")!@#$%^&*("[vk - 0x30] : (char)vk,
        >= 0x60 and <= 0x69 => (char)('0' + vk - 0x60), // number pad
        Space => ' ',
        0xBD => shift ? '_' : '-',
        0xBB => shift ? '+' : '=',
        0xBE => shift ? '>' : '.',
        0xBC => shift ? '<' : ',',
        0xBF => shift ? '?' : '/',
        0xBA => shift ? ':' : ';',
        _ => null,
    };
}
