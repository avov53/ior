namespace HappBox;

public static class WindowsPackagePolicy
{
    public static bool UseWindows10Compatibility(Version windows, bool markedWindows10Package) =>
        markedWindows10Package || windows.Major == 10 && windows.Minor == 0 && windows.Build < 22000;
}
