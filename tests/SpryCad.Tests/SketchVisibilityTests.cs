using SpryCad.Core.Model;
using SpryCad.Core.Regen;

namespace SpryCad.Tests;

/// <summary>Sketches shown or hidden in the graphics area: automatic (while unused), as set by the user, rebuilding nothing.</summary>
static class SketchVisibilityTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Sketch visibility: shown while unused, as set otherwise, saved, nothing rebuilt", () =>
        {
            var doc = new CadDocument();
            var used = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(40, 20) });
            doc.AddFeature(new ExtrudeFeature { SketchId = used.Id, Depth = "10" });
            var free = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = CoreTests.Rect(10, 10) });
            var consumed = doc.ConsumedByActive();
            t.True(consumed.Contains(used.Id) && !consumed.Contains(free.Id), "the extrude uses its sketch");

            // automatic: the used sketch hidden, the free one shown
            t.True(!doc.SketchShown(used.Id, true), "used: hidden");
            t.True(doc.SketchShown(free.Id, false), "unused: shown");
            t.True(doc.SketchVisibility == null, "nothing set");

            // shown again to be reused, the free one hidden
            doc.SetSketchShown(used.Id, true, true);
            doc.SetSketchShown(free.Id, false, false);
            t.True(doc.SketchShown(used.Id, true) && !doc.SketchShown(free.Id, false), "as set");

            // kept through a save / load
            var doc2 = CadDocument.FromJson(doc.ToJson());
            t.True(doc2.SketchShown(used.Id, true) && !doc2.SketchShown(free.Id, false), "saved");

            // back to what it would be anyway: nothing kept
            doc.SetSketchShown(used.Id, false, true);
            doc.SetSketchShown(free.Id, true, false);
            t.True(doc.SketchVisibility == null, "back to automatic");

            // display only: showing a sketch rebuilds no feature
            var regen = new Regenerator();
            regen.Rebuild(doc);
            doc.SetSketchShown(used.Id, true, true);
            var r = regen.Rebuild(doc);
            t.Eq(r.Rebuilt, 0);
        });
    }
}
