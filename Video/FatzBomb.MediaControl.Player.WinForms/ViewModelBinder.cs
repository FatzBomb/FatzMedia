using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

namespace FatzBomb.MediaControl.Player.WinForms;

/// <summary>
/// Small one-file binder between WinForms controls and an <see cref="INotifyPropertyChanged"/>
/// view model: each binding pushes the view model's value into the control when the named property
/// changes, and the control's edits back into the view model. Everything runs on the UI thread
/// (the shared view model raises its changes there). <see cref="Dispose"/> unhooks the view model.
/// </summary>
internal sealed class ViewModelBinder(INotifyPropertyChanged source) : IDisposable
{
    private readonly Dictionary<string, List<Action>> _updates = new(StringComparer.Ordinal);
    private readonly List<Action> _unhook = [];
    private bool _attached;
    private int _pushing;

    /// <summary>Runs <paramref name="update"/> now and whenever one of <paramref name="properties"/> changes.</summary>
    public void OnChanged(Action update, params string[] properties)
    {
        Attach();
        foreach (var property in properties)
        {
            if (!_updates.TryGetValue(property, out List<Action>? list))
                _updates[property] = list = [];
            list.Add(update);
        }

        Push(update);
    }

    /// <summary>Binds a label (or any control's text).</summary>
    public void Text(Control control, Func<string> value, params string[] properties) =>
        OnChanged(() => control.Text = value(), properties);

    /// <summary>Binds a check box both ways.</summary>
    public void Check(CheckBox box, Func<bool> get, Action<bool> set, string property)
    {
        OnChanged(() => box.Checked = get(), property);
        box.CheckedChanged += (_, _) => Pull(() => set(box.Checked));
    }

    /// <summary>
    /// Binds a track bar both ways: the bar holds <c>value * scale</c> as an integer between
    /// <paramref name="minimum"/> and <paramref name="maximum"/> (view-model units).
    /// </summary>
    public void Track(TrackBar bar, Func<double> get, Action<double> set, string property, double minimum, double maximum, double scale = 1)
    {
        bar.Minimum = (int)Math.Round(minimum * scale);
        bar.Maximum = (int)Math.Round(maximum * scale);
        OnChanged(() => bar.Value = Math.Clamp((int)Math.Round(get() * scale), bar.Minimum, bar.Maximum), property);
        bar.ValueChanged += (_, _) => Pull(() => set(bar.Value / scale));
    }

    /// <summary>Binds a drop-down list both ways (its items are <paramref name="items"/>).</summary>
    public void Choice<T>(ComboBox box, IEnumerable<T> items, Func<T> get, Action<T> set, string property)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (T item in items)
            _ = box.Items.Add(item!);
        OnChanged(() => box.SelectedItem = get(), property);
        box.SelectionChangeCommitted += (_, _) => Pull(() =>
        {
            if (box.SelectedItem is T selected)
                set(selected);
        });
    }

    /// <summary>Binds a text box both ways, updating the view model on every keystroke.</summary>
    public void Edit(TextBox box, Func<string> get, Action<string> set, string property)
    {
        OnChanged(() =>
        {
            if (box.Text != get())
                box.Text = get();
        }, property);
        box.TextChanged += (_, _) => Pull(() => set(box.Text));
    }

    /// <summary>Binds a button to a command: a click executes it, CanExecute enables the button.</summary>
    public void Command(ButtonBase button, ICommand command, object? parameter = null)
    {
        void Refresh()
        {
            if (!button.IsDisposed)
                button.Enabled = command.CanExecute(parameter);
        }

        void OnCanExecuteChanged(object? sender, EventArgs e) => Refresh();
        command.CanExecuteChanged += OnCanExecuteChanged;
        _unhook.Add(() => command.CanExecuteChanged -= OnCanExecuteChanged);
        button.Click += (_, _) =>
        {
            if (command.CanExecute(parameter))
                command.Execute(parameter);
        };
        Refresh();
    }

    /// <summary>Calls <paramref name="changed"/> for every change of <paramref name="collection"/> until disposed.</summary>
    public void Collection(INotifyCollectionChanged collection, NotifyCollectionChangedEventHandler changed)
    {
        collection.CollectionChanged += changed;
        _unhook.Add(() => collection.CollectionChanged -= changed);
    }

    public void Dispose()
    {
        if (_attached)
            source.PropertyChanged -= OnPropertyChanged;
        _attached = false;
        foreach (Action unhook in _unhook)
            unhook();
        _unhook.Clear();
        _updates.Clear();
    }

    private void Attach()
    {
        if (_attached)
            return;

        source.PropertyChanged += OnPropertyChanged;
        _attached = true;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && _updates.TryGetValue(name, out List<Action>? updates))
        {
            foreach (Action update in updates)
                Push(update);
        }
    }

    // A control change caused by the view model is not sent back to it.
    private void Push(Action update)
    {
        _pushing++;
        try
        {
            update();
        }
        finally
        {
            _pushing--;
        }
    }

    private void Pull(Action write)
    {
        if (_pushing == 0)
            write();
    }
}
