namespace SpryCad.Kernel;

/// <summary>A draft that cannot be made, with a message for the user.</summary>
public sealed class DraftFailedException(string message) : Exception(message);

/// <summary>Draft on exact bodies (<see cref="Native.NDraft"/>): faces tapered to a pull direction about a neutral plane.</summary>
public static class DraftOp
{
    /// <summary>
    /// The body with the named faces drafted. <paramref name="angle"/> in radians; negative: the part widens along
    /// <paramref name="pull"/>.
    /// </summary>
    public static Solid Apply(Solid body, ISet<string> faces, Plane neutral, Vec3 pull, double angle)
    {
        if (body.Native == null) throw new DraftFailedException("The body has no exact geometry to draft.");
        try
        {
            var nb = Native.NDraft.Apply(body.Native, f => faces.Contains(f.Tag.Name), neutral, pull, angle);
            var err = nb.Validate();
            if (err != null) throw new InvalidOperationException("draft: " + err);
            return Native.NTess.ToSolid(nb);
        }
        catch (InvalidOperationException ex)
        {
            var msg = ex.Message.StartsWith("draft: ") ? ex.Message["draft: ".Length..] : ex.Message;
            if (msg.Contains("no solution") || msg.StartsWith("invalid") || msg.Contains("open"))
                msg = "at this angle the drafted faces no longer meet their neighbours as they did (a face would vanish, or a fillet or " +
                      "tangent face next to them is not drafted with them). Try a smaller angle, include the tangent faces, or draft before filleting";
            throw new DraftFailedException("The draft cannot be made: " + msg + ".");
        }
    }
}
