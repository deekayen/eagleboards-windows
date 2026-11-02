using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EagleBoards.App;

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; protected init; } = "";

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static string V(IReadOnlyDictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v : "";
}
