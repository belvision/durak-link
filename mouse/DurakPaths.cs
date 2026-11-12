internal static class DurakPaths
{
    internal static readonly string Root = Path.GetFullPath(
        Environment.GetEnvironmentVariable("DURAK_LINK_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DurakLink"));
    internal static readonly string Cards = Path.Combine(Root, "cards");
    internal static readonly string Tests = Path.Combine(Root, "cards-tests");
    internal static readonly string Receiver = Path.GetFullPath(
        Environment.GetEnvironmentVariable("DURAK_UXPLAY_EXE")
        ?? Path.Combine(Root, "uxplay-windows-2.0.0.1736", "uxplay-windows.exe"));
}
