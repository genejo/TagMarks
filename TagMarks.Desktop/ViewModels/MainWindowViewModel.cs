using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using DynamicData;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows.Input;
using Cauldron.Managers;
using Cauldron.Models;
using Cauldron.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using TagMarks.Desktop.Services;
using ContainerDisposable = ReactiveUI.Primitives.Reactive.Disposables.ContainerDisposable;

namespace TagMarks.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IActivatableViewModel, IDisposable {
    private readonly ITagMarkUriService _tagMarkUriService;
    private readonly IClipboardService _clipboardService;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ShortcutCatalog _shortcutCatalog;
    private readonly KeyMap _keyMap;

    // Emit on this to force DynamicData's SortAndBind operator to re-evaluate order.
    private readonly BehaviorSubject<IComparer<string>> _sortComparer;
    // The service's comparer for one cache snapshot (TASK-44). Replaced only on the UI thread in
    // ReloadSourceCache, so a sort never mixes snapshots; SortAndBind reads it through the comparers
    // from CreateSortComparer, so inserts use the latest snapshot without a full re-sort.
    private IComparer<string> _snapshotComparer;

    // DynamicData source cache holding ALL tag marks (unfiltered).
    // Filtering is handled reactively in the pipeline via .Filter().
    private readonly SourceCache<string, string> _tagMarksSource = new(x => x);

    // Set before a SourceCache reload to direct SyncUiState to select a specific item.
    private string? _pendingSelectItem;
    // Suppresses SyncUiState during ReloadSourceCache so all collection mutations
    // (EditDiff + sort signal) complete before a single SyncUiState call runs.
    private bool _isReloading;
    private readonly ReadOnlyObservableCollection<string> _filteredTagMarks;
    // Applies a programmatic filter change right away, bypassing the typing throttle, so the
    // pipeline has processed it by the time OnNext returns.
    private readonly Subject<string> _immediateFilter = new();
    private readonly Subject<Unit> _filterTextSanitized = new();
    private readonly Subject<Unit> _renameTextSanitized = new();

    private readonly SerialDisposable _clipHideTimer = new();

    // Tagmark deletes and link removals share one stack: Ctrl/Cmd+Z undoes the most recent (spec §4.2).
    private readonly Stack<PendingUndo> _undoStack = new();

    // Every command, registered via Track() at creation, so all ThrownExceptions are observed.
    private readonly List<IHandleObservableErrors> _commands = [];

    private string? _pendingExpandedNavigationTarget;
    private bool _deferredRefreshFromWatcher;
    // Duplicate primary URIs already reported (or waiting to be), so each gets one toast (spec §11 #50).
    private readonly HashSet<string> _knownDuplicateUris = [];
    private readonly HashSet<string> _unreportedDuplicateUris = [];
    // The last startup hint shown (invalid GlobalHotkey setting, hotkey unavailable, unreadable config file),
    // so the duplicate URI toast can wait for it.
    private string? _startupHintMessage;
    // True while the applied filter holds the :dup token, which groups the list by URI owner.
    private bool _isDuplicateFilter;
    // Names of the clip-tagmarks, for :clip. A type switch keeps the set of names, so the
    // filter learns about it from this set changing (spec §3.2).
    private readonly BehaviorSubject<IReadOnlySet<string>> _clipTagMarks = new(new HashSet<string>());

    public const string DuplicateFilterToken = ":dup";
    public const string ClipFilterToken = ":clip";
    private const string DefaultEmptyStateText = "No tagmarks match.\nPress [Tab] to create one.";
    private bool _preservePositionOnMiss;

    // Resources created once in the constructor live as long as the VM, not one activation
    // (TASK-45): WhenActivated would dispose them on the first deactivation for good.
    private readonly CompositeDisposable _disposables = new();
    private volatile bool _isReadingClipboard;

    public ViewModelActivator Activator { get; }

    /// <summary>The tooltip and the toast.</summary>
    public NotificationsViewModel Notifications { get; }

    /// <summary>The content of CONFIRM_PANEL and the operation it applies on Enter.</summary>
    public ConfirmPanelViewModel ConfirmPanel { get; }

    /// <summary>The EXPANDED view's entries and highlight, and which tagmarks have links.</summary>
    public ExpandedViewModel Expanded { get; }

    public MainWindowViewModel(ITagMarkUriService tagMarkUriService, IClipboardService clipboardService, IConfigurationManager configurationManager, ILogger<MainWindowViewModel> logger, ShortcutCatalog? shortcutCatalog = null) {
        _tagMarkUriService = tagMarkUriService;
        _clipboardService = clipboardService;
        _configurationManager = configurationManager;
        _logger = logger;
        _shortcutCatalog = shortcutCatalog ?? ShortcutCatalog.Current;
        _keyMap = KeyMap.For(_shortcutCatalog.IsMac);

        Activator = new ViewModelActivator();
        Notifications = new NotificationsViewModel();
        ConfirmPanel = new ConfirmPanelViewModel();
        Expanded = new ExpandedViewModel(_tagMarkUriService);
        // Before the pipeline below, whose first SyncUiState sets the properties these derive from.
        CreateDerivedProperties();

        _snapshotComparer = _tagMarkUriService.TagMarkComparer;
        _sortComparer = new BehaviorSubject<IComparer<string>>(CreateSortComparer());

        // Typing an invalid character is blocked in the view (AcceptFilterInput/AcceptRenameInput); pasted
        // text is sanitized here. Subscribed before the filter pipeline, so it never sees unsanitized text:
        // WhenAnyValue reads the property when notified and drops repeats.
        // In ADD the filter text becomes the new tagmark's name, so ':' is removed there too.
        this.WhenAnyValue(x => x.TagMarkFilter)
            .Subscribe(text => SanitizeText(text,
                t => IsInAddMode ? Validation.SanitizeFileName(t) : Validation.SanitizeFilterText(t),
                sanitized => TagMarkFilter = sanitized, _filterTextSanitized))
            .DisposeWith(_disposables);
        this.WhenAnyValue(x => x.RenameText)
            .Subscribe(text => SanitizeText(text, t => Validation.SanitizeFileName(t),
                sanitized => RenameText = sanitized, _renameTextSanitized))
            .DisposeWith(_disposables);
        _filterTextSanitized.DisposeWith(_disposables);
        _renameTextSanitized.DisposeWith(_disposables);

        // Filter predicate re-evaluates whenever TagMarkFilter changes.
        // Replicates SubstringSearchService logic: every whitespace-delimited term
        // must appear as a case-insensitive substring of the tag mark string.
        // The initial value is applied at once, so the list is populated on construction;
        // only later changes (typing) are throttled.
        var filterText = this.WhenAnyValue(x => x.TagMarkFilter);
        var filterInput =
            filterText
                .Take(1)
                .Concat(filterText.Skip(1).Throttle(TimeSpan.FromMilliseconds(30), RxSchedulers.MainThreadScheduler))
                .Merge(_immediateFilter)
                .DistinctUntilChanged()
                .Do(filter => _logger.LogDebug("Search text changed: {Filter}", filter));
        // :dup depends on the duplicates as well as the text, so a refresh that changes them re-filters.
        // :clip likewise depends on the clip-tagmarks.
        var filterPredicate =
            Observable.CombineLatest(filterInput, this.WhenAnyValue(x => x.DuplicateUriOwners), _clipTagMarks,
                    (filter, _, _) => filter)
                .Do(ApplyFilterTokens)
                .Select(BuildFilterPredicate);

        // DynamicData pipeline: _tagMarksSource holds ALL tag marks; .Filter() narrows
        // the set reactively, then SortAndBind orders and binds the result.
        _tagMarksSource
            .Connect()
            .Filter(filterPredicate)
            .SortAndBind(out _filteredTagMarks, _sortComparer)
            .Subscribe(_ => { if (!_isReloading) SyncUiState(); })
            .DisposeWith(_disposables);
        // Disposed after the pipeline subscription above, which is still connected to them.
        _tagMarksSource.DisposeWith(_disposables);
        _sortComparer.DisposeWith(_disposables);
        _immediateFilter.DisposeWith(_disposables);
        Notifications.DisposeWith(_disposables);
        Expanded.DisposeWith(_disposables);
        _clipHideTimer.DisposeWith(_disposables);
        _clipTagMarks.DisposeWith(_disposables);
        var initialTagMarks = _tagMarkUriService.GetTagMarks(string.Empty);
        TrackClipTagMarks(initialTagMarks);
        _tagMarksSource.AddOrUpdate(initialTagMarks);
        Expanded.RefreshExpandableTagMarks();
        TrackDuplicateUris();
        // Ensure UI state (counts, empty-state) is correct immediately, even when the
        // cache is empty and the pipeline emitted no changeset.
        SyncUiState();

        // Where each command can execute (spec §3.4). InvokeCommand skips a disabled command, MenuItems
        // disable themselves, and the view routes keys through CanExecute instead of repeating mode checks.
        var state = this.WhenAnyValue(x => x.State);
        var inSearch = state.Select(s => s == UiState.Search);
        var inExpanded = state.Select(s => s == UiState.Expanded);
        var inPrimary = state.Select(s => s.IsPrimary);
        var inAdd = state.Select(s => s == UiState.Add);
        var inSearchOrAdd = state.Select(s => s == UiState.Search || s == UiState.Add);
        var inRename = state.Select(s => s == UiState.Rename);
        var inConfirmPanel = state.Select(s => s.Panel == UiPanel.Confirm);
        var inHelpPanel = state.Select(s => s.Panel == UiPanel.Help);
        var inPrimaryOrHelpPanel = state.Select(s => s.IsPrimary || s.Panel == UiPanel.Help);

        RefreshClipboardCommand = Track(ReactiveCommand.CreateFromObservable(ReadClipboard));
        // The command's result arrives on the main thread (its output scheduler).
        RefreshClipboardCommand
            .DistinctUntilChanged()
            .Subscribe(text => CurrentClipboardContent = text)
            .DisposeWith(_disposables);
        HideUiCommand = Track(ReactiveCommand.CreateFromTask(HideUiAsync, inPrimary));
        MoveSelectionCommand = Track(ReactiveCommand.Create<int>(MoveSelection, inSearch));
        ToggleAddModeCommand = Track(ReactiveCommand.Create(ToggleAddMode, inSearchOrAdd));
        CancelAddModeCommand = Track(ReactiveCommand.Create(() => ExitAddMode(restoreFilter: true), inAdd));
        CancelRenameCommand = Track(ReactiveCommand.Create(ExitRenameMode, inRename));
        EnterRenameModeCommand = Track(ReactiveCommand.Create(EnterRenameModeForSelected, inSearch));
        ConfirmRenameCommand = Track(ReactiveCommand.CreateFromTask(ConfirmRenameAsync, inRename));
        CopyTagMarkUriCommand = Track(ReactiveCommand.Create(CopySelectedPayload, inPrimary));
        LaunchTagMarkCommand = Track(ReactiveCommand.CreateFromTask<string>(LaunchTagMarkAsync, inSearch));
        DeleteTagMarkCommand = Track(ReactiveCommand.CreateFromTask<string>(DeleteTagMarkAsync, inPrimary));
        ConfirmAddCommand = Track(ReactiveCommand.CreateFromTask(ConfirmAddAsync, inAdd));
        UndoDeleteCommand = Track(ReactiveCommand.CreateFromTask(UndoDeleteAsync));
        ExpandSelectedCommand = Track(ReactiveCommand.Create(ExpandSelected, inSearch));
        CollapseExpandedCommand = Track(ReactiveCommand.Create(CollapseExpanded, inExpanded));
        MoveExpandedSelectionCommand = Track(ReactiveCommand.Create<int>(Expanded.MoveSelection, inExpanded));
        LaunchExpandedEntryCommand = Track(ReactiveCommand.CreateFromTask(LaunchExpandedEntryAsync, inExpanded));
        NavigateExpandedLinkCommand = Track(ReactiveCommand.Create(NavigateExpandedLink, inExpanded));
        DeleteExpandedEntryCommand = Track(ReactiveCommand.CreateFromTask(DeleteExpandedEntryAsync, inExpanded));
        DeleteSelectedCommand = Track(ReactiveCommand.CreateFromTask(DeleteSelectedAsync, inPrimary));
        CopyExpandedEntryUriCommand = Track(ReactiveCommand.Create(CopyExpandedEntryUri, inExpanded));
        AddLinkCommand = Track(ReactiveCommand.Create(AddLink, inPrimary));
        CancelConfirmPanelCommand = Track(ReactiveCommand.Create(ExitConfirmPanel, inConfirmPanel));
        ChangePayloadCommand = Track(ReactiveCommand.Create(ChangePayload, inPrimary));
        ConfirmPanelConfirmCommand = Track(ReactiveCommand.CreateFromTask(ConfirmPanelConfirmAsync, inConfirmPanel));
        SwitchTypeCommand = Track(ReactiveCommand.CreateFromTask(SwitchTypeAsync, inPrimary));
        ToggleHelpPanelCommand = Track(ReactiveCommand.Create(ToggleHelpPanel, inPrimaryOrHelpPanel));
        CloseHelpPanelCommand = Track(ReactiveCommand.Create(CloseHelpPanel, inHelpPanel));

        TagMarkFilter = string.Empty;

        this.WhenActivated(OnActivated);
        _logger.LogDebug("ViewModel created");
    }

    /// <summary>
    /// Releases the resources owned for the VM's whole lifetime. Safe to call more than once.
    /// </summary>
    public void Dispose() => _disposables.Dispose();

    internal IReadOnlyList<IHandleObservableErrors> TrackedCommands => _commands;

    private void OnActivated(ContainerDisposable disposables) {
		/* async bat loading
        // Load initial tag marks on activation (non-blocking; service starts empty).
        Observable.StartAsync(() => _tagMarkUriService.RefreshTagMarksAsync())
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ReloadSourceCache(),
                ex => _logger.LogError(ex, "Initial tag marks load failed"))
            .DisposeWith(disposables);
		*/

        if (_tagMarkUriService.StartupRecoveryCount > 0)
            Notifications.ShowToast($"Recovered {_tagMarkUriService.StartupRecoveryCount} tagmark(s) from previous session");

        // Set at startup while the window is hidden, so the hint waits for the first show. Subscribed before
        // the hotkey-unavailable hint, which replaces it when both are due on the same show.
        this.WhenAnyValue(x => x.IsWindowVisible, x => x.IsHotkeySettingInvalid,
                (visible, invalid) => visible && invalid)
            .Where(show => show)
            .Take(1)
            .Subscribe(_ => ShowStartupHint(
                $"Invalid GlobalHotkey setting, using {_shortcutCatalog.Summon}: see \"Global hotkey\" in the README"))
            .DisposeWith(disposables);

        // The hook usually fails at startup while the window is hidden, so the hint waits for the first show.
        this.WhenAnyValue(x => x.IsWindowVisible, x => x.IsHotkeyUnavailable,
                (visible, unavailable) => visible && unavailable)
            .Where(show => show)
            .Take(1)
            .Subscribe(_ => ShowStartupHint(HotkeyUnavailableMessage()))
            .DisposeWith(disposables);

        // Set at startup while the window is hidden. Subscribed after the hotkey hints: when one of them shows
        // on the same show, this one waits for a later show instead of replacing it.
        this.WhenAnyValue(x => x.IsWindowVisible, x => x.UnreadableConfigFile,
                (visible, path) => visible && path is not null)
            .Where(show => show && !IsStartupHintVisible)
            .Take(1)
            .Subscribe(_ => ShowStartupHint($"Can't read {UnreadableConfigFile}: using default settings"))
            .DisposeWith(disposables);

        // A duplicate found while the window is hidden is reported on the next show. Subscribed after the
        // startup hints, so it sees one that shows on the same show and waits for a later one.
        this.WhenAnyValue(x => x.IsWindowVisible)
            .Where(visible => visible)
            .Subscribe(_ => ShowDuplicateUriToast())
            .DisposeWith(disposables);

        // Poll clipboard only while the window is visible.
        // When IsWindowVisible turns true, start a 500ms interval;
        // when it turns false, dispose the interval (zero idle CPU).
        // The ticks and the reads run off the UI thread; a tick during a slow read is skipped.
        var clipboardPolling = this.WhenAnyValue(x => x.IsWindowVisible)
            .DistinctUntilChanged()
            .Select(visible => visible
                ? Observable.Interval(TimeSpan.FromMilliseconds(500), RxSchedulers.TaskpoolScheduler)
                    .Select(_ => Unit.Default)
                : Observable.Empty<Unit>());
        // Observable.Switch is named explicitly: ReactiveUI.Binding.Reactive also defines a
        // Switch extension and the two would be ambiguous as a fluent call.
        Observable.Switch(clipboardPolling)
            .InvokeCommand(this, vm => vm.RefreshClipboardCommand)
            .DisposeWith(disposables);

        // File-watcher: external changes to the TagMarks folder
        Observable.FromEvent(
                h => _tagMarkUriService.TagMarksChanged += h,
                h => _tagMarkUriService.TagMarksChanged -= h)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnTagMarksChangedExternally())
            .DisposeWith(disposables);

        // The filter keeps focus in EXPANDED, so typing/Backspace/Delete/paste edit it: any edit
        // collapses to SEARCH and keeps the text (TASK-17). Ctrl+Enter navigation clears the filter
        // on purpose and re-expands on the destination, so it doesn't collapse.
        this.WhenAnyValue(x => x.TagMarkFilter)
            .Skip(1)
            .Where(_ => IsExpanded && _pendingExpandedNavigationTarget is null)
            .Subscribe(_ => CollapseExpanded())
            .DisposeWith(disposables);

        _logger.LogDebug(
            "[vm {ThreadId}]: ViewModel activated",
            Environment.CurrentManagedThreadId);

        Disposable
            .Create(
                () =>
                    _logger.LogDebug(
                        "[vm {ThreadId}]: ViewModel deactivated",
                        Environment.CurrentManagedThreadId))
            .DisposeWith(disposables);

        this
            .WhenAnyValue(x => x.SelectedTagMarkIndex)
            .Subscribe(OnSelectedTagMarkIndexChanged)
            .DisposeWith(disposables);

        _commands.Select(c => c.ThrownExceptions).Merge()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ex => _logger.LogError(ex, "Command failed"))
            .DisposeWith(disposables);
    }

    private void OnTagMarksChangedExternally() {
        if (IsInRenameMode)
            _deferredRefreshFromWatcher = true;
        else {
            _preservePositionOnMiss = true;
            ReloadSourceCache();
        }
    }

    private void OnSelectedTagMarkIndexChanged(int index) {
        _logger.LogDebug("Selected index: {Index}", index);
        if (IsExpanded && Expanded.TagMark is not null) {
            int expandedIndex = FilteredTagMarks.IndexOf(Expanded.TagMark);
            if (expandedIndex >= 0 && index != expandedIndex) {
                SelectedTagMarkIndex = expandedIndex;
                return;
            }
        }
        UpdateSelectionDerivedState(index >= 0 && index < FilteredTagMarks.Count
            ? FilteredTagMarks[index]
            : null);
    }

    /// <summary>
    /// The properties derived from others ([ObservableAsProperty]). They update synchronously, in the
    /// same call that changes their sources.
    /// </summary>
    private void CreateDerivedProperties() {
        _filterCountDisplayHelper = this.WhenAnyValue(x => x.FilteredCount, x => x.TotalCount,
                (filtered, total) => $"{filtered} of {total}")
            .ToProperty(this, x => x.FilterCountDisplay)
            .DisposeWith(_disposables);
        _isFilteredListEmptyHelper = this.WhenAnyValue(x => x.FilteredCount, count => count == 0)
            .ToProperty(this, x => x.IsFilteredListEmpty)
            .DisposeWith(_disposables);
        _showEmptyStateHelper = this.WhenAnyValue(x => x.IsFilteredListEmpty, x => x.State,
                (empty, state) => empty && state.Mode is not (UiMode.Add or UiMode.Rename))
            .ToProperty(this, x => x.ShowEmptyState)
            .DisposeWith(_disposables);

        var info = this.WhenAnyValue(x => x.SelectedTagMarkInfo);
        _currentTagMarkUriHelper = info.Select(i => i?.Uri ?? string.Empty)
            .ToProperty(this, x => x.CurrentTagMarkUri, initialValue: string.Empty)
            .DisposeWith(_disposables);
        _payloadLabelHelper = info.Select(i => i is null ? string.Empty : BuildPayloadLabel(i.Payload))
            .ToProperty(this, x => x.PayloadLabel, initialValue: string.Empty)
            .DisposeWith(_disposables);
        _selectedTagMarkTypeHelper = info.Select(i => i?.Type ?? TagMarkType.Uri)
            .ToProperty(this, x => x.SelectedTagMarkType)
            .DisposeWith(_disposables);
        _isSelectedTagMarkUriHelper = info.Select(i => i is null || i.Type == TagMarkType.Uri)
            .ToProperty(this, x => x.IsSelectedTagMarkUri, initialValue: true)
            .DisposeWith(_disposables);
        _canSwitchSelectedToUriHelper = info
            .Select(i => i is null || i.Type == TagMarkType.Uri || Validation.IsValidUri(i.Payload.Value))
            .ToProperty(this, x => x.CanSwitchSelectedToUri, initialValue: true)
            .DisposeWith(_disposables);
        // Not pure: it also reads the service (current payload, URI owners) at the time its inputs change.
        _canChangePayloadHelper = this.WhenAnyValue(
                x => x.SelectedTagMark,
                x => x.CurrentClipboardContent,
                x => x.SelectedTagMarkInfo,
                (_, _, _) => ComputeCanChangePayload())
            .ToProperty(this, x => x.CanChangePayload)
            .DisposeWith(_disposables);
    }

    private TCommand Track<TCommand>(TCommand command) where TCommand : IHandleObservableErrors {
        _commands.Add(command);
        return command;
    }

    // ── Command bodies ───────────────────────────────────────────

    private async Task HideUiAsync() {
        _logger.LogDebug("Esc pressed");
        FinalizeAllPendingDeletes();
        ResetToSearchState();
        await HideUiInteraction.Handle(Unit.Default);
    }

    private void MoveSelection(int delta) {
        if (IsInAddMode) return;
        int newIndex = SelectedTagMarkIndex + delta;
        if (newIndex >= 0 && newIndex < FilteredTagMarks.Count)
            SelectedTagMarkIndex = newIndex;
    }

    /// <returns>True when ADD was entered.</returns>
    private bool ToggleAddMode() {
        if (IsInAddMode) {
            ExitAddMode(restoreFilter: true);
            return false;
        }
        return TryEnterAddMode();
    }

    private void EnterRenameModeForSelected() {
        if (State != UiState.Search) return;
        if (SelectedTagMark is null) {
            ShowTooltip("No tagmark selected");
            return;
        }
        EnterRenameMode(SelectedTagMark);
    }

    private async Task ConfirmRenameAsync() {
        string newName = (RenameText ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(newName)) {
            ShowTooltip("Name cannot be empty");
            return;
        }
        if (!Validation.HasValidFileNameChars(newName)) {
            ShowTooltip("Name contains invalid characters");
            return;
        }
        if (newName != RenameOriginalName && _tagMarkUriService.TagMarkExists(newName)) {
            ShowTooltip("TagMark already exists");
            return;
        }

        if (newName == RenameOriginalName) {
            ExitRenameMode();
            return;
        }

        if (!await _tagMarkUriService.RenameTagMarkAsync(RenameOriginalName!, newName)) {
            ShowTooltip("Failed to rename tagmark");
            return;
        }

        _logger.LogInformation("TagMark renamed: {OldName} → {NewName}", RenameOriginalName, newName);
        ExitRenameMode();
        ReloadSourceCache(selectItem: newName);
    }

    private void CopySelectedPayload() {
        if (IsInAddMode) return;
        if (SelectedTagMark is null) return;
        var payload = _tagMarkUriService.GetPayload(SelectedTagMark).Value;
        if (string.IsNullOrEmpty(payload)) return;
        if (!CopyToClipboard(payload)) return;
        _logger.LogInformation("Copied payload to clipboard for {TagMark}", SelectedTagMark);
    }

    private async Task LaunchTagMarkAsync(string item) {
        _logger.LogInformation("TagMark launched: {TagMark}", item);
        bool isClip = _tagMarkUriService.GetTagMarkType(item) == TagMarkType.Clip;
        string payload = _tagMarkUriService.GetPayload(item).Value;
        // On failure the window stays open so the error toast can be read.
        if (isClip && !CopyToClipboard(payload)) return;
        if (!await _tagMarkUriService.OpenTagMarkUriAsync(item)) {
            ShowFeedback(CouldNotOpenUriMessage(payload));
            return;
        }
        if (!isClip)
            await HideUiInteraction.Handle(Unit.Default);
        TagMarkFilter = string.Empty;
        ReloadSourceCache();
        if (isClip) {
            _clipHideTimer.Disposable = Observable
                .Timer(TimeSpan.FromMilliseconds(500), RxSchedulers.MainThreadScheduler)
                .SelectMany(_ => HideUiInteraction.Handle(Unit.Default))
                .Subscribe(_ => { }, ex => _logger.LogError(ex, "Hiding the window after a clip launch failed"));
        }
    }

    private async Task ConfirmAddAsync() {
        string name = (TagMarkFilter ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(name)) {
            ShowTooltip("Name cannot be empty");
            return;
        }
        if (!Validation.HasValidFileNameChars(name)) {
            ShowTooltip("Name contains invalid characters");
            return;
        }
        if (_tagMarkUriService.TagMarkExists(name)) {
            ShowTooltip("TagMark already exists");
            return;
        }
        bool success = CapturedPayloadIsUri
            ? await _tagMarkUriService.AddTagMarkAsync(name, CapturedPayload)
            : await _tagMarkUriService.AddClipTagMarkAsync(name, CapturedPayload);
        if (!success) {
            ShowTooltip("Failed to create tagmark");
            return;
        }

        _logger.LogInformation("TagMark created: {TagMark}", name);
        ExitAddMode(restoreFilter: false);
        ReloadSourceCache(selectItem: name);
    }

    private async Task UndoDeleteAsync() {
        if (_undoStack.Count == 0) return;

        PendingUndo pending = _undoStack.Pop();
        bool restored = pending switch {
            PendingUndo.LinkRemoval link => await UndoLinkRemovalAsync(link),
            PendingUndo.TagMarkDelete delete => await UndoTagMarkDeleteAsync(delete.TagMark),
            _ => false
        };

        if (_undoStack.Count > 0)
            ShowUndoToast(restored ? pending : null);
        else
            Notifications.DismissToast();
    }

    private void ExpandSelected() {
        if (State != UiState.Search) return;
        if (SelectedTagMark is null) return;
        var secondaryUris = _tagMarkUriService.GetSecondaryUris(SelectedTagMark);
        if (secondaryUris.Count == 0) {
            ShowTooltip("No links");
            return;
        }
        ExpandTagMark(SelectedTagMark);
    }

    private async Task LaunchExpandedEntryAsync() {
        if (Expanded.SelectedEntry is not { } entry) return;
        if (Expanded.TagMark is null) return;
        string parentTagMark = Expanded.TagMark;
        _logger.LogInformation("Expanded entry launched: {Uri}", entry.Uri);
        if (!await _tagMarkUriService.OpenUriForTagMarkAsync(parentTagMark, entry.Uri)) {
            ShowFeedback(CouldNotOpenUriMessage(entry.Uri));
            return;
        }
        CollapseExpanded();
        await HideUiInteraction.Handle(Unit.Default);
        TagMarkFilter = string.Empty;
    }

    private void NavigateExpandedLink() {
        if (Expanded.SelectedEntry is not { } entry) return;
        if (entry.LinkedTagMarkName is null) return;

        string? targetTagMark = _tagMarkUriService.FindTagMarkByUri(entry.Uri);
        if (targetTagMark is null) {
            ShowTooltip("TagMark no longer exists");
            return;
        }

        NavigateToExpandedTagMark(targetTagMark);
    }

    // Ctrl/Cmd+D: deletes what the current mode selects — the tagmark in SEARCH,
    // the highlighted link in EXPANDED (spec §3.4).
    private async Task DeleteSelectedAsync() {
        if (!State.IsPrimary) return;
        if (IsExpanded)
            await DeleteExpandedEntryAsync();
        else if (SelectedTagMark is { } item)
            await DeleteTagMarkAsync(item);
    }

    private void CopyExpandedEntryUri() {
        if (Expanded.SelectedEntry is not { } entry) return;
        if (!CopyToClipboard(entry.Uri)) return;
        _logger.LogInformation("Copied expanded entry URI to clipboard: {Uri}", entry.Uri);
    }

    private void AddLink() {
        if (!State.IsPrimary) return;
        string? currentTagMark = IsExpanded ? Expanded.TagMark : SelectedTagMark;
        if (currentTagMark is null) {
            ShowTooltip("No tagmark selected");
            return;
        }
        string? clipUri = _clipboardService.GetTextFirstLine();
        if (string.IsNullOrWhiteSpace(clipUri) || !Validation.IsValidUri(clipUri)) {
            ShowTooltip("No valid URI in clipboard");
            return;
        }
        var type = _tagMarkUriService.GetTagMarkType(currentTagMark);
        if (type == TagMarkType.Uri) {
            string primaryUri = _tagMarkUriService.GetUri(currentTagMark);
            if (clipUri == primaryUri) {
                ShowTooltip("Already the primary URI");
                return;
            }
        } else {
            string payload = _tagMarkUriService.GetPayload(currentTagMark).Value;
            if (clipUri == payload) {
                ShowTooltip("URI matches clip payload");
                return;
            }
        }
        var secondaryUris = _tagMarkUriService.GetSecondaryUris(currentTagMark);
        if (secondaryUris.Contains(clipUri)) {
            ShowTooltip("URI already linked");
            return;
        }
        ConfirmPanel.PrepareAddLink(currentTagMark, IsExpanded, clipUri);
        OpenConfirmPanel();
    }

    private void ChangePayload() {
        if (!State.IsPrimary) return;
        string? currentTagMark = IsExpanded ? Expanded.TagMark : SelectedTagMark;
        if (currentTagMark is null) {
            ShowTooltip("No tagmark selected");
            return;
        }
        string clipContent = CurrentClipboardContent;
        if (string.IsNullOrWhiteSpace(clipContent)) {
            ShowTooltip("Clipboard is empty");
            return;
        }
        string currentPayload = _tagMarkUriService.GetPayload(currentTagMark).Value;
        string normalized = clipContent.ReplaceLineEndings("").Trim();
        bool newIsUri = Validation.IsValidUri(normalized);
        string effectivePayload = newIsUri ? normalized : clipContent;
        if (effectivePayload == currentPayload) {
            ShowTooltip("Payload unchanged");
            return;
        }
        if (newIsUri) {
            string? existingOwner = _tagMarkUriService.FindTagMarkByUri(normalized);
            if (existingOwner is not null && existingOwner != currentTagMark) {
                ShowTooltip($"URI already saved as \"{existingOwner}\"");
                return;
            }
        } else if (Validation.IsClipPayloadTooLong(clipContent)) {
            ShowTooltip("Clip text too long (max ~16,000 characters)");
            return;
        }
        var currentType = _tagMarkUriService.GetTagMarkType(currentTagMark);
        ConfirmPanel.PrepareChangePayload(currentTagMark, IsExpanded,
            currentPayload, currentType, effectivePayload, newIsUri ? TagMarkType.Uri : TagMarkType.Clip);
        OpenConfirmPanel();
    }

    private async Task ConfirmPanelConfirmAsync() {
        if (!IsConfirmPanelVisible || ConfirmPanel.Pending is not { } pending) return;
        string currentTagMark = pending.TagMark;
        bool returnToExpanded = pending.FromExpanded;
        if (!_tagMarkUriService.TagMarkExists(currentTagMark)) {
            ExitConfirmPanel();
            ShowTooltip("TagMark no longer exists");
            return;
        }
        if (pending.Kind == ConfirmKind.ChangePayload) {
            var oldType = _tagMarkUriService.GetTagMarkType(currentTagMark);
            if (!await _tagMarkUriService.ChangePayloadAsync(currentTagMark, pending.Payload, pending.PayloadType)) {
                ExitConfirmPanel();
                ShowTooltip("Failed to change payload");
                return;
            }
            _logger.LogInformation("Payload changed for {TagMark}", currentTagMark);
            ExitConfirmPanel();
            bool typeChanged = oldType != pending.PayloadType;
            if (typeChanged && returnToExpanded)
                CollapseExpanded();
            ReloadSourceCache();
            if (returnToExpanded && !typeChanged)
                Expanded.Refresh();
        } else {
            if (!await _tagMarkUriService.AddSecondaryUriAsync(currentTagMark, pending.Payload)) {
                ExitConfirmPanel();
                ShowTooltip("Failed to add link");
                return;
            }
            _logger.LogInformation("Secondary URI added: {Uri} to {TagMark}", pending.Payload, currentTagMark);
            ExitConfirmPanel();
            Expanded.RefreshExpandableTagMarks();
            if (returnToExpanded)
                Expanded.Refresh();
        }
    }

    private async Task SwitchTypeAsync() {
        if (!State.IsPrimary) return;
        string? currentTagMark = IsExpanded ? Expanded.TagMark : SelectedTagMark;
        if (currentTagMark is null) {
            ShowTooltip("No tagmark selected");
            return;
        }
        var currentType = _tagMarkUriService.GetTagMarkType(currentTagMark);
        string currentPayload = _tagMarkUriService.GetPayload(currentTagMark).Value;
        var newType = currentType == TagMarkType.Uri ? TagMarkType.Clip : TagMarkType.Uri;

        if (currentType == TagMarkType.Clip) {
            if (!Validation.IsValidUri(currentPayload)) {
                ShowTooltip("Clip text is not a valid URI");
                return;
            }
            string? existingOwner = _tagMarkUriService.FindTagMarkByUri(currentPayload);
            if (existingOwner is not null && existingOwner != currentTagMark) {
                ShowTooltip($"URI already saved as \"{existingOwner}\"");
                return;
            }
        }

        bool wasExpanded = IsExpanded;
        if (wasExpanded) CollapseExpanded();

        if (!await _tagMarkUriService.ChangePayloadAsync(currentTagMark, currentPayload, newType)) {
            ShowTooltip("Failed to switch type");
            return;
        }

        _logger.LogInformation("Type switched for {TagMark}: {OldType} → {NewType}",
            currentTagMark, currentType, newType);
        ReloadSourceCache();
    }

    private void ToggleHelpPanel() {
        if (IsHelpPanelVisible)
            CloseHelpPanel();
        else
            ShowHelpPanel();
    }

    // ── List pipeline ────────────────────────────────────────────

    /// <summary>
    /// Reloads the SourceCache from the service with ALL tag marks (unfiltered).
    /// The DynamicData pipeline applies the current filter and sort automatically.
    /// </summary>
    private void ReloadSourceCache(string? selectItem = null) {
        _pendingSelectItem = selectItem;

        // Suppress per-emission SyncUiState calls so that all collection mutations
        // (EditDiff + sort signal) settle before we sync selection once.
        _isReloading = true;

        _snapshotComparer = _tagMarkUriService.TagMarkComparer;
        var allTagMarks = _tagMarkUriService.GetTagMarks(string.Empty);
        // Before the diff, so a clip added by this reload passes :clip at once.
        TrackClipTagMarks(allTagMarks);
        _tagMarksSource.EditDiff(allTagMarks, EqualityComparer<string>.Default);
        Expanded.RefreshExpandableTagMarks();
        TrackDuplicateUris();

        // Signal SortAndBind to re-evaluate order. Needed because EditDiff only handles
        // set membership — when sort keys change (e.g., LastOpened updated) but the set of
        // items is identical, EditDiff emits nothing. The signal forces a re-sort.
        // Only signal when the order is actually off: a re-sort resets the bound list, so the
        // ListBox rebuilds every row and loses its scroll position (TASK-64).
        if (!IsSorted(FilteredTagMarks, CreateSortComparer()))
            _sortComparer.OnNext(CreateSortComparer());

        _isReloading = false;
        SyncUiState();
    }

    // A new instance each time, so SortAndBind treats it as a comparer change and re-sorts.
    private IComparer<string> CreateSortComparer() {
        if (!_isDuplicateFilter)
            return Comparer<string>.Create((a, b) => _snapshotComparer.Compare(a, b));
        // :dup groups tagmarks sharing a URI under their owner, which sorts first in its group.
        var owners = DuplicateUriOwners;
        return Comparer<string>.Create((a, b) => {
            int byGroup = CompareNames(owners.GetValueOrDefault(a, a), owners.GetValueOrDefault(b, b));
            return byGroup != 0 ? byGroup : CompareNames(a, b);
        });
    }

    private static int CompareNames(string a, string b) {
        int byName = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a, b);
    }

    private static bool HasFilterToken(string? filter, string token) =>
        filter?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Sets the empty-state text for the tokens in the filter. Switches to the grouped :dup order,
    /// or back; re-sorts while :dup is applied, since groups may have changed.
    /// </summary>
    private void ApplyFilterTokens(string? filter) {
        bool isDuplicateFilter = HasFilterToken(filter, DuplicateFilterToken);
        // Set here, not in SyncUiState: an empty list that stays empty emits no changeset.
        // :dup wins over :clip: together they match nothing, since duplicates are URI-tagmarks only.
        EmptyStateText = isDuplicateFilter ? "No duplicate URIs"
            : HasFilterToken(filter, ClipFilterToken) ? "No clips"
            : DefaultEmptyStateText;
        if (!isDuplicateFilter && !_isDuplicateFilter) return;
        _isDuplicateFilter = isDuplicateFilter;
        _sortComparer.OnNext(CreateSortComparer());
    }

    private void TrackClipTagMarks(IEnumerable<string> names) {
        var clips = names.Where(name => _tagMarkUriService.GetTagMarkType(name) == TagMarkType.Clip).ToHashSet();
        if (!clips.SetEquals(_clipTagMarks.Value))
            _clipTagMarks.OnNext(clips);
    }

    private static bool IsSorted(IReadOnlyList<string> items, IComparer<string> comparer) {
        for (int i = 1; i < items.Count; i++)
            if (comparer.Compare(items[i - 1], items[i]) > 0)
                return false;
        return true;
    }

    /// <summary>
    /// Called by the DynamicData pipeline subscription after every changeset (filter, sort, add/remove).
    /// Syncs derived UI state: selection, counts, empty-state flags.
    /// </summary>
    private void SyncUiState() {
        string? targetItem = _pendingSelectItem ?? (IsExpanded ? Expanded.TagMark : SelectedTagMark);
        _pendingSelectItem = null;

        int restoredIndex = targetItem is not null
            ? FilteredTagMarks.IndexOf(targetItem)
            : -1;
        // If the target item still exists, select it. When an external deletion removes it,
        // keep the same index position (now pointing to the next item). For filter changes,
        // fall back to the first result per the design spec's filter selection rules.
        bool wasRemovedExternally = _preservePositionOnMiss && restoredIndex < 0 && targetItem is not null;
        SelectedTagMarkIndex = restoredIndex >= 0 ? restoredIndex
            : FilteredTagMarks.Count > 0
                ? (_preservePositionOnMiss
                    ? Math.Clamp(SelectedTagMarkIndex, 0, FilteredTagMarks.Count - 1)
                    : 0)
            : -1;
        _preservePositionOnMiss = false;
        if (wasRemovedExternally)
            ShowTooltip("TagMark was removed externally");

        // Eagerly sync SelectedTagMark/SelectedTagMarkInfo because the [Reactive] property won't fire
        // WhenAnyValue if the index didn't change numerically (e.g., 0 → 0 after collection reorder).
        UpdateSelectionDerivedState(SelectedTagMarkIndex >= 0 && SelectedTagMarkIndex < FilteredTagMarks.Count
            ? FilteredTagMarks[SelectedTagMarkIndex]
            : null);

        FilteredCount = FilteredTagMarks.Count;
        TotalCount = _tagMarksSource.Count;
        ApplyPendingExpandedNavigation();
    }

    private bool ComputeCanChangePayload()
    {
        string? tagMark = IsExpanded ? Expanded.TagMark : SelectedTagMark;
        if (tagMark is null) return false;
        string clip = CurrentClipboardContent;
        if (string.IsNullOrWhiteSpace(clip)) return false;
        string normalized = clip.ReplaceLineEndings("").Trim();
        bool newIsUri = Validation.IsValidUri(normalized);
        string effectivePayload = newIsUri ? normalized : clip;
        string currentPayload = _tagMarkUriService.GetPayload(tagMark).Value;
        if (effectivePayload == currentPayload) return false;
        if (newIsUri)
        {
            string? existingOwner = _tagMarkUriService.FindTagMarkByUri(normalized);
            if (existingOwner is not null && existingOwner != tagMark) return false;
        }
        else if (Validation.IsClipPayloadTooLong(clip)) return false;
        return true;
    }

    /// <summary>
    /// Builds a filter predicate matching SubstringSearchService semantics:
    /// every whitespace-delimited term must be a case-insensitive substring.
    /// The :dup token instead keeps only tagmarks that share a primary URI, and :clip only
    /// clip-tagmarks (spec §3.2).
    /// </summary>
    private Func<string, bool> BuildFilterPredicate(string? filter) {
        if (string.IsNullOrWhiteSpace(filter))
            return _ => true;

        var terms = filter
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .ToArray();

        if (terms.Length == 0)
            return _ => true;

        HashSet<string>? sharedUri = null;
        if (terms.Contains(DuplicateFilterToken)) {
            terms = terms.Where(t => t != DuplicateFilterToken).ToArray();
            var owners = DuplicateUriOwners;
            sharedUri = [..owners.Keys, ..owners.Values];
        }
        IReadOnlySet<string>? clips = null;
        if (terms.Contains(ClipFilterToken)) {
            terms = terms.Where(t => t != ClipFilterToken).ToArray();
            clips = _clipTagMarks.Value;
        }

        return item => {
            if (sharedUri is not null && !sharedUri.Contains(item))
                return false;
            if (clips is not null && !clips.Contains(item))
                return false;
            var lower = item.ToLowerInvariant();
            foreach (var term in terms) {
                if (!lower.Contains(term))
                    return false;
            }
            return true;
        };
    }

    // ── Deletes and undo ─────────────────────────────────────────

    private async Task DeleteTagMarkAsync(string item) {
        if (IsInAddMode) return;
        if (IsExpanded)
            CollapseExpanded();

        if (!await _tagMarkUriService.MoveToTrashAsync(item)) {
            _logger.LogWarning("Couldn't delete TagMark: {TagMark}", item);
            return;
        }

        // Select the item that takes the deleted one's place (or the previous one if it was last)
        // before removing it: the removal runs SyncUiState, which would otherwise fall back to
        // index 0 and make the view scroll to the top and back (spec §4.2).
        var remaining = FilteredTagMarks.Where(name => name != item).ToList();
        int nextIndex = Math.Min(SelectedTagMarkIndex, remaining.Count - 1);
        _pendingSelectItem = nextIndex >= 0 ? remaining[nextIndex] : null;
        // Remove by key from the source cache; DynamicData updates the bound collection.
        _tagMarksSource.RemoveKey(item);
        _pendingSelectItem = null;
        // No reload follows a delete, and the watcher finds nothing new: the other half of a
        // duplicate pair stops being one here (:dup, row marker).
        TrackDuplicateUris();

        // The service permanently deleted an older trashed copy with this name, so drop its entry (spec §4.2).
        var entry = new PendingUndo.TagMarkDelete(item);
        if (_undoStack.Contains(entry)) {
            var kept = _undoStack.Where(u => u != entry).Reverse().ToList();
            _undoStack.Clear();
            kept.ForEach(_undoStack.Push);
        }
        _undoStack.Push(entry);
        ShowUndoToast();

        _logger.LogInformation("TagMark soft-deleted: {TagMark}", item);
    }

    private async Task<bool> UndoTagMarkDeleteAsync(string tagMark) {
        bool restored = await _tagMarkUriService.RestoreFromTrashAsync(tagMark);
        if (restored) {
            ReloadSourceCache(selectItem: tagMark);
            _logger.LogInformation("TagMark restored from undo: {TagMark}", tagMark);
        }
        else if (_tagMarkUriService.TagMarkExists(tagMark)) {
            // The name was taken during the undo window. Startup recovery can't restore it
            // either (no overwrite), so delete it now instead of leaving it in the trash.
            _tagMarkUriService.PermanentlyDeleteFromTrash(tagMark);
            ShowTooltip($"Can't restore \"{tagMark}\": name already exists");
            _logger.LogWarning("Couldn't restore TagMark, name already exists: {TagMark}", tagMark);
        }
        else {
            // Left in the trash on purpose: startup recovery restores it.
            ShowTooltip("Failed to restore tagmark");
            _logger.LogWarning("Couldn't restore TagMark: {TagMark}", tagMark);
        }
        return restored;
    }

    private async Task<bool> UndoLinkRemovalAsync(PendingUndo.LinkRemoval link) {
        if (!await _tagMarkUriService.RestoreSecondaryUriAsync(link.TagMark, link.Uri, link.Index)) {
            ShowTooltip("Failed to restore link");
            _logger.LogWarning("Couldn't restore secondary URI {Uri} to {TagMark}", link.Uri, link.TagMark);
            return false;
        }
        _logger.LogInformation("Secondary URI restored from undo: {Uri} to {TagMark}", link.Uri, link.TagMark);

        Expanded.RefreshExpandableTagMarks();
        if (IsExpanded && Expanded.TagMark == link.TagMark) {
            Expanded.Refresh();
            Expanded.SelectLink(link.Uri);
        }
        return true;
    }

    private async Task DeleteExpandedEntryAsync() {
        if (Expanded.SelectedEntry is not { } entry) return;
        if (entry.IsPrimary) { // Cannot delete primary URI row
            ShowTooltip("Primary URI can't be removed");
            return;
        }
        if (Expanded.TagMark is null) return;

        string tagMark = Expanded.TagMark;
        int linkIndex = _tagMarkUriService.GetSecondaryUris(tagMark).ToList().IndexOf(entry.Uri);
        if (!await _tagMarkUriService.RemoveSecondaryUriAsync(tagMark, entry.Uri)) {
            _logger.LogWarning("Couldn't remove secondary URI {Uri} from {TagMark}", entry.Uri, tagMark);
            return;
        }
        _logger.LogInformation("Secondary URI removed: {Uri} from {TagMark}", entry.Uri, tagMark);
        _undoStack.Push(new PendingUndo.LinkRemoval(tagMark, entry.Uri, linkIndex));
        ShowUndoToast();

        // Refresh the expanded entries. With no links left there is nothing to expand (the ► marker
        // is gone too), so collapse even when the primary row remains (spec §5.3).
        Expanded.RefreshExpandableTagMarks();
        if (_tagMarkUriService.GetSecondaryUris(tagMark).Count == 0)
            CollapseExpanded();
        else
            Expanded.Refresh(); // the row below takes the highlight, or the one above if it was last
    }

    // ── Text sanitization ────────────────────────────────────────

    private const string InvalidInputTooltip = "Only file name compatible characters allowed";

    /// <summary>Signals that the VM replaced <see cref="TagMarkFilter"/> with its sanitized text.</summary>
    public IObservable<Unit> FilterTextSanitized => _filterTextSanitized;

    /// <summary>Signals that the VM replaced <see cref="RenameText"/> with its sanitized text.</summary>
    public IObservable<Unit> RenameTextSanitized => _renameTextSanitized;

    /// <summary>
    /// Whether typed <paramref name="text"/> may go into the filter textbox; shows a tooltip when not.
    /// Outside ADD ':' is allowed for filter tokens like :dup.
    /// </summary>
    public bool AcceptFilterInput(string text) =>
        AcceptInput(IsInAddMode ? Validation.HasValidFileNameChars(text) : Validation.HasValidFilterChars(text));

    /// <summary>Whether typed <paramref name="text"/> may go into the rename textbox; shows a tooltip when not.</summary>
    public bool AcceptRenameInput(string text) => AcceptInput(Validation.HasValidFileNameChars(text));

    private bool AcceptInput(bool valid) {
        if (!valid)
            ShowTooltip(InvalidInputTooltip);
        return valid;
    }

    private void SanitizeText(string? text, Func<string, SanitizeResult> sanitize, Action<string> apply,
        Subject<Unit> sanitized) {
        if (string.IsNullOrEmpty(text)) return;
        var result = sanitize(text);
        if (result.Text == text) return;

        apply(result.Text);
        sanitized.OnNext(Unit.Default);
        string tooltip = (result.CharsRemoved, result.Truncated) switch {
            (true, true)   => "Invalid characters removed and name trimmed to 255 characters",
            (true, false)  => "Invalid characters removed",
            _              => "Name trimmed to 255 characters"
        };
        ShowTooltip(tooltip);
    }

    // ── Notifications ────────────────────────────────────────────

    private void ShowTooltip(string message) => Notifications.ShowTooltip(message);

    /// <summary>
    /// Copies <paramref name="text"/> and shows the "Copied to clipboard" toast, or "Could not copy to clipboard"
    /// if the clipboard rejects it (spec §7 toast table, §11 #41).
    /// </summary>
    private bool CopyToClipboard(string text) {
        try {
            _clipboardService.SetText(text);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Copying to the clipboard failed");
            ShowFeedback("Could not copy to clipboard");
            return false;
        }
        ShowFeedback("Copied to clipboard");
        return true;
    }

    /// <summary>The toast for a URI the OS could not open (spec §11 #22), e.g. "Could not open URI (zoommtg://)".</summary>
    private static string CouldNotOpenUriMessage(string uri) {
        int colon = uri.IndexOf(':');
        string scheme = colon > 0 ? uri[..colon] : uri;
        return $"Could not open URI ({scheme}://)";
    }

    /// <summary>
    /// Shows a 2s result toast without an action (spec §7 toast table). While an undo is pending the undo toast
    /// stays, since replacing it would end the undo window, and the message falls back to a tooltip.
    /// </summary>
    private void ShowFeedback(string message) {
        if (HasPendingUndo)
            ShowTooltip(message);
        else
            Notifications.ShowToast(message, durationSeconds: 2);
    }

    /// <summary>True while a delete or link removal can still be undone, i.e. the undo toast is showing.</summary>
    public bool HasPendingUndo => _undoStack.Count > 0;

    /// <summary>
    /// Records that the global hotkey can't be used; a hint toast follows on the next window show (spec §3.1).
    /// </summary>
    public void ReportHotkeyUnavailable() => IsHotkeyUnavailable = true;

    /// <summary>
    /// Records that the <c>GlobalHotkey</c> setting is invalid and the default hotkey is used; a hint toast
    /// follows on the next window show (spec §3.1).
    /// </summary>
    public void ReportInvalidHotkeySetting() => IsHotkeySettingInvalid = true;

    /// <summary>
    /// Records that the config file at <paramref name="path"/> couldn't be read and the defaults are used; a hint
    /// toast follows on the next window show (spec §11 #55).
    /// </summary>
    public void ReportUnreadableConfigFile(string path) => UnreadableConfigFile = path;

    /// <summary>
    /// Shows a once-per-run startup hint (spec §3.1, §11 #55). While it is on screen the duplicate URI toast waits,
    /// so the hint isn't replaced on the show it appears on.
    /// </summary>
    private void ShowStartupHint(string message) {
        _startupHintMessage = message;
        Notifications.ShowToast(message, durationSeconds: 10);
    }

    private bool IsStartupHintVisible => Notifications.IsToastShowing(_startupHintMessage);

    private string HotkeyUnavailableMessage() =>
        _shortcutCatalog.IsMac
            ? $"Hotkey {_shortcutCatalog.Summon} unavailable: allow TagMarks in System Settings › Privacy & Security › Accessibility, then restart"
            : $"Hotkey {_shortcutCatalog.Summon} unavailable: open TagMarks from the tray icon";

    /// <summary>
    /// Picks up duplicate primary URIs the service found since the last reload. A fixed duplicate is
    /// forgotten, so it is reported again if it comes back.
    /// </summary>
    private void TrackDuplicateUris() {
        var duplicates = _tagMarkUriService.DuplicateUriOwners;
        if (!HaveSameEntries(DuplicateUriOwners, duplicates))
            DuplicateUriOwners = duplicates;
        _knownDuplicateUris.IntersectWith(duplicates.Keys);
        _unreportedDuplicateUris.IntersectWith(duplicates.Keys);
        foreach (var name in duplicates.Keys)
            if (_knownDuplicateUris.Add(name))
                _unreportedDuplicateUris.Add(name);
        ShowDuplicateUriToast();
    }

    private static bool HaveSameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var value) && value == e.Value);

    /// <summary>
    /// Shows the duplicate URI toast (spec §7 toast table) once the window is visible. It waits while
    /// an undo is pending, since replacing the undo toast would end the undo window.
    /// </summary>
    private void ShowDuplicateUriToast() {
        if (_unreportedDuplicateUris.Count == 0 || !IsWindowVisible || HasPendingUndo || IsStartupHintVisible) return;
        var duplicates = _tagMarkUriService.DuplicateUriOwners;
        var name = _unreportedDuplicateUris.Min(StringComparer.OrdinalIgnoreCase)!;
        int more = _unreportedDuplicateUris.Count - 1;
        _unreportedDuplicateUris.Clear();
        Notifications.ShowToast($"Duplicate URI in \"{name}\" (already saved as \"{duplicates[name]}\")"
                  + (more > 0 ? $" and {more} more" : string.Empty));
    }

    /// <param name="restored">The entry the last undo restored, if any (spec §7 toast table).</param>
    private void ShowUndoToast(PendingUndo? restored = null) {
        int count = _undoStack.Count;
        (string message, string action) = (restored, count, _undoStack.Peek()) switch {
            (PendingUndo.TagMarkDelete d, _, _) => ($"Restored \"{d.TagMark}\"  {count} still pending", "Undo next"),
            (PendingUndo.LinkRemoval l, _, _) => ($"Restored link in \"{l.TagMark}\"  {count} still pending", "Undo next"),
            (null, 1, PendingUndo.TagMarkDelete d) => ($"Deleted \"{d.TagMark}\"", "Undo"),
            (null, 1, PendingUndo.LinkRemoval l) => ($"Removed link from \"{l.TagMark}\"", "Undo"),
            _ when _undoStack.All(u => u is PendingUndo.TagMarkDelete) => ($"Deleted {count} tagmarks", "Undo last"),
            _ when _undoStack.All(u => u is PendingUndo.LinkRemoval) => ($"Removed {count} links", "Undo last"),
            _ => ($"Deleted {count} items", "Undo last")
        };
        Notifications.ShowToast(message, $"[{_shortcutCatalog.Undo}] {action}", durationSeconds: 5, onExpired: OnUndoToastExpired);
    }

    private void OnUndoToastExpired() {
        FinalizeAllPendingDeletes();
    }

    public void FinalizeAllPendingDeletes() {
        // Link removals are already on disk; dropping them just ends their undo window.
        while (_undoStack.Count > 0) {
            if (_undoStack.Pop() is PendingUndo.TagMarkDelete delete)
                _tagMarkUriService.PermanentlyDeleteFromTrash(delete.TagMark);
        }
        Notifications.DismissToast();
    }

    // ── State transitions ────────────────────────────────────────

    public void ResetToSearchState() {
        Notifications.HideTooltip();

        if (IsHelpPanelVisible)
            CloseHelpPanel();

        if (IsConfirmPanelVisible)
            ExitConfirmPanel();

        if (IsInAddMode)
            ExitAddMode(restoreFilter: true);

        if (IsInRenameMode)
            ExitRenameMode();

        if (IsExpanded)
            CollapseExpanded();

        ClearPendingExpandedNavigation();
        SyncUiState();
    }

    /// <summary>
    /// SEARCH → ADD. Ignored in any other state.
    /// </summary>
    public void EnterAddMode(string clipboardContent) {
        if (State != UiState.Search) return;
        PreAddFilterText = TagMarkFilter;
        string normalized = clipboardContent.ReplaceLineEndings("").Trim();
        bool isUri = Validation.IsValidUri(normalized);
        CapturedPayload = isUri ? normalized : clipboardContent;
        CapturedPayloadPreview = PayloadFormat.Preview(CapturedPayload);
        CapturedPayloadIsUri = isUri;
        CapturedPayloadTypeIcon = isUri ? "🔗 URI:" : "📋 Clip:";
        State = UiState.Add;

        string prefix = GenerateAutoTagPrefix(CapturedPayload);
        TagMarkFilter = prefix;
    }

    public bool TryEnterAddMode() {
        if (State != UiState.Search) return false;
        if (string.IsNullOrWhiteSpace(CurrentClipboardContent)) {
            ShowTooltip("Clipboard is empty");
            return false;
        }
        if (Validation.IsClipPayloadTooLong(CurrentClipboardContent)) {
            ShowTooltip("Clip text too long (max ~16,000 characters)");
            return false;
        }
        string normalized = CurrentClipboardContent.ReplaceLineEndings("").Trim();
        if (Validation.IsValidUri(normalized)) {
            string? existingTagMark = _tagMarkUriService.FindTagMarkByUri(normalized);
            if (existingTagMark is not null) {
                ShowTooltip($"URI already saved as \"{existingTagMark}\"");
                return false;
            }
        }
        EnterAddMode(CurrentClipboardContent);
        return true;
    }

    public void ExitAddMode(bool restoreFilter) {
        if (!IsInAddMode) return;
        State = UiState.Search;
        CapturedPayload = string.Empty;
        CapturedPayloadPreview = string.Empty;
        CapturedPayloadIsUri = false;
        CapturedPayloadTypeIcon = string.Empty;
        TagMarkFilter = restoreFilter ? PreAddFilterText : string.Empty;
    }

    /// <summary>
    /// SEARCH → RENAME. Ignored in any other state.
    /// </summary>
    public void EnterRenameMode(string tagMarkName) {
        if (State != UiState.Search) return;
        RenameOriginalName = tagMarkName;
        RenameText = tagMarkName;
        State = UiState.Rename;
    }

    public void ExitRenameMode() {
        if (!IsInRenameMode) return;
        State = UiState.Search;
        RenameText = string.Empty;
        RenameOriginalName = null;
        SyncUiState();
        if (_deferredRefreshFromWatcher) {
            _deferredRefreshFromWatcher = false;
            _preservePositionOnMiss = true;
            ReloadSourceCache();
        }
    }

    /// <summary>
    /// SEARCH → EXPANDED, or EXPANDED on another tagmark. Ignored in any other state.
    /// </summary>
    public void ExpandTagMark(string name) {
        if (!State.IsPrimary) return;
        State = UiState.Expanded;
        Expanded.Open(name);
    }

    /// <summary>
    /// EXPANDED → SEARCH. Ignored in any other state, including a panel open over EXPANDED.
    /// </summary>
    public void CollapseExpanded() {
        if (State != UiState.Expanded) return;
        State = UiState.Search;
        Expanded.Close();
    }

    private void NavigateToExpandedTagMark(string targetTagMark) {
        if (!_tagMarkUriService.TagMarkExists(targetTagMark)) {
            ShowTooltip("TagMark no longer exists");
            return;
        }

        _pendingSelectItem = targetTagMark;
        _pendingExpandedNavigationTarget = targetTagMark;

        TagMarkFilter = string.Empty;
        // The pipeline filters synchronously; when the list changes, its SyncUiState completes
        // the navigation. The call below covers a list the cleared filter leaves unchanged.
        _immediateFilter.OnNext(string.Empty);
        SyncUiState();
    }

    private void ApplyPendingExpandedNavigation() {
        if (_pendingExpandedNavigationTarget is null)
            return;

        if (!_tagMarkUriService.TagMarkExists(_pendingExpandedNavigationTarget)) {
            ClearPendingExpandedNavigation();
            ShowTooltip("TagMark no longer exists");
            return;
        }

        if (!FilteredTagMarks.Contains(_pendingExpandedNavigationTarget))
            return;

        string targetTagMark = _pendingExpandedNavigationTarget;
        _pendingExpandedNavigationTarget = null;
        // A target without links would expand to its primary row alone, so land on it in SEARCH.
        // Selection stayed pinned to the source while it was expanded, so select the target again.
        if (_tagMarkUriService.GetSecondaryUris(targetTagMark).Count == 0) {
            CollapseExpanded();
            _pendingSelectItem = targetTagMark;
        } else {
            ExpandTagMark(targetTagMark);
        }
        SyncUiState();
    }

    private void ClearPendingExpandedNavigation() {
        _pendingExpandedNavigationTarget = null;
    }

    /// <summary>
    /// SEARCH or EXPANDED → CONFIRM_PANEL, which applies <see cref="ConfirmPanelViewModel.Pending"/> on Enter.
    /// </summary>
    private void OpenConfirmPanel() {
        if (!State.IsPrimary) return;
        State = State.WithPanel(UiPanel.Confirm);
    }

    /// <summary>
    /// CONFIRM_PANEL → the invoking state.
    /// </summary>
    private void ExitConfirmPanel() {
        if (!IsConfirmPanelVisible) return;
        State = State.WithoutPanel();
        ConfirmPanel.Clear();
    }

    /// <summary>
    /// Opens HELP_PANEL from SEARCH or EXPANDED, with the invoking state's shortcuts listed first.
    /// No-op in ADD, RENAME and CONFIRM_PANEL. The invoking state stays as is underneath.
    /// </summary>
    public void ShowHelpPanel() {
        if (!State.IsPrimary) return;
        Notifications.HideTooltip();
        HelpSections = _shortcutCatalog.SectionsStartingWith(IsExpanded ? ShortcutScope.Expanded : ShortcutScope.Search);
        State = State.WithPanel(UiPanel.Help);
    }

    /// <summary>
    /// HELP_PANEL → the invoking state.
    /// </summary>
    private void CloseHelpPanel() {
        if (!IsHelpPanelVisible) return;
        State = State.WithoutPanel();
    }

    // ── Keys ─────────────────────────────────────────────────────

    /// <summary>
    /// Routes a key press through the key map (spec §3.4) and runs the command it maps to.
    /// </summary>
    /// <returns>The action, so the view can mark the key handled and run the view-only ones (help panel scrolling).</returns>
    public KeyAction HandleKey(KeyPress key) => Run(_keyMap.Route(State, key, HasPendingUndo));

    /// <summary>
    /// Routes typed text (TextInput) through the key map and runs the command it maps to.
    /// </summary>
    public KeyAction HandleTextInput(string? text) => Run(KeyMap.RouteText(State, text));

    private KeyAction Run(KeyAction action) {
        (ICommand Command, object? Parameter)? target = action switch {
            KeyAction.HideWindow => (HideUiCommand, null),
            KeyAction.MoveSelectionUp => (MoveSelectionCommand, -1),
            KeyAction.MoveSelectionDown => (MoveSelectionCommand, 1),
            KeyAction.LaunchSelected when SelectedTagMark is { } item => (LaunchTagMarkCommand, item),
            KeyAction.ExpandSelected => (ExpandSelectedCommand, null),
            KeyAction.EnterAddMode => (ToggleAddModeCommand, null),
            KeyAction.CancelAddMode => (CancelAddModeCommand, null),
            KeyAction.ConfirmAdd => (ConfirmAddCommand, null),
            KeyAction.EnterRenameMode => (EnterRenameModeCommand, null),
            KeyAction.ConfirmRename => (ConfirmRenameCommand, null),
            KeyAction.CancelRename => (CancelRenameCommand, null),
            KeyAction.DeleteSelected => (DeleteSelectedCommand, null),
            KeyAction.UndoDelete => (UndoDeleteCommand, null),
            KeyAction.CopyPayload => (CopyTagMarkUriCommand, null),
            KeyAction.AddLink => (AddLinkCommand, null),
            KeyAction.ChangePayload => (ChangePayloadCommand, null),
            KeyAction.SwitchType => (SwitchTypeCommand, null),
            KeyAction.CollapseExpanded => (CollapseExpandedCommand, null),
            KeyAction.MoveExpandedSelectionUp => (MoveExpandedSelectionCommand, -1),
            KeyAction.MoveExpandedSelectionDown => (MoveExpandedSelectionCommand, 1),
            KeyAction.LaunchExpandedEntry => (LaunchExpandedEntryCommand, null),
            KeyAction.NavigateExpandedLink => (NavigateExpandedLinkCommand, null),
            KeyAction.ConfirmPanelConfirm => (ConfirmPanelConfirmCommand, null),
            KeyAction.ConfirmPanelCancel => (CancelConfirmPanelCommand, null),
            KeyAction.ShowHelpPanel => (ToggleHelpPanelCommand, null),
            KeyAction.CloseHelpPanel => (CloseHelpPanelCommand, null),
            _ => null
        };
        // CanExecute is false while an async command is still running, so a repeated key doesn't start it twice.
        if (target is var (command, parameter) && command.CanExecute(parameter))
            command.Execute(parameter);
        return action;
    }

    // ── Window ───────────────────────────────────────────────────

    /// <summary>
    /// The window was summoned (global hotkey, tray, second instance) while possibly already visible:
    /// an open HELP_PANEL closes and the invoking state underneath is back.
    /// </summary>
    public void OnSummoned() {
        if (IsHelpPanelVisible)
            CloseHelpPanel();
        _ = RescanFolderAsync();
    }

    /// <summary>
    /// Catches changes the file watcher missed (sync clients, sleep, network drives). Changes found
    /// arrive through <see cref="ITagMarkUriService.TagMarksChanged"/>, like a file-watcher refresh.
    /// </summary>
    private async Task RescanFolderAsync() {
        try {
            await _tagMarkUriService.RefreshTagMarksAsync();
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Rescanning the tagmarks folder on show failed");
        }
    }

    /// <summary>
    /// The one place the selection-derived state is set: the selected tagmark and its info, read from
    /// one service snapshot. Null clears both.
    /// </summary>
    private void UpdateSelectionDerivedState(string? item) {
        SelectedTagMark = item;
        SelectedTagMarkInfo = item is null ? null : _tagMarkUriService.GetTagMarkInfo(item);
    }

    private static string BuildPayloadLabel(TagMarkPayload payload) {
        return payload switch {
            TagMarkPayload.Uri uri => $"🔗 {uri.Value}",
            TagMarkPayload.Clip clip => $"📋 {PayloadFormat.Preview(clip.Value)}",
            _ => string.Empty
        };
    }

    private string GenerateAutoTagPrefix(string clipboardContent) {
        string datePrefix = DateTime.Now.ToString("yyyy-MM-dd");

        if (!CapturedPayloadIsUri)
            return $"{datePrefix} clip ";

        string lowerUri = clipboardContent.ToLowerInvariant();
        var matchedTags = _configurationManager.Config.AutoTags
            .Where(tag => lowerUri.Contains(tag.ToLowerInvariant()))
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matchedTags.Count > 0
            ? $"{datePrefix} {string.Join(" ", matchedTags)} "
            : $"{datePrefix} ";
    }

    public ReadOnlyObservableCollection<string> FilteredTagMarks => _filteredTagMarks;

    [Reactive] public partial int SelectedTagMarkIndex { get; set; }
    [Reactive] public partial string? SelectedTagMark { get; private set; }

    [Reactive] public partial int FilteredCount { get; private set; }
    [Reactive] public partial int TotalCount { get; private set; }
    [ObservableAsProperty] public partial string FilterCountDisplay { get; }
    [ObservableAsProperty] public partial bool IsFilteredListEmpty { get; }
    /// <summary>The list is empty outside ADD and RENAME, where the list area shows something else.</summary>
    [ObservableAsProperty] public partial bool ShowEmptyState { get; }
    [Reactive] public partial string EmptyStateText { get; private set; } = DefaultEmptyStateText;
    /// <summary>Duplicate → owner for tagmarks sharing a primary URI (spec §11 #50); drives the row marker and :dup.</summary>
    [Reactive] public partial IReadOnlyDictionary<string, string> DuplicateUriOwners { get; private set; } = new Dictionary<string, string>();

    /// <summary>The clipboard text as last read; empty when the clipboard holds no text.</summary>
    [Reactive] public partial string CurrentClipboardContent { get; private set; } = string.Empty;
    public ReactiveCommand<Unit, string> RefreshClipboardCommand { get; }

    // Execute() ignores CanExecute, so a read still running is checked here, as InvokeCommand does for polling.
    public void RefreshClipboard() {
        if (!_isReadingClipboard)
            RefreshClipboardCommand.Execute().Subscribe();
    }

    // Reads off the UI thread: TextCopy starts xsel/xclip on Linux, and large contents take a while to copy.
    // A failed read keeps the last content.
    // The flag is set synchronously on subscribe; the command's IsExecuting arrives later, on its output scheduler.
    private IObservable<string> ReadClipboard() {
        _isReadingClipboard = true;
        return Observable.Start(() => _clipboardService.GetText() ?? string.Empty, RxSchedulers.TaskpoolScheduler)
            .Catch((Exception ex) => {
                _logger.LogWarning(ex, "Clipboard access failed");
                return Observable.Empty<string>();
            })
            .Finally(() => _isReadingClipboard = false);
    }

    /// <summary>
    /// The selected tagmark's URI and payload, read from one service snapshot, so a file-watcher refresh
    /// between reads cannot mix the URI of one version with the type of another. Null when nothing is selected.
    /// The selected-item properties below derive from it.
    /// </summary>
    [Reactive] public partial TagMarkInfo? SelectedTagMarkInfo { get; private set; }
    [ObservableAsProperty] public partial string CurrentTagMarkUri { get; }
    [ObservableAsProperty] public partial string PayloadLabel { get; }
    [ObservableAsProperty] public partial TagMarkType SelectedTagMarkType { get; }
    [ObservableAsProperty] public partial bool IsSelectedTagMarkUri { get; }
    [ObservableAsProperty] public partial bool CanSwitchSelectedToUri { get; }
    [ObservableAsProperty] public partial bool CanChangePayload { get; }

    public ReactiveCommand<string, Unit> LaunchTagMarkCommand { get; }
    public ReactiveCommand<string, Unit> DeleteTagMarkCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmAddCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoDeleteCommand { get; }

    public ReactiveCommand<Unit, Unit> HideUiCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyTagMarkUriCommand { get; }
    public ReactiveCommand<int, Unit> MoveSelectionCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleAddModeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAddModeCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> EnterRenameModeCommand { get; }
    public Interaction<Unit, Unit> HideUiInteraction { get; } = new();

    [Reactive] public partial string TagMarkFilter { get; internal set; }

    // ── Interaction state (spec §3.4) ────────────────────────────
    private UiState _state;

    /// <summary>
    /// The interaction state. Changed only by the transition methods (Enter*/Exit*, ExpandTagMark,
    /// CollapseExpanded, OpenConfirmPanel, ShowHelpPanel, CloseHelpPanel), which ignore a transition
    /// the current state doesn't allow. The mode flags below are derived from it.
    /// </summary>
    public UiState State {
        get => _state;
        private set {
            if (_state == value) return;
            _logger.LogDebug("State: {Previous} → {State}", _state, value);
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(CurrentMode));
            this.RaisePropertyChanged(nameof(ModeIcon));
            this.RaisePropertyChanged(nameof(IsInAddMode));
            this.RaisePropertyChanged(nameof(IsInRenameMode));
            this.RaisePropertyChanged(nameof(IsExpanded));
            this.RaisePropertyChanged(nameof(IsConfirmPanelVisible));
            this.RaisePropertyChanged(nameof(IsHelpPanelVisible));
        }
    }

    /// <summary>
    /// The mode shown by the filter's border and icon: EXPANDED and the panels show their invoking state's.
    /// </summary>
    public AppMode CurrentMode => State.Mode switch {
        UiMode.Add => AppMode.Add,
        UiMode.Rename => AppMode.Rename,
        _ => AppMode.Search
    };
    public string ModeIcon => CurrentMode switch {
        AppMode.Add => "＋",
        AppMode.Rename => "✏",
        _ => "🔍"
    };
    public bool IsInAddMode => State.Mode == UiMode.Add;
    public bool IsInRenameMode => State.Mode == UiMode.Rename;
    public bool IsExpanded => State.Mode == UiMode.Expanded;
    public bool IsConfirmPanelVisible => State.Panel == UiPanel.Confirm;
    public bool IsHelpPanelVisible => State.Panel == UiPanel.Help;

    [Reactive] public partial bool IsWindowVisible { get; set; }
    [Reactive] public partial bool IsHotkeyUnavailable { get; private set; }
    [Reactive] public partial bool IsHotkeySettingInvalid { get; private set; }
    [Reactive] public partial string? UnreadableConfigFile { get; private set; }
    [Reactive] public partial string CapturedPayload { get; private set; } = string.Empty;
    [Reactive] public partial string CapturedPayloadPreview { get; private set; } = string.Empty;
    [Reactive] public partial bool CapturedPayloadIsUri { get; private set; }
    [Reactive] public partial string CapturedPayloadTypeIcon { get; private set; } = string.Empty;
    [Reactive] public partial string PreAddFilterText { get; private set; } = string.Empty;

    [Reactive] public partial string RenameText { get; set; } = string.Empty;
    internal string? RenameOriginalName { get; private set; }

    // ── EXPANDED mode (state in Expanded) ────────────────────────
    public ReactiveCommand<Unit, Unit> ExpandSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> CollapseExpandedCommand { get; }
    public ReactiveCommand<int, Unit> MoveExpandedSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> LaunchExpandedEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> NavigateExpandedLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteExpandedEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyExpandedEntryUriCommand { get; }

    // ── CONFIRM_PANEL (content in ConfirmPanel) ──────────────────
    public ReactiveCommand<Unit, Unit> AddLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelConfirmPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangePayloadCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmPanelConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> SwitchTypeCommand { get; }

    // ── HELP_PANEL ───────────────────────────────────────────────
    [Reactive] public partial IReadOnlyList<ShortcutSection> HelpSections { get; private set; } = [];

    public ReactiveCommand<Unit, Unit> ToggleHelpPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseHelpPanelCommand { get; }

    // An entry on the undo stack: a tagmark in the trash, or a link removed at Index in its tagmark's link list.
    private abstract record PendingUndo {
        public sealed record TagMarkDelete(string TagMark) : PendingUndo;
        public sealed record LinkRemoval(string TagMark, string Uri, int Index) : PendingUndo;
    }
}
