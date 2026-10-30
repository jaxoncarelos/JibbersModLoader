namespace Slvsh;

/// <summary>
/// The radial menu for calling a set: a line of tricks, each built one part at a time. A jump is
/// stance, spin, rotation, how many (dub, trip...), then degrees; a rail trick is stance, spin on,
/// slide, then spin off. Every part has "Don't care". After each trick: add another, call the line,
/// or undo. Pure, so it is tested without the game.
/// </summary>
internal sealed class CallMenu
{
    internal enum Step { Stance, Type, Spin, Axis, Count, Degrees, OnSpin, OnDegrees, Slide, Off, OffAxis, OffCount, OffDegrees, Typing, Next }

    /// <summary>One slice: what it says, and what picking it does to the trick being built.</summary>
    internal sealed record Slice(string Label, Func<CalledTrick, CalledTrick> Apply, string Value);

    /// <summary>The most slices on one page of the wheel (the number keys 1-9); longer lists get "More".</summary>
    public const int PerPage = 9;
    const string Any = "Don't care";

    readonly List<CalledTrick> done = new();
    readonly Stack<(Step Step, CalledTrick Draft, int Done, string[] Picks)> history = new();
    readonly List<string> picks = new();
    CalledTrick draft = new();
    int page;

    public Step Current { get; private set; } = Step.Stance;
    public IReadOnlyList<CalledTrick> Done => done;
    public CalledTrick Draft => draft;
    /// <summary>What's been picked so far for the trick being built ("Switch", "Jump", "Left").</summary>
    public IReadOnlyList<string> Picks => picks;
    /// <summary>Typing a trick in words (keyboard): the slices are off and keys go to <see cref="Text"/>.</summary>
    public bool Typing => Current == Step.Typing;
    /// <summary>The typed trick so far.</summary>
    public string Text { get; private set; } = "";
    /// <summary>Set once the line is called.</summary>
    public IReadOnlyList<CalledTrick>? Called { get; private set; }
    /// <summary>Set when backed out of entirely.</summary>
    public bool Cancelled { get; private set; }

    public string Title => Current switch
    {
        Step.Stance => "Stance",
        Step.Type => "Jump or rail",
        Step.Typing => "Type the trick",
        Step.Spin => "Spin",
        Step.Axis => "Rotation",
        Step.Count => "How many",
        Step.Degrees => "Degrees",
        Step.OnSpin => "Spin on",
        Step.OnDegrees => "Degrees on",
        Step.Slide => "Slide",
        Step.Off => "Spin off",
        Step.OffAxis => "Rotation off",
        Step.OffCount => "How many",
        Step.OffDegrees => "Degrees off",
        _ => done.Count == 1 ? "1 trick" : $"{done.Count} tricks",
    };

    /// <summary>The slices on the wheel now: up to <see cref="PerPage"/>, the last one "More" when there are more pages.</summary>
    public IReadOnlyList<string> Labels => Page().Select(s => s.Label).ToList();

    List<Slice> Page()
    {
        var all = Options();
        if (all.Count <= PerPage) return all;
        var per = PerPage - 1;
        var pages = (all.Count + per - 1) / per;
        page %= pages;
        return all.Skip(page * per).Take(per).Append(new Slice("More", t => t, "more")).ToList();
    }

    List<Slice> Options()
    {
        static Slice S(string label, Func<CalledTrick, CalledTrick> apply, string value = "") => new(label, apply, value.Length > 0 ? value : label);
        static List<Slice> Degrees(int from, int step, int max, Func<CalledTrick, int?, CalledTrick> set)
        {
            var list = new List<Slice> { S(Any, t => set(t, null), "any") };
            for (var d = from; d <= max; d += step) { var x = d; list.Add(S(x.ToString(), t => set(t, x))); }
            return list;
        }
        var flips = new[] { "frontflip", "backflip" };
        return Current switch
        {
            Step.Stance => new() { S("Regular", t => t with { Stance = "regular" }), S("Switch", t => t with { Stance = "switch" }), S(Any, t => t with { Stance = null }, "any") },
            Step.Type => new() { S("Jump", t => t with { Type = TrickType.Jump }), S("Rail", t => t with { Type = TrickType.Rail }), S("Type it", t => t with { Type = TrickType.Typed }, "typed") },
            Step.Typing => new List<Slice>(),
            Step.Spin => new() { S("Left", t => t with { Spin = "left" }), S("Right", t => t with { Spin = "right" }), S("No spin", t => t with { Spin = "none" }, "none"), S(Any, t => t with { Spin = null }, "any") },
            Step.Axis => new[] { "flat", "cork", "misty", "bio", "lincoln", "frontflip", "backflip" }
                .Select(a => S(a == "flat" ? "Flat" : char.ToUpperInvariant(a[0]) + a[1..], t => t with { Axis = a }, a))
                .Append(S(Any, t => t with { Axis = null }, "any")).ToList(),
            Step.Count => new() { S("Single", t => t with { Count = 1 }), S("Dub", t => t with { Count = 2 }), S("Trip", t => t with { Count = 3 }), S("Quad", t => t with { Count = 4 }), S(Any, t => t with { Count = null }, "any") },
            Step.Degrees => Degrees(180, 180, 2160, (t, d) => t with { Degrees = d }),
            // A normal (90) on is what the game names "Regular On" / "Switch On": no spin.
            Step.OnSpin => new() { S("Normal (90)", t => t with { Spin = "none" }, "none"), S("Left", t => t with { Spin = "left" }), S("Right", t => t with { Spin = "right" }), S(Any, t => t with { Spin = null }, "any") },
            // The game names a 90 on just "Regular On", so spins on start at 180.
            Step.OnDegrees => Degrees(180, 90, 900, (t, d) => t with { Degrees = d }),
            Step.Slide => new() { S("Slide", t => t with { Slide = "slide" }), S("Frontslide", t => t with { Slide = "frontslide" }), S("Backslide", t => t with { Slide = "backslide" }), S("50-50", t => t with { Slide = "5050" }), S("5-0", t => t with { Slide = "5-0" }), S(Any, t => t with { Slide = null }, "any") },
            // The game names spins off a rail front or back ("Back 270 Out"), not left or right.
            Step.Off => new() { S("Front", t => t with { Off = "front" }), S("Back", t => t with { Off = "back" }), S("Straight off", t => t with { Off = "none" }, "none"), S(Any, t => t with { Off = null }, "any") },
            Step.OffAxis => new() { S("Flat", t => t with { OffAxis = "flat" }), S("Cork", t => t with { OffAxis = "cork" }), S("Misty", t => t with { OffAxis = "misty" }), S("Bio", t => t with { OffAxis = "bio" }), S("Frontflip", t => t with { OffAxis = "frontflip" }, "frontflip"), S("Backflip", t => t with { OffAxis = "backflip" }, "backflip"), S(Any, t => t with { OffAxis = null }, "any") },
            Step.OffCount => new() { S("Single", t => t with { OffCount = 1 }), S("Dub", t => t with { OffCount = 2 }), S("Trip", t => t with { OffCount = 3 }), S(Any, t => t with { OffCount = null }, "any") },
            Step.OffDegrees => Degrees(90, 90, 1080, (t, d) => t with { OffDegrees = d }),
            _ => new List<Slice>()
                .Concat(done.Count < CalledLine.MaxTricks ? new[] { S("Add a trick", t => t, "add") } : Array.Empty<Slice>())
                .Append(S("Call it", t => t, "call"))
                .Append(S("Undo last", t => t, "undo")).ToList(),
        };
    }

    /// <summary>Picks slice <paramref name="index"/> (0-based) on the current page. Out of range does nothing.</summary>
    public void Pick(int index)
    {
        var slices = Page();
        if (index < 0 || index >= slices.Count || Called is not null || Cancelled) return;
        var slice = slices[index];
        if (slice.Value == "more")
        {
            page++;
            return;
        }
        if (Current == Step.Next)
        {
            switch (slice.Value)
            {
                case "call": Called = done.ToList(); break;
                case "add": Go(Step.Stance, new CalledTrick(), null); break;
                case "undo":
                    history.Clear();
                    picks.Clear();
                    done.RemoveAt(done.Count - 1);
                    draft = new CalledTrick();
                    page = 0;
                    Current = done.Count == 0 ? Step.Stance : Step.Next;
                    break;
            }
            return;
        }
        var next = slice.Apply(draft);
        Go(After(Current, next, slice.Value.ToLowerInvariant()), next, slice.Label);
    }

    void Go(Step step, CalledTrick next, string? picked)
    {
        history.Push((Current, draft, done.Count, picks.ToArray()));
        draft = next;
        page = 0;
        if (picked is not null) picks.Add(picked);
        if (step == Step.Next)
        {
            done.Add(draft);
            draft = new CalledTrick();
            picks.Clear();
        }
        Current = step;
    }

    /// <summary>Which part comes after picking <paramref name="value"/> at <paramref name="step"/>.</summary>
    static Step After(Step step, CalledTrick t, string value) => step switch
    {
        Step.Stance => Step.Type,
        Step.Type => t.Type switch { TrickType.Jump => Step.Spin, TrickType.Rail => Step.OnSpin, _ => Step.Typing },
        Step.Spin => Step.Axis,
        Step.Axis => value switch
        {
            "frontflip" or "backflip" or "cork" or "misty" or "bio" or "lincoln" => Step.Count,
            "flat" when t.Spin == "none" => Step.Next, // a straight air
            _ => Step.Degrees,
        },
        Step.Count => t.Axis is "frontflip" or "backflip" ? Step.Next : Step.Degrees,
        Step.Degrees => Step.Next,
        Step.OnSpin => value is "left" or "right" ? Step.OnDegrees : Step.Slide,
        Step.OnDegrees => Step.Slide,
        Step.Slide => Step.Off,
        Step.Off => value is "left" or "right" or "front" or "back" ? Step.OffAxis : Step.Next,
        Step.OffAxis => value is "cork" or "misty" or "bio" or "frontflip" or "backflip" ? Step.OffCount : Step.OffDegrees,
        Step.OffCount => t.OffAxis is "frontflip" or "backflip" ? Step.Next : Step.OffDegrees, // a flip has no degrees
        _ => Step.Next,
    };

    /// <summary>Adds a typed character (while <see cref="Typing"/>).</summary>
    public void Type(char c)
    {
        if (Typing && !char.IsControl(c) && Text.Length < CalledTrick.MaxText) Text += c;
    }

    /// <summary>Adds the typed trick to the line (while <see cref="Typing"/>). Nothing typed does nothing.</summary>
    public void Enter()
    {
        if (!Typing || CalledTrick.CleanText(Text) is not { } text) return;
        Text = "";
        Go(Step.Next, draft with { Type = TrickType.Typed, Text = text, Stance = null }, null);
    }

    /// <summary>Goes back one part (out of "More" pages first). Backing out of the first part closes the menu. While typing, deletes a character first.</summary>
    public void Back()
    {
        if (Called is not null || Cancelled) return;
        if (Typing && Text.Length > 0)
        {
            Text = Text[..^1];
            return;
        }
        if (page > 0)
        {
            page = 0;
            return;
        }
        if (history.Count == 0)
        {
            if (Current == Step.Stance && done.Count > 0) Current = Step.Next;
            else Cancelled = true;
            return;
        }
        var (step, previous, count, before) = history.Pop();
        while (done.Count > count) done.RemoveAt(done.Count - 1);
        draft = previous;
        picks.Clear();
        picks.AddRange(before);
        Current = step;
    }
}
