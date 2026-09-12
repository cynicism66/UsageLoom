namespace UsageLoom.Core;

// Identity and quota freshness are independent. Never restore this lease from a
// quota cache or persist it across process restarts.
public sealed class AttributionIdentity
{
    private readonly object gate=new();
    private string? account,home;
    private DateTimeOffset verifiedAt;
    public void Observe(string? key,string source,DateTimeOffset at)
    {
        lock(gate){account=string.IsNullOrWhiteSpace(key)?null:key;home=Path.GetFullPath(source);verifiedAt=at;}
    }
    public void Clear(){lock(gate){account=null;home=null;}}
    public bool NeedsRenewal(string source,DateTimeOffset now)
    {
        lock(gate)return string.IsNullOrWhiteSpace(account)||now<verifiedAt||now-verifiedAt>=TimeSpan.FromMinutes(3)||
            !string.Equals(home,Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase);
    }
    // Comparison only: an expired identity must never be used to assign usage.
    internal string? LastKnown(string source)
    {
        lock(gate)return string.Equals(home,Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase)?account:null;
    }
    public string? Get(string source,DateTimeOffset now)
    {
        lock(gate)return now>=verifiedAt&&now-verifiedAt<=TimeSpan.FromMinutes(5)&&
            string.Equals(home,Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase)?account:null;
    }
}
