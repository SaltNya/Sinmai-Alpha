using System.Security.Cryptography;
using System.Text.Json;
using UnityEngine;
using V=System.Numerics.Vector2;

var random=new Random(510618);
long assertions=0,pixelsCompared=0;
int cases=0,uniformCases=0,mixedCases=0,emptyCases=0,warningCases=0,degenerateCases=0;
void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
bool Same(Color32 a,Color32 b)=>a.r==b.r&&a.g==b.g&&a.b==b.b&&a.a==b.a;
Color RandomColor()=>new Color(random.Next(256)/255f,random.Next(256)/255f,random.Next(256)/255f,random.Next(256)/255f);
List<V> Shape(int mode,float cx,float cy,float rx,float ry,int sides)
{
    if(mode==0)return new List<V>();
    if(mode==1)return new List<V>{new(cx,cy),new(cx+rx,cy)};
    if(mode==2)return new List<V>{new(cx-rx,cy),new(cx,cy),new(cx+rx,cy)};
    if(mode==3){degenerateCases++;return new List<V>{new(cx-rx,cy),new(cx+rx,cy),new(cx+rx,cy+1e-8f),new(cx-rx,cy+1e-8f)};}
    var result=new List<V>();
    for(int k=0;k<sides;k++){double a=2*Math.PI*k/sides;result.Add(new V(cx+rx*(float)Math.Cos(a),cy+ry*(float)Math.Sin(a)));}
    if(mode==4)result.Reverse();return result;
}
var oldRaster=new OldRaster();var newRaster=new NewRaster();
var oldComp=new OldCompositor{owner=oldRaster};var newComp=new NewCompositor{owner=newRaster};
void Run(int w,int h,int scenario,int iteration)
{
    cases++;
    if(oldComp.width!=w||oldComp.height!=h){oldComp.Resize(w,h);newComp.Resize(w,h);}
    bool monochrome=scenario%4!=0;
    Color common=RandomColor();
    int count=scenario==7?33:random.Next(1,10);
    for(int i=0;i<33;i++)
    {
        var center=new V((i%6)/5f,(i/6)/5f);
        oldRaster.centers[i]=newRaster.centers[i]=center;
        bool visible=i<count && (scenario==1 || random.Next(5)!=0);
        bool selected=visible && (scenario!=2 || i%2==0);
        oldRaster.visible[i]=newRaster.visible[i]=visible;
        oldRaster.active[i]=newRaster.active[i]=selected;
        oldRaster.timeline.Selected[i]=newRaster.timeline.Selected[i]=selected;
        // Exact transition boundaries, just inside/outside, and short no-transition spans.
        double start=(i%3)*.1,end=start+(scenario==3?.2:2.0);
        oldRaster.timeline.Starts[i]=newRaster.timeline.Starts[i]=start;
        oldRaster.timeline.Ends[i]=newRaster.timeline.Ends[i]=end;
        bool warning=i<count && random.Next(3)!=0;
        oldRaster.warningZones[i]=newRaster.warningZones[i]=warning;
        var color=monochrome?common:RandomColor();
        if(scenario==4&&i==count-1)color=new Color(color.r,color.g,color.b,0);
        var style=new TestStyle{Color=color,Size=new Vector2(scenario==5?(i%2==0?-1f:0f):.2f+random.NextSingle()*1.8f,.2f+random.NextSingle()*1.8f)};
        oldRaster.styles[i]=newRaster.styles[i]=style;
        int mode=scenario==6?iteration%4:4+random.Next(3);
        float cx=scenario==8?3f:random.NextSingle()*1.2f-.1f;
        float cy=scenario==8?3f:random.NextSingle()*1.2f-.1f;
        if(scenario==1){cx=.5f;cy=.5f;}// Complete overlap tests first-writer ownership.
        var cell=Shape(mode,cx,cy,.01f+random.NextSingle()*.6f,.01f+random.NextSingle()*.6f,3+random.Next(5));
        oldRaster.cells[i]=newRaster.cells[i]=cell;
    }
    double[] times={0,.0000001,.12,.239999,.24,.3,.8,1.759999,1.76,1.9,2,2.1};
    oldRaster.Time=newRaster.Time=times[iteration%times.Length];
    var camera=new Camera{SX=1,SY=iteration%7==0?-1f:1f,TX=0,TY=iteration%7==0?1f:0};
    var field=new Matrix4x4{SX=1,SY=1,TX=iteration%11==0?.123f:0,TY=0};
    var viewport=iteration%5==0?new PresentationRect(.12f,.08f,.76f,.84f):new PresentationRect(0,0,1,1);
    bool uniform=newRaster.TryGetUniformVisibleStyle(out _);
    if(uniform)uniformCases++;else mixedCases++;
    // Reuse buffers across calls. Any stale color left by the previous nonempty
    // frame must become zero when the new frame is clipped/degenerate/empty.
    oldComp.Update(camera,field,viewport);newComp.Update(camera,field,viewport);
    Check(oldComp.maskBytes.AsSpan().SequenceEqual(newComp.maskBytes),$"active mask {w}x{h} case{cases}");
    for(int i=0;i<oldComp.stylePixels.Length;i++)Check(Same(oldComp.stylePixels[i],newComp.stylePixels[i]),$"style pixel {i} case{cases}");
    bool hasPixels=oldComp.maskBytes.Any(b=>b!=0);
    Check(newRaster.HasFilledPixels==hasPixels,$"HasFilledPixels case{cases}");
    if(!hasPixels)emptyCases++;
    pixelsCompared+=oldComp.maskBytes.Length;
    // Null-style direct contract remains the old union, with no color buffer.
    byte[] directOld=new byte[w*h],directNew=new byte[w*h];
    oldRaster.RunActive(camera,field,w,h,directOld,viewport,null);
    newRaster.RunActive(camera,field,w,h,directNew,viewport,null);
    Check(directOld.AsSpan().SequenceEqual(directNew),$"direct null styles case{cases}");
    Check(newRaster.HasFilledPixels==directNew.Any(b=>b!=0),$"direct null HasPixels case{cases}");
    byte[] warningOld=new byte[w*h],warningNew=new byte[w*h];
    oldRaster.RunWarning(camera,field,w,h,warningOld,viewport);
    newRaster.RunWarning(camera,field,w,h,warningNew,viewport);
    Check(warningOld.AsSpan().SequenceEqual(warningNew),$"warning mask case{cases}");
    Check(warningNew.All(b=>b==0||b==255),$"binary warning case{cases}");
    pixelsCompared+=2L*w*h;warningCases++;
}
try
{
    // Tiny/one-dimensional/non-power-of-two sizes, changing capacity and row ends.
    foreach(var w in new[]{1,2,3,4,5,7,15,16,17,31,32,33})
    foreach(var h in new[]{1,2,3,5,9,17})
    for(int scenario=0;scenario<10;scenario++)
    for(int it=0;it<12;it++)Run(w,h,scenario,it);
    for(int k=0;k<1200;k++)Run(random.Next(1,130),random.Next(1,130),k%10,k);
    foreach(var size in new[]{(270,480),(480,270),(1080,1920),(1,1025),(1025,1),(17,31),(2,3)})
    for(int s=0;s<10;s++)Run(size.Item1,size.Item2,s,s);
    // Direct private-raster boundary contract: row fills must not cross the
    // clipped image end or touch guard bytes/struct elements beyond it.
    foreach(int w in new[]{1,2,3,5,31,33})
    foreach(int h in new[]{1,3,7})
    foreach(bool withStyles in new[]{false,true})
    {
        int n=w*h;var before=Enumerable.Repeat((byte)0xa7,n+17).ToArray();var after=(byte[])before.Clone();
        for(int i=0;i<n;i++)before[i]=after[i]=(byte)(i%3==0?255:0);
        var color=new Color32(19,29,39,49);var s1=Enumerable.Repeat(color,n+11).ToArray();var s2=(Color32[])s1.Clone();
        oldRaster.centers[0]=newRaster.centers[0]=new V(.5f,.5f);
        oldRaster.styles[0]=newRaster.styles[0]=new TestStyle{Color=new Color(1,0,0,1),Size=new Vector2(1,1)};
        var cell=new List<V>{new(-3,-3),new(4,-3),new(4,4),new(-3,4)};
        var camera=new Camera();var field=new Matrix4x4{SX=1,SY=1};var viewport=new PresentationRect(0,0,1,1);
        oldRaster.RunRaw(cell,0,camera,field,w,h,before,withStyles?s1:null,viewport);
        newRaster.RunRaw(cell,0,camera,field,w,h,after,withStyles?s2:null,viewport);
        Check(before.AsSpan().SequenceEqual(after),"direct raster clipped/tail bytes");
        Check(after.AsSpan(n).ToArray().All(b=>b==0xa7),"unchanged mask guard bytes");
        Check(s1.Zip(s2).All(p=>Same(p.First,p.Second)),"direct raster ownership struct elements");
        Check(s2.AsSpan(n).ToArray().All(v=>Same(v,color)),"unchanged style guard structs");
    }
    // Probe half-pixel boundaries on both windings and both signs of the
    // original 1e-5 float-cross epsilon. This also repeatedly shrinks/grows
    // the cached edge count, with no stale entries from the prior polygon.
    foreach(float delta in new[]{-.0001f,-.00001f,-.000001f,0f,.000001f,.00001f,.0001f})
    foreach(bool reverse in new[]{false,true})
    foreach(float thin in new[]{0f,.000001f,.001f,.25f,3f,17f})
    {
        int w=33,h=31;var cell=new List<V>{new((.5f+delta)/w,(.5f-delta)/h),new((25.5f+delta)/w,(.5f-delta)/h),new((25.5f+delta)/w,(.5f+thin-delta)/h),new((.5f+delta)/w,(.5f+thin-delta)/h)};
        if(reverse)cell.Reverse();
        oldRaster.centers[0]=newRaster.centers[0]=new V(0,0);
        oldRaster.styles[0]=newRaster.styles[0]=new TestStyle{Color=new Color(1,1,1,1),Size=new Vector2(1,1)};
        byte[] p1=new byte[w*h],p2=new byte[w*h];var cam=new Camera();var field=new Matrix4x4{SX=1,SY=1};var vp=new PresentationRect(0,0,1,1);
        oldRaster.RunRaw(cell,0,cam,field,w,h,p1,null,vp);newRaster.RunRaw(cell,0,cam,field,w,h,p2,null,vp);
        Check(p1.AsSpan().SequenceEqual(p2),"epsilon/winding mask");
        for(int y=-2;y<h+2;y++)for(int x=-2;x<w+2;x++)Check(oldRaster.PointInside(x,y)==newRaster.PointInside(x,y),"epsilon/winding InsideProjected");
    }
    // State-sequence replay uses the exact cache helpers and the exact Clear
    // data prefix. It reuses one owner/slot across hits, shape changes, colors,
    // sensor order, projection changes, warning/active sizes, and Clear.
    newRaster.ClearCachesForTest();
    Check(newRaster.ProbeCount()==0,"cold cache");
    int sequenceCases=0;
    for(int i=0;i<33;i++){
        oldRaster.centers[i]=newRaster.centers[i]=new V(0,0);
        oldRaster.styles[i]=newRaster.styles[i]=new TestStyle{Color=new Color(1,0,0,1),Size=new Vector2(1,1)};
    }
    var stable=new List<V>{new(0,0),new(.8f,0),new(.8f,.8f),new(0,.8f)};
    void Sequence(int w,int h,List<V> cell0,List<V> cell1,bool reverse,Camera cam)
    {
        var a=new byte[w*h];var b=new byte[w*h];var sa=new Color32[w*h];var sb=new Color32[w*h];
        var field=new Matrix4x4{SX=1,SY=1};var vp=new PresentationRect(0,0,1,1);
        newRaster.HasFilledPixels=false;
        foreach(int sensor in reverse?new[]{1,0}:new[]{0,1}){
            var cell=sensor==0?cell0:cell1;
            oldRaster.RunRaw(cell,sensor,cam,field,w,h,a,sa,vp);
            newRaster.RunRaw(cell,sensor,cam,field,w,h,b,sb,vp);
        }
        Check(a.AsSpan().SequenceEqual(b),"cache sequence masks");
        Check(sa.Zip(sb).All(p=>Same(p.First,p.Second)),"cache sequence current color/first-writer");
        Check(newRaster.HasFilledPixels==a.Any(v=>v!=0),"cache empty cannot erase previous HasPixels");
        sequenceCases++;
    }
    var empty=new List<V>{new(3,3),new(4,3),new(4,4),new(3,4)};
    Sequence(33,31,stable,empty,false,new Camera());
    Check(newRaster.ProbeMatch(1,33,31),"empty projected cache exact match");
    Sequence(33,31,stable,empty,false,new Camera());
    Sequence(33,31,stable,empty,false,new Camera());
    Check(newRaster.HasFilledPixels,"empty cache hit preserves earlier span");
    // Probe the actual matcher: wrong dims/count/coordinates cannot replay;
    // NaN and infinities always miss, and signed zero is not conflated.
    Sequence(33,31,empty,stable,false,new Camera());
    Check(newRaster.ProbeMatch(1,33,31),"warm cache matcher");
    Check(!newRaster.ProbeMatch(1,34,31)&&!newRaster.ProbeMatch(1,33,32),"dimension miss");
    float originalX=newRaster.ProbeGetX(0);
    Check(originalX==0f,"signed zero fixture");
    foreach(float bad in new[]{-0f,float.NaN,float.PositiveInfinity,float.NegativeInfinity,.0000001f}){
        newRaster.ProbeX(0,bad);Check(!newRaster.ProbeMatch(1,33,31),"exact/signed-zero/nonfinite miss");
    }
    newRaster.ProbeX(0,originalX);Check(newRaster.ProbeMatch(1,33,31),"restored exact key");
    foreach(int w in new[]{1,3,17,33,225})foreach(int h in new[]{1,7,31,391})
    for(int k=0;k<12;k++){
        var shifted=stable.Select(v=>v+new V(.01f*(k%3),.02f*(k%2))).ToList();
        var growing=NoiseZoneGeometry.GrowFromEdge(stable,stable[0],stable[1],k/11f);
        var shrinking=NoiseZoneGeometry.ShrinkToCenter(stable,new V(.4f,.4f),k/11f);
        oldRaster.styles[0]=newRaster.styles[0]=new TestStyle{Color=RandomColor(),Size=new Vector2(1,1)};
        oldRaster.styles[1]=newRaster.styles[1]=new TestStyle{Color=RandomColor(),Size=new Vector2(1,1)};
        foreach(var moving in new[]{stable,shifted,stable,growing,stable,shrinking,stable}){
            Sequence(w,h,moving,stable,k%2==0,new Camera());
            Sequence(w,h,moving,stable,k%2!=0,new Camera()); // exact hit, changed first writer
        }
        Sequence(w,h,stable,stable,false,new Camera{TX=.000001f});
        Sequence(w,h,stable,stable,false,new Camera());
    }
    // Multiple GrowFromEdge clips for one sensor, A->B->A, then degenerate and
    // nonconvex fallbacks must never expose a stale cached polygon.
    foreach(var strange in new[]{new List<V>{new(0,0),new(1,1),new(0,1),new(1,0)},new List<V>{new(0,0),new(.4f,.4f),new(.8f,.8f)},new List<V>{new(0,0),new(1,0),new(.2f,.2f),new(1,1),new(0,1)}}){
        Sequence(37,29,stable,stable,false,new Camera());
        Sequence(37,29,strange,strange,false,new Camera());
        Sequence(37,29,stable,stable,false,new Camera());
    }
    newRaster.ClearCachesForTest();Check(newRaster.ProbeCount()==0,"Clear releases all slots");
    Sequence(3,5,stable,stable,false,new Camera());
    Check(newRaster.ProbeCount()==2,"new cold slots after Clear");
    Check(uniformCases>0&&mixedCases>0&&emptyCases>0&&degenerateCases>0,"matrix coverage");
    // The runtime operation is non-generic Array.Copy on a struct array. Exercise
    // doubling with all channel patterns, alpha zero, and non-power-of-two tails.
    foreach(int length in new[]{0,1,2,3,7,31,257,129600})
    for(int channel=0;channel<256;channel++)
    {
        var data=new Color32[length];var color=new Color32((byte)channel,(byte)(255-channel),(byte)(channel^0xa5),(byte)(channel%2==0?0:255));
        if(length!=0){data[0]=color;for(int filled=1;filled<length;){int n=Math.Min(filled,length-filled);Array.Copy(data,0,data,filled,n);filled+=n;}}
        Check(data.All(v=>Same(v,color)),"Array.Copy struct doubling");
    }
    long lifecycleAssertions = ExtraChecks.Run(); assertions += lifecycleAssertions;
    var result=new{passed=true,lifecycleAssertions,assertions,cases,cacheSequenceCases=sequenceCases,pixelsCompared,uniformCases,mixedCases,emptyCases,warningCases,degenerateCases,sourceBodiesExtractedExactly=true,independentOldImplementation=true,nativeUnityOrGameExecuted=false,scope="Exact source CPU masks, styles, null-style contract, first-writer overlap, warning masks, binary output, HasPixels, projection flips/crops, enter/exit/short spans, degenerate/offscreen fields, size changes, cached reuse, explicit epsilon/half-pixel/reverse-winding/degenerate edge probes, exact-cache cold/hit/shape/dimension/projection/signed-zero/nonfinite/first-writer/Clear sequences and Color32 struct native-copy semantics under CLR. Projection/value types use explicit managed doubles; no native Unity projection, shader, HDD gameplay or performance acceptance is claimed. Separate cache and copy guards use extracted production CPU control flow."};
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"../../../obj/result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(result));
}
catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
