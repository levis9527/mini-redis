namespace MiniRedis.Core.Commands;

public sealed class CommandHistory
{
    private readonly List<string> _items = [];
    private int _cursor = 0;
    private string _draft = "";

    public IReadOnlyList<string> Items => _items;

    public void Accept(ParsedCommand command, ArgVisibility visibility)
    {
        if (visibility != ArgVisibility.ShowAll)
            return;
        var line = command.Raw.Trim();
        if (line.Length == 0)
            return;
        if (_items.Count == 0 || _items[^1] != line)
            _items.Add(line);
        if (_items.Count > 100)
            _items.RemoveAt(0);
        _cursor = _items.Count;
        _draft = "";
    }

    public string? Previous(string current)
    {
        if (_items.Count == 0)
            return null;
        if (_cursor == _items.Count)
            _draft = current;
        if (_cursor == 0)
            return _items[0];
        _cursor--;
        return _items[_cursor];
    }

    public string? Next()
    {
        if (_items.Count == 0 || _cursor >= _items.Count)
            return null;
        _cursor++;
        if (_cursor >= _items.Count)
            return _draft;
        return _items[_cursor];
    }
}
