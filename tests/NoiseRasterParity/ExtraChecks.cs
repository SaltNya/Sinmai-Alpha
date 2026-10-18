using UnityEngine;
using V=System.Numerics.Vector2;

// Uses extracted production control flow and a fresh pre-sync PC raster for
// every cached output comparison. These doubles never call Unity native code.
static class ExtraChecks
{
    public static long Run()
    {
        long assertions=0;
        void Check(bool ok,string message){assertions++;if(!ok)throw new Exception(message);}
        bool Equal(Color32 a,Color32 b)=>a.r==b.r&&a.g==b.g&&a.b==b.b&&a.a==b.a;
        NewRaster NewOwner()
        {
            var owner=new NewRaster();
            for(int i=0;i<33;i++){
                owner.cells[i]=new List<V>{new(.1f,.1f),new(.8f,.1f),new(.8f,.8f),new(.1f,.8f)};
                owner.centers[i]=new V(.45f,.45f);
                owner.styles[i]=new TestStyle{Color=new Color(.2f,.4f,.6f,.8f),Size=new Vector2(1,1)};
                owner.timeline.Starts[i]=0;owner.timeline.Ends[i]=2;
            }
            owner.active[0]=owner.visible[0]=owner.timeline.Selected[0]=true;owner.Time=.8;
            return owner;
        }
        OldRaster OldCopy(NewRaster n)
        {
            var old=new OldRaster{Time=n.Time,GeometryVersion=n.GeometryVersion};
            Array.Copy(n.active,old.active,33);Array.Copy(n.visible,old.visible,33);Array.Copy(n.warningZones,old.warningZones,33);
            Array.Copy(n.centers,old.centers,33);Array.Copy(n.cells,old.cells,33);Array.Copy(n.styles,old.styles,33);
            Array.Copy(n.timeline.Starts,old.timeline.Starts,33);Array.Copy(n.timeline.Ends,old.timeline.Ends,33);Array.Copy(n.timeline.Selected,old.timeline.Selected,33);
            return old;
        }
        var comp=new NewCompositor();var a=NewOwner();var b=NewOwner();var camera=new Camera();
        var field=new Matrix4x4{SX=1,SY=1};var viewport=new PresentationRect(0,0,1,1);
        int outputWidth=132,outputHeight=124;
        void Step(NewRaster owner,bool expectedRebuild,string label)
        {
            int before=comp.sourceMask.ByteUploads;bool rebuild=comp.PrepareCpu(owner,camera,outputWidth,outputHeight,field,viewport);
            Check(rebuild==expectedRebuild,label+" cache decision");
            Check(comp.sourceMask.ByteUploads-before==(expectedRebuild?1:0),label+" byte upload count");
            if(owner==null||!owner.HasActive)return;
            var fresh=new OldCompositor{owner=OldCopy(owner)};fresh.Resize(Math.Max(1,outputWidth/4),Math.Max(1,outputHeight/4));fresh.Update(camera,field,viewport);
            Check(fresh.maskBytes.AsSpan().SequenceEqual(comp.maskBytes),label+" old/new cached mask");
            Check(fresh.stylePixels.Zip(comp.stylePixels).All(p=>Equal(p.First,p.Second)),label+" old/new cached color/ownership");
        }
        Step(a,true,"cold");Step(a,false,"exact warm");a.Time=.9;Step(a,false,"steady time only");
        Step(b,true,"second owner");Step(a,true,"owner A-B-A");Step(a,false,"owner rewarm");
        a.GeometryVersion++;Step(a,true,"geometry version");Step(a,false,"geometry warm");
        field.TX=.0001f;Step(a,true,"field matrix");Step(a,false,"field warm");
        camera.worldToCameraMatrix.TX=.01f;Step(a,true,"view matrix");
        camera.projectionMatrix.SX=.75f;Step(a,true,"projection matrix");
        viewport=new PresentationRect(.1f,.2f,.8f,.7f);Step(a,true,"viewport");Step(a,false,"viewport warm");
        a.styles[0]=new TestStyle{Color=new Color(.3f,.4f,.6f,.8f),Size=new Vector2(1,1)};Step(a,true,"exact channel change");
        a.styles[0]=new TestStyle{Color=new Color(.3f,.4f,.6f,0),Size=new Vector2(1,1)};Step(a,true,"zero alpha change");
        a.styles[0]=new TestStyle{Color=new Color(.3f,.4f,.6f,.5f),Size=new Vector2(.5f,1)};Step(a,true,"size");
        a.timeline.Starts[0]=.05;Step(a,true,"selected span start");
        a.timeline.Ends[0]=2.3;Step(a,true,"selected span end");
        a.visible[1]=a.active[1]=a.timeline.Selected[1]=true;
        a.styles[1]=new TestStyle{Color=new Color(1,0,0,1),Size=new Vector2(.8f,.9f)};Step(a,true,"mixed first-writer owner");Step(a,false,"mixed warm");
        a.timeline.Selected[1]=false;Step(a,true,"visible but deselected");
        a.visible[1]=a.active[1]=false;Step(a,false,"irrelevant deselected visibility");
        a.Time=.1;Step(a,true,"enter transition");a.Time=.11;Step(a,true,"enter frame advances");Step(a,false,"same enter time");
        a.Time=.8;Step(a,true,"transition to stable");a.Time=.9;Step(a,false,"stable frame advances");
        a.Time=2.1;Step(a,true,"exit transition");a.Time=2.2;Step(a,true,"exit frame advances");
        a.Time=.8;Step(a,true,"clock rewind");Step(a,false,"rewind warm");
        a.timeline.Starts[0]=.75;a.timeline.Ends[0]=.95;Step(a,true,"short span");a.Time=.85;Step(a,false,"short non-transition advance");
        outputWidth=140;Step(a,true,"active dimensions change");Step(a,false,"new dimension warm");
        outputWidth=141;Step(a,false,"same quarter active dimensions");
        outputHeight=128;Step(a,true,"active height changes");
        // Empty raster with selected/visible metadata must overwrite old styles,
        // then be a valid warm cache result; it must not keep the prior scene.
        field.TX=10;Step(a,true,"offscreen empty");Check(comp.maskBytes.All(v=>v==0),"empty mask clears");
        Check(comp.stylePixels.All(v=>Equal(v,default)),"empty styles clear");Step(a,false,"empty warm");
        field.TX=0;Step(a,true,"empty to filled");comp.ReleaseCpu();Step(a,true,"release invalidates owner/mask");
        var noVisible=NewOwner();Array.Clear(noVisible.visible);Step(noVisible,false,"inactive owner early exit");Step(null,false,"missing owner early exit");
        // Direct state key must reject non-finite sizes even on repeated input.
        var v=new bool[33];var starts=new double[33];var ends=new double[33];var sizes=new Vector2[33];var colors=new Color32[33];
        a.styles[0]=new TestStyle{Color=new Color(1,1,1,1),Size=new Vector2(float.NaN,1)};
        Check(!a.ActiveMaskStateMatches(a.Time,v,starts,ends,sizes,colors,out _),"NaN size initial miss");
        Check(!a.ActiveMaskStateMatches(a.Time,v,starts,ends,sizes,colors,out _),"NaN size repeated miss");
        a.styles[0]=new TestStyle{Color=new Color(1,1,1,1),Size=new Vector2(1,float.PositiveInfinity)};
        Check(!a.ActiveMaskStateMatches(a.Time,v,starts,ends,sizes,colors,out _),"infinite size miss");
        // Exhaustive original TryDraw rejection vs shared CanDraw. This includes
        // missing/different/null camera identities and warning-only fields.
        var cam1=new Camera();var cam2=new Camera();
        foreach(bool ready in new[]{false,true})foreach(bool captured in new[]{false,true})foreach(bool active in new[]{false,true})
        foreach(var capturedCamera in new[]{null,cam1,cam2})foreach(var currentCamera in new[]{null,cam1,cam2})
        foreach(bool boundsArea in new[]{false,true})foreach(bool visibleArea in new[]{false,true}){
            var state=new GuardState{Ready=ready,captured=captured,capturedCamera=capturedCamera};state.Display.HasActive=active;
            var bounds=new PresentationRect(0,0,boundsArea?1:0,1);var visible=new PresentationRect(0,0,1,visibleArea?1:0);
            Check(state.Old(currentCamera,bounds,visible)==state.CanDraw(currentCamera,bounds,visible),"old/new camera/copy eligibility");
        }
        return assertions;
    }
}
