namespace TagMarks.Desktop.Services;

/// <summary>
/// Static facade over <see cref="ShortcutCatalog.Current"/> for the context-menu hints (<c>x:Static</c> in XAML).
/// </summary>
public static class ContextMenuShortcuts
{
    private static ShortcutCatalog Catalog => ShortcutCatalog.Current;

    public static string Activate => Catalog.Activate;
    public static string Copy => Catalog.Copy;
    public static string Rename => Catalog.Rename;
    public static string ChangePayload => Catalog.ChangePayload;
    public static string SwitchType => Catalog.SwitchType;
    public static string AddLink => Catalog.AddLink;
    public static string Delete => Catalog.Delete;
    public static string NavigateLink => Catalog.NavigateLink;
    public static string DeleteEntry => Catalog.DeleteEntry;
}
