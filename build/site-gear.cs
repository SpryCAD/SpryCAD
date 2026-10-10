// The website's 3D model (site/img/gear.stl): the spur gear sample, rebuilt and exported as STL.
//   dotnet run build/site-gear.cs
// Run again when the sample or the geometry code changes how it looks.
#:project ../src/SpryCad.Core/SpryCad.Core.csproj
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "build", ".."));
var doc = CadDocument.Load(Path.Combine(root, "samples", "demos", "spur-gear.sprt"));
var r = new Regenerator().Rebuild(doc);
var path = Path.Combine(root, "site", "img", "gear.stl");
using (var fs = File.Create(path)) StlWriter.Write(fs, r.State.Bodies.Select(b => b.Solid));
Console.WriteLine($"{path}: {r.State.Bodies.Sum(b => b.Solid.Faces.Count)} faces, {new FileInfo(path).Length / 1024} KB");
