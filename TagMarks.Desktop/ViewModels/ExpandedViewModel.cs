using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Cauldron.Models;
using Cauldron.Services;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// The EXPANDED view (spec §5.3): the expanded tagmark's primary URI and links, and the highlighted row.
/// Also tracks which tagmarks have links (the ► marker). Entering and leaving EXPANDED is a state
/// transition, which the owner makes.
/// </summary>
public sealed partial class ExpandedViewModel : ViewModelBase, IDisposable {
    private readonly ITagMarkUriService _tagMarkUriService;
    private readonly CompositeDisposable _disposables = new();

    public ExpandedViewModel(ITagMarkUriService tagMarkUriService) {
        _tagMarkUriService = tagMarkUriService;

        var selectedEntry = this.WhenAnyValue(x => x.SelectedIndex, x => x.Entries,
            (index, entries) => index >= 0 && index < entries.Count ? entries[index] : null);
        _isEntryPrimaryHelper = selectedEntry.Select(e => e?.IsPrimary == true)
            .ToProperty(this, x => x.IsEntryPrimary)
            .DisposeWith(_disposables);
        _canNavigateLinkHelper = selectedEntry.Select(e => e?.LinkedTagMarkName is not null)
            .ToProperty(this, x => x.CanNavigateLink)
            .DisposeWith(_disposables);
    }

    [Reactive] public partial string? TagMark { get; private set; }
    [Reactive] public partial IReadOnlyList<ExpandedEntry> Entries { get; private set; } = [];
    [Reactive] public partial int SelectedIndex { get; set; }
    [ObservableAsProperty] public partial bool IsEntryPrimary { get; }
    [ObservableAsProperty] public partial bool CanNavigateLink { get; }
    [Reactive] public partial IReadOnlySet<string> ExpandableTagMarks { get; private set; } = new HashSet<string>();

    /// <summary>The highlighted row, or null when none is.</summary>
    public ExpandedEntry? SelectedEntry =>
        SelectedIndex >= 0 && SelectedIndex < Entries.Count ? Entries[SelectedIndex] : null;

    /// <summary>Shows <paramref name="tagMark"/>'s entries with the first row highlighted.</summary>
    public void Open(string tagMark) {
        TagMark = tagMark;
        Refresh();
        SelectedIndex = 0;
    }

    public void Close() {
        TagMark = null;
        Entries = [];
        SelectedIndex = -1;
    }

    public void MoveSelection(int delta) {
        int newIndex = SelectedIndex + delta;
        if (newIndex >= 0 && newIndex < Entries.Count)
            SelectedIndex = newIndex;
    }

    /// <summary>Highlights the link to <paramref name="uri"/>, if it is listed.</summary>
    public void SelectLink(string uri) {
        int index = Entries.ToList().FindIndex(e => !e.IsPrimary && e.Uri == uri);
        if (index >= 0)
            SelectedIndex = index;
    }

    /// <summary>
    /// Rebuilds the entries and keeps the highlight at the same index, clamped to the new list.
    /// </summary>
    public void Refresh() {
        if (TagMark is null) return;
        int previousIndex = SelectedIndex;
        var entries = new List<ExpandedEntry>();

        string primaryUri = _tagMarkUriService.GetUri(TagMark);
        if (!string.IsNullOrEmpty(primaryUri)) {
            string primaryDisplay = primaryUri;
            entries.Add(new ExpandedEntry(primaryUri, primaryDisplay, IsPrimary: true, LinkedTagMarkName: null));
        }

        var secondaryUris = _tagMarkUriService.GetSecondaryUris(TagMark);
        foreach (var uri in secondaryUris) {
            string? linkedName = _tagMarkUriService.FindTagMarkByUri(uri);
            string display = linkedName is not null ? $"→ {linkedName}" : uri;
            entries.Add(new ExpandedEntry(uri, display, IsPrimary: false, LinkedTagMarkName: linkedName));
        }

        Entries = entries;
        // Set it again even when unchanged: replacing the ListBox's ItemsSource resets its
        // SelectedIndex to -1, and the TwoWay binding writes that back here.
        SelectedIndex = entries.Count == 0 ? -1 : Math.Clamp(previousIndex, 0, entries.Count - 1);
    }

    public void RefreshExpandableTagMarks() {
        var allNames = _tagMarkUriService.GetTagMarks(string.Empty);
        ExpandableTagMarks = allNames
            .Where(name => _tagMarkUriService.GetSecondaryUris(name).Count > 0)
            .ToHashSet();
    }

    public void Dispose() => _disposables.Dispose();
}
