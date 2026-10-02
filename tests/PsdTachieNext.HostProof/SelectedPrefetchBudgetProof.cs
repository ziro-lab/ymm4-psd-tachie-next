using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;

namespace PsdTachieNext.HostProof;

internal sealed record SelectedBudgetResult(string Name,int Width,int Height,int VisibleLayers,int HiddenLayers,
    long OriginalPsdBytes,long AllCompiledRawBytes,long RequiredDecodedBytes,long PrefetchedDecodedBytes,
    long RealUseDecodedBytes,long CompilerCount,double ColdPrefetchMs,double RealCacheReuseMs,bool PrefetchReady,
    string? PrefetchError,bool PersistentCacheReused,bool Passed);

/// <summary>Size controls, not a survey of user/typical assets. Run off the host UI thread.</summary>
internal static class SelectedPrefetchBudgetProof
{
    internal static async Task<SelectedBudgetResult[]> Run(string output)
    {
        var results=new List<SelectedBudgetResult>();
        foreach(var spec in new[]{(Name:"one-visible-one-hidden",Width:1024,Height:2048,Visible:1,Hidden:1),
            (Name:"exact-16-mib",Width:2048,Height:2048,Visible:1,Hidden:0),
            (Name:"over-16-mib",Width:2048,Height:2049,Visible:1,Hidden:0),
            (Name:"two-visible-32-mib",Width:2048,Height:2048,Visible:2,Hidden:0)})
        {
            var root=Path.Combine(output,"budget-"+spec.Name);Directory.CreateDirectory(root);
            var path=Path.Combine(root,"synthetic-budget.psd");WritePsd(path,spec.Width,spec.Height,spec.Visible,spec.Hidden);
            var source=SourceAssetRef.Create(path);using var pool=new SharedDocumentPool(64L*1024*1024,4);
            using var preparation=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(root,"compiled-cache")));
            using var prefetch=new SelectedSourcePrefetch(preparation,pool,TimeSpan.FromSeconds(30),16L*1024*1024);
            var watch=Stopwatch.StartNew();await prefetch.Select(source);watch.Stop();
            var snapshot=prefetch.Snapshot();var compilerBefore=preparation.CompilationCount;
            long required,all,realBytes;var reuse=Stopwatch.StartNew();
            using(var asset=await preparation.PrepareAsync(source,preparation.Revision(path)))
            using(var real=asset.PrepareAppearance(pool))
            {
                required=real.Plan.RequiredBlockIds.Sum(id=>real.Plan.Manifest.Blocks[id].RawLength);
                all=real.Plan.Manifest.Blocks.Sum(block=>block.RawLength);realBytes=pool.Snapshot().ResidentDecodedBytes;
            }
            reuse.Stop();var expected=checked((long)spec.Width*spec.Height*4*spec.Visible);
            var within=expected<=16L*1024*1024;var cacheReused=preparation.CompilationCount==compilerBefore&&compilerBefore==1;
            var passed=required==expected&&realBytes==expected&&snapshot.Ready==within
                &&snapshot.RetainedDecodedBytes==(within?expected:0)&&cacheReused
                &&(within?snapshot.Error is null:snapshot.Error?.Contains(nameof(CacheCapacityException))==true);
            results.Add(new(spec.Name,spec.Width,spec.Height,spec.Visible,spec.Hidden,new FileInfo(path).Length,
                all,required,snapshot.RetainedDecodedBytes,realBytes,preparation.CompilationCount,
                watch.Elapsed.TotalMilliseconds,reuse.Elapsed.TotalMilliseconds,snapshot.Ready,snapshot.Error,cacheReused,passed));
            prefetch.Dispose();await prefetch.ShutdownCompletion;
        }
        return results.ToArray();
    }
    // Small independent RGB8 raw-channel writer. Rectangle sizes reflect each layer's full bounds.
    private static void WritePsd(string path,int width,int height,int visible,int hidden)
    {
        var pixels=checked(width*height);var layers=visible+hidden;
        using var records=new MemoryStream();
        for(var layer=0;layer<layers;layer++)
        {
            I32(records,0);I32(records,0);I32(records,height);I32(records,width);U16(records,4);
            foreach(short channel in new short[]{0,1,2,-1}){U16(records,unchecked((ushort)channel));U32(records,pixels+2);}
            Text(records,"8BIMnorm");records.WriteByte(255);records.WriteByte(0);
            records.WriteByte(layer>=visible?(byte)2:(byte)0);records.WriteByte(0);
            using var extra=new MemoryStream();U32(extra,0);U32(extra,0);
            var name=Encoding.ASCII.GetBytes("layer-"+layer);extra.WriteByte((byte)name.Length);extra.Write(name);
            while(extra.Length%4!=0)extra.WriteByte(0);U32(records,checked((int)extra.Length));extra.Position=0;extra.CopyTo(records);
        }
        var infoLength=checked(2+(int)records.Length+layers*4*(pixels+2));var pad=infoLength%2;
        using var file=File.Create(path);Text(file,"8BPS");U16(file,1);file.Write(new byte[6]);
        U16(file,4);U32(file,height);U32(file,width);U16(file,8);U16(file,3);U32(file,0);U32(file,0);
        U32(file,checked(4+infoLength+pad+4));U32(file,infoLength+pad);U16(file,layers);records.Position=0;records.CopyTo(file);
        var buffer=new byte[64*1024];
        void Plane(byte value){Array.Fill(buffer,value);for(var remaining=pixels;remaining>0;){var count=Math.Min(remaining,buffer.Length);file.Write(buffer,0,count);remaining-=count;}}
        for(var layer=0;layer<layers;layer++)foreach(var channel in new[]{0,1,2,3})
        {U16(file,0);Plane(channel==3?(byte)255:(byte)(50+layer*20+channel*30));}
        if(pad!=0)file.WriteByte(0);U32(file,0);U16(file,0);for(var channel=0;channel<4;channel++)Plane(0);
    }
    private static void Text(Stream stream,string value)=>stream.Write(Encoding.ASCII.GetBytes(value));
    private static void U16(Stream stream,int value){Span<byte> bytes=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(bytes,(ushort)value);stream.Write(bytes);}
    private static void U32(Stream stream,int value){Span<byte> bytes=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(bytes,(uint)value);stream.Write(bytes);}
    private static void I32(Stream stream,int value){Span<byte> bytes=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(bytes,value);stream.Write(bytes);}
}
