# Compares a pattern operation in OCCT with SpryCAD (see tests/SpryCad.Tests/OcctPrep.cs):
#   dotnet run --project tests/SpryCad.Tests -c Release -- occtprep samples/demos/spur-gear.sprt Teeth out 9
#   pip install cadquery-ocp   (in a venv)
#   python build/occt-compare.py out 5
"""The same pattern operation in OCCT: occt_bench.py DIR [runs]. DIR holds body.step, tool.step, transforms.txt, op.txt
(written by SpryCAD's `occtprep`). Times, with OCCT's parallel mode on: one Boolean with every copy as a tool, the
copies fused first then one Boolean (as SpryCAD does), and one copy at a time. Prints volume, faces, validity."""
import os, statistics, sys, time
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.gp import gp_Trsf
from OCP.BRepBuilderAPI import BRepBuilderAPI_Transform
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse
from OCP.TopTools import TopTools_ListOfShape
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE
from OCP.BRepCheck import BRepCheck_Analyzer

d = sys.argv[1]; runs = int(sys.argv[2]) if len(sys.argv) > 2 else 7

def read(p):
    r = STEPControl_Reader()
    assert r.ReadFile(p) == IFSelect_RetDone, p
    r.TransferRoots()
    return r.OneShape()

body = read(os.path.join(d, 'body.step'))
tool = read(os.path.join(d, 'tool.step'))
cut = open(os.path.join(d, 'op.txt')).read().strip() == 'cut'
mats = [list(map(float, l.split())) for l in open(os.path.join(d, 'transforms.txt')) if l.strip()]

def copies():
    res = []
    for m in mats:
        t = gp_Trsf()
        t.SetValues(*m)
        res.append(BRepBuilderAPI_Transform(tool, t, True).Shape())
    return res

def lst(shapes):
    l = TopTools_ListOfShape()
    for s in shapes: l.Append(s)
    return l

def boolean(args, tools, is_cut):
    op = BRepAlgoAPI_Cut() if is_cut else BRepAlgoAPI_Fuse()
    op.SetArguments(lst(args)); op.SetTools(lst(tools))
    op.SetRunParallel(True)
    op.Build()
    assert op.IsDone(), 'boolean failed'
    return op.Shape()

def one_shot():
    return boolean([body], copies(), cut)

def fuse_then():
    cs = copies()
    fused = boolean([cs[0]], cs[1:], False)
    return boolean([body], [fused], cut)

def one_at_a_time():
    s = body
    for c in copies(): s = boolean([s], [c], cut)
    return s

def faces(s):
    n = 0; e = TopExp_Explorer(s, TopAbs_FACE)
    while e.More(): n += 1; e.Next()
    return n

def volume(s):
    p = GProp_GProps(); BRepGProp.VolumeProperties_s(s, p); return p.Mass()

print(f"OCCT: {len(mats)} copies, {'cut' if cut else 'fuse'}; parallel mode on")
for name, fn in [('one Boolean, all copies as tools', one_shot), ('copies fused, then one Boolean', fuse_then), ('one copy at a time', one_at_a_time)]:
    times = []; res = None
    for r in range(runs + 1):
        t0 = time.perf_counter(); res = fn(); dt = (time.perf_counter() - t0) * 1000
        if r > 0: times.append(dt)
    print(f"  {name:34} median {statistics.median(times):8.1f} ms (min {min(times):7.1f}); volume {volume(res):.8f}, {faces(res)} faces, valid {BRepCheck_Analyzer(res).IsValid()}")
