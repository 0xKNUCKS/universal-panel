using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Processes.History;

internal sealed partial class HistoryView : UserControl
{
    private readonly ProcessHistory _history;
    private readonly Action _back;
    private bool _loading = true;

    public HistoryView(ProcessHistory history, Action back)
    {
        _history = history;
        _back = back;
        InitializeComponent();
        Events.ItemsSource = history.Events;
        KeepRecording.IsChecked = PanelSettings.Current.HistoryWhileHidden;
        history.Events.CollectionChanged += OnEventsChanged;
        UpdateSummary();
        _loading = false;
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        int started = _history.Events.Count(e => e.Started);
        Summary.Text = _history.Events.Count == 0
            ? "Records processes that start or exit while it's on (not ones that live under a second)."
            : $"{started} started · {_history.Events.Count - started} exited · newest first (last {ProcessHistory.MaxEvents:N0})";
    }

    private void OnKeepRecordingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool on = KeepRecording.IsChecked == true;
        PanelSettings.Current.HistoryWhileHidden = on;
        PanelSettings.Current.SaveSoon();
        if (on)
        {
            _history.Hold();
        }
        else
        {
            _history.Release();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _history.Clear();

    private void OnBackClick(object sender, RoutedEventArgs e) => _back();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            e.Handled = true;
            _back();
        }
    }
}
