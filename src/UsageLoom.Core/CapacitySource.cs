namespace UsageLoom.Core;

// Only source/identity configuration belongs to the sampling boundary.
// Refresh cadence, notifications, language and appearance do not.
public sealed record CapacitySource(string? Executable,string Home,bool Authorized,bool ReuseBackend)
{
    public bool Matches(CapacitySource other)=>Authorized==other.Authorized&&ReuseBackend==other.ReuseBackend&&
        SamePath(Executable,other.Executable)&&SamePath(Home,other.Home);
    private static bool SamePath(string? a,string? b)=>string.Equals(Normalize(a),Normalize(b),StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string? path)=>string.IsNullOrWhiteSpace(path)?"":Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
