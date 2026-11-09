namespace Jibbers.Sdk;

// Selection is owned by Unity's EventSystem; no scene enumeration is needed to find its input field.
internal sealed class FocusedInputLookup(Func<nint, nint> resolve, Func<nint, bool> alive,
    Func<nint, bool> focused, Func<nint, IDisposable> keep) : IDisposable
{
    nint selection, field;
    IDisposable? selectedLease, fieldLease;

    public bool Read(nint selected)
    {
        if (selected == 0 || !alive(selected)) { Dispose(); return false; }
        if (selected != selection || (field != 0 && !alive(field)))
        {
            Dispose();
            selectedLease = keep(selected);
            selection = selected;
            field = resolve(selected);
            if (field != 0 && alive(field)) fieldLease = keep(field);
            else field = 0;
        }
        return field != 0 && focused(field);
    }

    public void Dispose()
    {
        fieldLease?.Dispose(); selectedLease?.Dispose();
        fieldLease = selectedLease = null;
        selection = field = 0;
    }
}
