"""Extract real production CPU bodies; never edit product files or read a game install."""
from pathlib import Path
import hashlib, json, re

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
OUT = HERE / 'obj'
OUT.mkdir(exist_ok=True)

def sha(data): return hashlib.sha256(data).hexdigest()
def read(path): return path.read_text(encoding='utf-8-sig')
def method(text, needle):
    start = text.index(needle); brace = text.index('{', start); level = 1; end = brace + 1
    while level:
        if text[end] == '{': level += 1
        elif text[end] == '}': level -= 1
        end += 1
    return text[start:end]

sources = {name: ROOT / path for name, path in {
    'display': 'src/Notes/ChartNoiseDisplay.cs', 'rendering': 'src/Notes/ChartNoiseRendering.cs',
    'presentation': 'src/Notes/ChartPresentation.cs', 'geometry': 'src/ChartVisuals/NoiseZones.cs',
    'warningHelper': 'src/Notes/NoiseWarningMaskDilation.cs'}.items()}
texts = {k:read(p) for k,p in sources.items()}
oracle = json.loads(read(HERE/'OldOracle.json'))
for name, item in oracle['methods'].items():
    assert sha(item['body'].encode()) == item['sha256'], 'Oracle body/hash mismatch: '+name
extracts = []
def get(text, needle, source):
    value = method(text, needle)
    extracts.append({'source':source, 'method':needle, 'sha256':sha(value.encode())})
    return value

parts = [read(HERE/'HarnessTypes.txt'), get(texts['geometry'], 'public static class NoiseZoneGeometry', 'geometry')]
base_header = '''public sealed class LABELRaster {
 public TestTimeline timeline=new TestTimeline(); public bool[] active=new bool[33],visible=new bool[33],warningZones=new bool[33];
 public System.Numerics.Vector2[] centers=new System.Numerics.Vector2[33];public List<System.Numerics.Vector2>[] cells=new List<System.Numerics.Vector2>[33];
 public TestStyle[] styles=new TestStyle[33];public double Time;public int GeometryVersion;
 private readonly List<Vector2> projected=new List<Vector2>();
 public bool HasActive=>visible.Any(v=>v);
 public bool HasFilledPixels;private byte[] solidMaskRow;private float[] edgeAx,edgeAy,edgeDx,edgeDy;private int projectedEdgeCount;
 public void RunActive(Camera camera,Matrix4x4 field,int width,int height,byte[] pixels,PresentationRect viewport,Color32[] stylePixels)=>FillActiveMask(camera,field,width,height,pixels,viewport,stylePixels);
 public void RunWarning(Camera camera,Matrix4x4 field,int width,int height,byte[] pixels,PresentationRect viewport)=>FillWarningMask(camera,field,width,height,pixels,viewport);
 public bool PointInside(int x,int y)=>InsideProjected(x,y);
 public void RunRaw(List<System.Numerics.Vector2> cell,int sensor,Camera camera,Matrix4x4 field,int width,int height,byte[] pixels,Color32[] stylePixels,PresentationRect viewport)=>RasterCell(cell,sensor,camera,field,width,height,pixels,stylePixels,viewport);
'''
core_needles = ['private System.Numerics.Vector2 ScalePoint(', 'internal void FillActiveMask(',
    'internal void FillWarningMask(', 'private void RasterCell(', 'private bool InsideProjected(']
for label in ('Old', 'New'):
    raster = base_header.replace('LABEL',label)
    if label=='New':
        raster += 'private readonly RasterSpanCache[] rasterSpanCaches = new RasterSpanCache[33];\n'
        needles = core_needles + ['internal bool TryGetUniformVisibleStyle(', 'internal bool ActiveMaskStateMatches(',
            'private void FillSolidMaskSpan(', 'private sealed class RasterSpanCache', 'private bool MatchesRasterCache(',
            'private bool CanCacheProjected(', 'private RasterSpanCache CaptureRasterCache(', 'private void ReplayRasterCache(']
        for needle in needles: raster += get(texts['display'], needle, 'display')+'\n'
        clear = get(texts['display'], 'public void Clear()', 'display')
        prefix = clear[:clear.index('Array.Clear(active')]
        raster += prefix.replace('public void Clear()', 'public void ClearCachesForTest()')+'}\n'
        raster += '''public bool ProbeMatch(int sensor,int w,int h)=>MatchesRasterCache(rasterSpanCaches[sensor],w,h);
 public int ProbeCount(){int n=0;foreach(var c in rasterSpanCaches)if(c!=null&&c.Valid)n++;return n;}
 public void ProbeX(int i,float x){edgeAx[i]=x;}public float ProbeGetX(int i)=>edgeAx[i];
'''
    else:
        raster = raster.replace(' public bool HasFilledPixels;private byte[] solidMaskRow;private float[] edgeAx,edgeAy,edgeDx,edgeDy;private int projectedEdgeCount;\n','')
        for needle in core_needles: raster += oracle['methods'][needle]['body']+'\n'
    parts.append(raster+'}\n')
    comp = '''public sealed class LABELCompositor {
 public LABELRaster owner;public Color32[] stylePixels;public byte[] maskBytes;public int width,height;
 private int[] styleQueue,styleDistance;private bool uniformStyleFill;private Color32 uniformStyleColor;
 public void Resize(int w,int h){width=w;height=h;maskBytes=new byte[w*h];stylePixels=new Color32[w*h];styleQueue=new int[w*h];styleDistance=new int[w*h];RESET}
 public void Update(Camera camera,Matrix4x4 field,PresentationRect viewport){STATEMENTS SpreadStyles();}
'''.replace('LABEL',label)
    if label=='Old':
        comp = comp.replace('private bool uniformStyleFill;private Color32 uniformStyleColor;','')
        comp = comp.replace('RESET','').replace('STATEMENTS','owner.FillActiveMask(camera,field,width,height,maskBytes,viewport,stylePixels);')
        comp += oracle['methods']['private void SpreadStyles(']['body']+'\n'
    else:
        statements = '''uniformStyleFill = owner.TryGetUniformVisibleStyle(out uniformStyleColor);
        owner.FillActiveMask(camera, field, width, height, maskBytes, viewport, uniformStyleFill ? null : stylePixels);'''
        assert statements in texts['display'], 'Prepare CPU fill call must remain the exact tested statements'
        comp = comp.replace('RESET','activeMaskValid=false;').replace('STATEMENTS',statements)
        comp += get(texts['display'], 'private void SpreadStyles(', 'display')+'\n'
        field_start = texts['display'].index('private bool activeMaskValid;')
        field_end = texts['display'].index('private RenderTexture compose;',field_start)
        fields = texts['display'][field_start:field_end]
        # These are the actual production cache fields; only the owner test-double type changes.
        comp += fields.replace('NoiseZoneDisplay','NewRaster')
        prepare = get(texts['display'], 'internal Material Prepare(NoiseZoneDisplay value, Camera camera, int outputWidth, int outputHeight,', 'display')
        start = prepare.index('Ensure(outputWidth, outputHeight);')+len('Ensure(outputWidth, outputHeight);')
        end = prepare.index('// Warning protection',start)
        cpu = prepare[start:end]
        assert 'bool rebuildActive' in cpu and 'sourceMask.LoadRawTextureData' in cpu
        extracts.append({'source':'display','method':'Prepare exact active CPU block','sha256':sha(cpu.encode())})
        comp += '''public readonly FakeTexture sourceStyles=new FakeTexture(),sourceMask=new FakeTexture();
 public bool PrepareCpu(NewRaster value,Camera camera,int outputWidth,int outputHeight,Matrix4x4 field,PresentationRect viewport){
 owner=value;if(owner==null||!owner.HasActive)return false;Ensure(outputWidth,outputHeight);
'''+cpu+'\nreturn rebuildActive;}\n'
        comp += '''private void Ensure(int outputWidth,int outputHeight){int w=Math.Max(1,outputWidth/4),h=Math.Max(1,outputHeight/4);if(w!=width||h!=height||maskBytes==null)Resize(w,h);}
'''
        release = get(texts['display'], 'private void ReleaseMasks()', 'display')
        cache_reset = re.findall(r'activeMask(?:Valid|Owner)\s*=\s*(?:false|null)\s*;', release)
        assert len(cache_reset)==2, 'ReleaseMasks must invalidate active owner/valid cache'
        comp += 'public void ReleaseCpu(){'+''.join(cache_reset)+'width=height=0;maskBytes=null;stylePixels=null;}\n'
        protect = get(texts['display'],'private void ProtectWarningBorder(', 'display')
        assert re.search(r'warningRunDilation\.Dilate\(\s*w\s*,\s*h\s*,\s*warningBytes\s*,\s*warningScratch\s*\)',protect), 'Wrong helper buffer/dim call'
        assert 'warningRunDilation.Clear()' in release, 'ReleaseMasks must release warning helper cache'
        helper_code = re.sub(r'//[^\n]*|/\*.*?\*/', '', texts['warningHelper'], flags=re.S)
        assert not re.search(r'\bZXing\s*[.;]', helper_code), 'PC helper should not gain an undeclared ZXing dependency'
    parts.append(comp+'}\n')

render=texts['rendering']
guard_one = re.search(r'internal bool CanDraw\(Camera camera\)\s*=>\s*(.*?);',render,re.S)
guard_two = re.search(r'internal bool CanDraw\(Camera camera, PresentationRect bounds, PresentationRect visible\)\s*=>\s*(.*?);',render,re.S)
assert guard_one and guard_two
new_draw = get(render,'internal bool TryDraw(', 'rendering')
assert re.search(r'if\s*\(!CanDraw\(camera, bounds, visible\)\)\s*return false;',new_draw)
outer=texts['presentation']; first=outer.index('RenderTexture noiseSource = null;'); tail=outer[first:outer.index('finally { if (noiseSource',first)]
area_helper=get(outer,'private bool TryGetNoiseArea(', 'presentation')
assert '!frame.Noise.CanDraw(camera)' in area_helper and 'bounds.HasArea && visible.HasArea' in area_helper
can_pos=tail.index('TryGetNoiseArea(pair.Key, frame, camera, out var bounds, out var visible)'); copy_pos=tail.index('Graphics.Blit(destination, noiseSource)')
assert can_pos<copy_pos, 'CanDraw/area guard must precede expensive frame copy'
parts.append('''public sealed class GuardDisplay {public bool HasActive;}
public sealed class GuardState {public bool Ready,captured;public Camera capturedCamera;public GuardDisplay Display=new GuardDisplay();
 public bool Old(Camera camera,PresentationRect bounds,PresentationRect visible)=>!('''+oracle['oldTryDrawRejectionExpression']+''');
 internal bool CanDraw(Camera camera)=>'''+guard_one.group(1)+''';
 internal bool CanDraw(Camera camera,PresentationRect bounds,PresentationRect visible)=>'''+guard_two.group(1)+''';
}
''')
(OUT/'Extracted.cs').write_text('\n'.join(parts),encoding='utf-8')
(OUT/'source-extracts.json').write_text(json.dumps({'sources':{k:{'path':str(p.relative_to(ROOT)).replace('\\','/'),'sha256':sha(p.read_bytes())} for k,p in sources.items()},
    'oracle':{'file':'OldOracle.json','sha256':sha((HERE/'OldOracle.json').read_bytes()),'origin':oracle['origin']},
    'methods':extracts,'scope':'Verbatim selected CPU bodies. Prepare active block is verbatim; texture uploads are recording doubles. Unity projection and geometry values use managed doubles. No native or full-game assertion.'},ensure_ascii=False,indent=2),encoding='utf-8')
print('Extracted current PC noise methods and verified helper/copy lifecycle hooks.')
