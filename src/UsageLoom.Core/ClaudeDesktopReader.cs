using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageLoom.Core;

/// <summary>Read-only, allowlisted Chromium blockfile cache adapter. No network or authentication access.</summary>
public sealed class ClaudeDesktopReader
{
    private const int MaxResponse=65536,MaxIndex=1048576;
    private readonly string root;
    private readonly CancellationToken cancellation;
    private readonly Stopwatch elapsed=Stopwatch.StartNew();
    private ClaudeDesktopReader(string root,CancellationToken cancellation){this.root=Path.GetFullPath(root);this.cancellation=cancellation;}
    public static IReadOnlyList<string> Discover()
    {
        var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]{Path.Combine(roaming,"Claude"),Path.Combine(local,"Packages","Claude_pzs8sxrjxfjjc","LocalCache","Roaming","Claude")}
            .Where(Directory.Exists).Where(p=>File.Exists(Path.Combine(p,"plan-usage-history.json"))||Directory.Exists(Path.Combine(p,"Cache")))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static ClaudeQuotaSnapshot Read(string? directory,string? scope,DateTimeOffset now,CancellationToken cancellation=default)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(directory))
            {
                var paths=Discover();
                if(paths.Count==0)return ClaudeQuotaSnapshot.Empty("notFound");
                if(paths.Count>1)return ClaudeQuotaSnapshot.Empty("multiplePaths");
                directory=paths[0];
            }
            if(!Path.IsPathFullyQualified(directory)||directory.StartsWith(@"\\")||!Directory.Exists(directory))return ClaudeQuotaSnapshot.Empty("notFound");
            var reader=new ClaudeDesktopReader(directory,cancellation);
            ClaudeQuotaSnapshot? history=null;
            if(File.Exists(Path.Combine(directory,"plan-usage-history.json")))
            {
                var data=reader.ReadWhole("plan-usage-history.json",2*MaxIndex);
                if(!data.AsSpan().SequenceEqual(reader.ReadWhole("plan-usage-history.json",2*MaxIndex)))throw new IOException("Changing history");
                history=ClaudeQuotaParser.History(data,directory,scope,now);
            }
            var index=Path.Combine("Cache","Cache_Data","index");
            if(File.Exists(Path.Combine(directory,index)))
            {
                // Retry once for cache rotation/concurrent writes; never modify or lock the source.
                for(var attempt=0;attempt<2;attempt++)
                {
                    try
                    {
                        var snapshots=reader.ReadBlockCache(now);
                        if(snapshots.Count>0)
                        {
                            var snapshot=ClaudeQuotaParser.Select(snapshots,scope);
                            var reconciled=history is null?snapshot:ClaudeQuotaParser.Reconcile(snapshot,history,scope);
                            if(reconciled.Status!="snapshot")return reconciled;
                            return reconciled with{History=ClaudeQuotaHistory.Normalize(
                                snapshots.SelectMany(ClaudeQuotaHistory.Observations).Concat(history?.History??[])
                                .Where(p=>p.Scope==reconciled.Scope))};
                        }
                        break;
                    }
                    catch(NotSupportedException){return ClaudeQuotaSnapshot.Empty("unsupported");}
                    catch(IOException)when(attempt==0){cancellation.ThrowIfCancellationRequested();}
                }
            }
            else if(Directory.Exists(Path.Combine(directory,"Cache","Cache_Data"))&&
                Directory.EnumerateFiles(Path.Combine(directory,"Cache","Cache_Data"),"*_0").Any())return ClaudeQuotaSnapshot.Empty("unsupported");
            if(history is not null)return history;
            return ClaudeQuotaSnapshot.Empty("waiting");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)when(ex is not OutOfMemoryException){return ClaudeQuotaSnapshot.Empty("readFailed");}
    }
    private void Check()
    {
        cancellation.ThrowIfCancellationRequested();
        if(elapsed.Elapsed>TimeSpan.FromSeconds(8))throw new IOException("Read deadline");
    }
    private string SafePath(string relative)
    {
        Check();var full=Path.GetFullPath(Path.Combine(root,relative));
        if(!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Path boundary");
        // Do not follow junctions/symlinks, including source roots and parents.
        for(string? current=full;current is not null;current=Path.GetDirectoryName(current))
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked cache path");
        return full;
    }
    private byte[] ReadRange(string relative,long offset,int length,int maximum=MaxIndex)
    {
        Check();if(offset<0||length<0||length>maximum)throw new IOException("Read bounds");
        using var file=new FileStream(SafePath(relative),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(offset>file.Length-length)throw new IOException("Short file");
        file.Position=offset;var result=new byte[length];file.ReadExactly(result);return result;
    }
    private byte[] ReadWhole(string relative,int maximum)
    {
        var length=new FileInfo(SafePath(relative)).Length;
        if(length>maximum)throw new IOException("Oversized file");
        return ReadRange(relative,0,checked((int)length),maximum);
    }
    private static uint U(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));
    private static int I(byte[] bytes,int at)=>BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at,4));
    private byte[] Address(uint address,int length,int extra=0)
    {
        if((address&0x80000000)==0)throw new IOException("Uninitialized address");
        var type=(address>>28)&7;var cache=Path.Combine("Cache","Cache_Data");
        if(type==0)return ReadRange(Path.Combine(cache,$"f_{address&0x0fffffff:x6}"),extra,length);
        var size=type switch{1=>36,2=>256,3=>1024,4=>4096,_=>throw new NotSupportedException()};
        var capacity=(((address>>24)&3)+1)*size;
        if(length<0||extra<0||(long)extra+length>capacity)throw new IOException("Block bounds");
        var file=Path.Combine(cache,$"data_{(address>>16)&255}");
        var header=ReadRange(file,0,20);
        if(U(header,0)!=0xC104CAC3||U(header,4)!=0x20000||I(header,12)!=size)throw new IOException("Block format");
        return ReadRange(file,8192L+(address&65535)*size+extra,length);
    }
    private List<ClaudeQuotaSnapshot> ReadBlockCache(DateTimeOffset now)
    {
        var indexPath=Path.Combine("Cache","Cache_Data","index");
        var header=ReadRange(indexPath,0,368);
        if(U(header,0)!=0xC103CAC3||U(header,4)!=0x30000)throw new NotSupportedException();
        var count=I(header,28);if(count==0)count=65536;
        if(count<1||count>262144||I(header,8)<0||I(header,8)>30000)throw new IOException("Index bounds");
        var table=ReadRange(indexPath,368,count*4);var seen=new HashSet<uint>();var result=new List<ClaudeQuotaSnapshot>();
        for(var bucket=0;bucket<count;bucket++)
        {
            Check();var address=U(table,bucket*4);
            while(address!=0)
            {
                if(!seen.Add(address)||seen.Count>30000)throw new IOException("Invalid index chain");
                if(((address>>28)&7)!=2)throw new NotSupportedException();
                var entry=Address(address,96);var next=U(entry,4);
                if(I(entry,20)==0)
                {
                    var keyLength=I(entry,32);if(keyLength<1||keyLength>32768)throw new IOException("Key bounds");
                    var keyBytes=U(entry,36)!=0?Address(U(entry,36),keyLength):Address(address,keyLength,96);
                    var scope=MatchScope(Encoding.UTF8.GetString(keyBytes),root);
                    if(scope is not null)
                    {
                        if(result.Count>=128)throw new IOException("Too many quota records");
                        if(I(entry,40)<1||I(entry,40)>MaxResponse||I(entry,44)<1||I(entry,44)>MaxResponse)throw new IOException("Response bounds");
                        var rankingAddress=U(entry,8);
                        if(((rankingAddress>>28)&7)!=1)throw new IOException("Missing ranking node");
                        var ranking=Address(rankingAddress,36);
                        if(I(ranking,28)!=0||U(ranking,24)!=address)throw new IOException("Dirty or mismatched quota entry");
                        // A changing or corrupt quota entry invalidates the read, rather than silently choosing an older one.
                        var headers=Address(U(entry,56),I(entry,40));
                        var http=Encoding.UTF8.GetString(headers);
                        if(!http.Contains("HTTP/1.1 200",StringComparison.Ordinal)&&!http.Contains("HTTP/2 200",StringComparison.Ordinal))throw new IOException("Quota status");
                        if(!DateTimeOffset.TryParse(Header(http,"date"),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var at))throw new IOException("Missing response date");
                        var body=Address(U(entry,60),I(entry,44));
                        var parsed=ClaudeQuotaParser.Parse(Decode(body,Header(http,"content-encoding")??"identity"),at,scope,now);
                        if(!entry.AsSpan().SequenceEqual(Address(address,96))||!ranking.AsSpan().SequenceEqual(Address(rankingAddress,36))||!body.AsSpan().SequenceEqual(Address(U(entry,60),I(entry,44)))||
                            !headers.AsSpan().SequenceEqual(Address(U(entry,56),I(entry,40))))throw new IOException("Changing entry");
                        result.Add(parsed);
                    }
                }
                address=next;
            }
        }
        if(!header.AsSpan().SequenceEqual(ReadRange(indexPath,0,368))||!table.AsSpan().SequenceEqual(ReadRange(indexPath,368,count*4)))throw new IOException("Changing index");
        return result;
    }
    internal static string? MatchScope(string cacheKey,string root)
    {
        // Chromium may prepend a partition key. Match the final exact HTTPS URL only.
        var match=Regex.Match(cacheKey,@"https://claude\.ai/api/organizations/([0-9a-fA-F-]{36})/usage(?:\?skip_spend=[01])?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
        return match.Success&&Guid.TryParse(match.Groups[1].Value,out var id)?ClaudeQuotaParser.ScopeKey(root,id.ToString()):null;
    }
    private static string? Header(string headers,string name)
    {
        var matches=Regex.Matches(headers,@"(?:\x00|\r?\n)"+name+@":\s*([^\x00\r\n]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
        if(matches.Count>1)throw new IOException("Duplicate header");
        return matches.Count==1?matches[0].Groups[1].Value.Trim():null;
    }
    internal static byte[] Decode(byte[] data,string encoding)
    {
        if(data.Length>MaxResponse)throw new InvalidDataException("Oversized response");
        if(encoding=="identity")return data;
        if(encoding=="zstd")
        {
            using var decoder=new ZstdSharp.Decompressor();var output=new byte[MaxResponse];
            var length=decoder.Unwrap(data.AsSpan(),output.AsSpan());return output[..length];
        }
        using var input=new MemoryStream(data,false);
        using Stream stream=encoding switch{"br"=>new BrotliStream(input,CompressionMode.Decompress),"gzip"=>new GZipStream(input,CompressionMode.Decompress),
            "deflate"=>new ZLibStream(input,CompressionMode.Decompress),_=>throw new NotSupportedException()};
        var buffer=new byte[MaxResponse+1];var read=0;
        while(read<buffer.Length){var size=stream.Read(buffer,read,buffer.Length-read);if(size==0)break;read+=size;}
        if(read>MaxResponse)throw new InvalidDataException("Oversized decoded response");return buffer[..read];
    }
}
