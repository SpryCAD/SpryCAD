using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Sketch.Point and Sketch.Entity (indexed on larger sketches) against a plain scan, through edits.</summary>
static class SketchLookupTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Sketch point and entity lookups stay right as the lists are edited in place", () =>
        {
            var sk = new Sketch();
            for (int i = 0; i < 40; i++)
            {
                var a = sk.AddPoint(new Vec2(i, 0)); var b = sk.AddPoint(new Vec2(i, 1));
                sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            }
            void Check(string when)
            {
                for (int id = -2; id <= sk.NextId + 2; id++)
                {
                    var p = sk.Points.FirstOrDefault(x => x.Id == id);
                    var e = sk.Entities.FirstOrDefault(x => x.Id == id);
                    if (!ReferenceEquals(sk.Point(id), p) || !ReferenceEquals(sk.Entity(id), e))
                    {
                        t.True(false, $"{when}: id {id}");
                        return;
                    }
                }
            }
            Check("built");
            // removed, added, removed then added (same count), replaced at a place, reordered, list replaced, cloned
            sk.Points.RemoveAt(5); sk.Entities.RemoveAt(3); Check("removed");
            sk.AddPoint(new Vec2(7, 7)); sk.Add(new SkCircle { Center = sk.Points[1].Id, R = 2 }); Check("added");
            sk.Points.RemoveAt(10); sk.AddPoint(new Vec2(8, 8)); Check("same count");
            sk.Entities[4] = new SkLine { Id = sk.NewId(), P1 = sk.Points[2].Id, P2 = sk.Points[3].Id }; Check("replaced");
            sk.Points.Reverse(); sk.Entities.Reverse(); Check("reordered");
            sk.Points = sk.Points.Take(30).ToList(); Check("list replaced");
            sk.DeleteEntity(sk.Entities[0].Id); Check("entity deleted");
            var copy = sk.Clone(); sk.Points.Clear(); Check("cleared");
            sk = copy; Check("clone");
        });
    }
}
