using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SinmaiAlpha.Notes.Libs;

// Verbatim old separable loop rule, independent of the new byte-run helper.
static void Reference(int w,int h,byte[] warningBytes,byte[] warningScratch)
{
    Array.Clear(warningScratch,0,warningScratch.Length);
    for(int y=0;y<h;y++)
        for(int x=0;x<w;x++)
            if(warningBytes[y*w+x]!=0)
                for(int nx=Math.Max(0,x-2);nx<=Math.Min(w-1,x+2);nx++)
                    warningScratch[y*w+nx]=255;
    Array.Clear(warningBytes,0,warningBytes.Length);
    for(int y=0;y<h;y++)
        for(int x=0;x<w;x++)
            if(warningScratch[y*w+x]!=0)
                for(int ny=Math.Max(0,y-2);ny<=Math.Min(h-1,y+2);ny++)
                    warningBytes[ny*w+x]=255;
}
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../.."));
string output=args.Length==0?Path.Combine(AppContext.BaseDirectory,"result.json"):args.Length==2&&args[0]=="--output"?Path.GetFullPath(args[1]):throw new ArgumentException("Usage: [--output result.json]");
string source=Path.GetFullPath(Path.Combine(root,"../../src/Notes/NoiseWarningMaskDilation.cs"));
var random=new Random(20261018);
var helper=new WarningRunDilation();
long checks=0,bytesCompared=0;
int cases=0,clearCalls=0;
void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
void Compare(int w,int h,byte[] input)
{
    int n=w*h;var a=(byte[])input.Clone();var b=(byte[])input.Clone();
    var sa=new byte[n+11];var sb=new byte[n+11];random.NextBytes(sa);Array.Copy(sa,sb,sa.Length);
    Reference(w,h,a,sa);helper.Dilate(w,h,b,sb);
    Check(a.AsSpan().SequenceEqual(b),$"output mismatch {w}x{h} case{cases}");
    Check(sa.AsSpan().SequenceEqual(sb),$"horizontal scratch mismatch {w}x{h} case{cases}");
    Check(b.All(v=>v==0||v==255),"output binary/tail-cleared");
    bytesCompared+=a.Length+sa.Length;cases++;
}
byte[] Input(int w,int h,Func<int,int,byte> f)
{
    var data=new byte[w*h+13];random.NextBytes(data);
    for(int y=0;y<h;y++)for(int x=0;x<w;x++)data[y*w+x]=f(x,y);
    return data;
}
try
{
    foreach(var size in new[]{(0,0),(0,1),(1,0),(0,17),(17,0)})Compare(size.Item1,size.Item2,Input(size.Item1,size.Item2,(_,_)=>0));
    // Exhaustive small masks cover both run edges, row tails and short sides.
    for(int w=1;w<=4;w++)for(int h=1;h<=4;h++)
    for(int mask=0;mask<(1<<(w*h));mask++)
        Compare(w,h,Input(w,h,(x,y)=>(byte)((mask&(1<<(y*w+x)))==0?0:((x+y)%2==0?1:173))));
    foreach(int w in new[]{1,2,3,5,7,17,31,33,127})
    foreach(int h in new[]{1,2,3,5,7,19,31})
    {
        foreach(byte fill in new byte[]{0,1,37,128,254,255})Compare(w,h,Input(w,h,(_,_)=>fill));
        foreach(var p in new[]{(0,0),(w-1,0),(0,h-1),(w-1,h-1),(w/2,h/2)})
            Compare(w,h,Input(w,h,(x,y)=>(byte)(x==p.Item1&&y==p.Item2?7:0)));
        Compare(w,h,Input(w,h,(x,y)=>(byte)((x+y)%2==0?0:99)));
        Compare(w,h,Input(w,h,(x,y)=>(byte)(x%5<=1||y%7==0?3:0)));
    }
    for(int k=0;k<4000;k++)
    {
        int w=random.Next(1,130),h=random.Next(1,134);
        var data=Input(w,h,(_,_)=>random.Next(5)==0?(byte)random.Next(1,256):(byte)0);
        Compare(w,h,data);
        if(k%97==0){helper.Clear();clearCalls++;Compare(w,h,data);}
    }
    foreach(var wh in new[]{(900,1564),(225,391),(1,1025),(1025,1),(17,31),(3,5),(0,9),(33,17)})
    foreach(byte fill in new byte[]{0,1,255})Compare(wh.Item1,wh.Item2,Input(wh.Item1,wh.Item2,(_,_)=>fill));

    // Same helper width capacity alternates from full resolution to tiny, then
    // Clear really releases its sole cached allocation and it rebuilds correctly.
    helper.Clear();clearCalls++;
    var row=typeof(WarningRunDilation).GetField("onesRow",BindingFlags.Instance|BindingFlags.NonPublic)!;
    Check(row.GetValue(helper)==null,"Clear releases cached row");
    var warm=Input(37,29,(x,y)=>(byte)(x<15&&y<10?27:0));
    var warmScratch=new byte[37*29+11];
    for(int i=0;i<20;i++)helper.Dilate(37,29,warm,warmScratch);
    long allocationBefore=GC.GetAllocatedBytesForCurrentThread();
    for(int i=0;i<100;i++)helper.Dilate(37,29,warm,warmScratch);
    long warmAllocated=GC.GetAllocatedBytesForCurrentThread()-allocationBefore;
    Check(warmAllocated==0,"no warm per-call managed allocation");

    // Short .NET10 Windows CPU timings only. They are not PC Unity/Mono game
    // FPS measurements and are not used to pass semantic checks.
    var timings=new List<object>();
    foreach(var entry in new[]{("blocks",900,1564),("full",900,1564),("checkerboard",257,193)})
    {
        var data=Input(entry.Item2,entry.Item3,(x,y)=>entry.Item1=="full"?(byte)255:entry.Item1=="checkerboard"?(byte)((x+y)%2*255):(byte)((x>100&&x<430&&y>200&&y<1000)||(x>600&&x<850&&y>900&&y<1450)?37:0));
        var a=new byte[data.Length];var b=new byte[data.Length];var sa=new byte[entry.Item2*entry.Item3+11];var sb=new byte[sa.Length];
        for(int i=0;i<2;i++){Array.Copy(data,a,data.Length);Reference(entry.Item2,entry.Item3,a,sa);Array.Copy(data,b,data.Length);helper.Dilate(entry.Item2,entry.Item3,b,sb);}
        double oldMs=0,newMs=0;
        for(int i=0;i<5;i++){
            Array.Copy(data,a,data.Length);var clock=Stopwatch.StartNew();Reference(entry.Item2,entry.Item3,a,sa);clock.Stop();oldMs+=clock.Elapsed.TotalMilliseconds;
            Array.Copy(data,b,data.Length);clock.Restart();helper.Dilate(entry.Item2,entry.Item3,b,sb);clock.Stop();newMs+=clock.Elapsed.TotalMilliseconds;
            Check(a.AsSpan().SequenceEqual(b)&&sa.AsSpan().SequenceEqual(sb),"timed outputs still identical");
        }
        timings.Add(new{pattern=entry.Item1,width=entry.Item2,height=entry.Item3,iterations=5,referenceMeanMs=oldMs/5,runHelperMeanMs=newMs/5});
    }
    var report=new{passed=true,utc=DateTime.UtcNow.ToString("O"),cases,assertions=checks,allBytesCompared=bytesCompared,bothOutputAndHorizontalScratchCompared=true,allArrayTailsCompared=true,non255NonzeroSupported=true,clearCalls,warmAllocatedBytes=warmAllocated,helperSource=source,helperSourceSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(source))),framework=RuntimeInformation.FrameworkDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),timings,scope="Offline exact old separable rule vs dependency-free PC byte-run helper, .NET10 CLR CPU only; not Unity/Mono gameplay FPS or all-hardware performance acceptance."};
    File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(report));
}
catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
