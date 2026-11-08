#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Human-readable file filter group for host file pickers.
/// </summary>
public sealed record MediaFileFilter(string Name, IReadOnlyList<string> Extensions)
{
    public string ToOpenFileDialogFilter()
    {
        IEnumerable<string> patterns = Extensions
            .Select(extension => extension == "*" ? "*.*" : $"*{extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"{Name}|{string.Join(";", patterns)}";
    }
}
