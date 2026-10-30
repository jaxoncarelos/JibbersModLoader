using Jibbers.Sdk;

namespace Slvsh.Integration;

/// <summary>
/// Moves a saved rider state from one spawn point to another: every free body (the rider's root,
/// and anything else floating free) is turned by the difference in facing and shifted to the new
/// spot, keeping the pose; it starts with the new spot's speed. MuJoCo is Z-up and right-handed,
/// Unity Y-up and left-handed: a Unity (x, y, z) is MuJoCo (x, z, y), and a Unity turn of θ about Y
/// is a MuJoCo turn of -θ about Z. Pure, so it is tested without the game.
/// </summary>
internal static class SpawnMove
{
    /// <summary>A free joint's place in the state: its 7 position values (x y z, quaternion w x y z) and its 6 speeds (linear, angular).</summary>
    internal readonly record struct FreeJoint(int Qpos, int Dof);

    /// <summary>
    /// <paramref name="state"/> starts with qpos (<paramref name="nq"/> values) then qvel. Returns a moved copy,
    /// or null if a joint lies outside it.
    /// </summary>
    public static double[]? Move(double[] state, int nq, IReadOnlyList<FreeJoint> joints, SpawnSpot from, SpawnSpot to)
    {
        var moved = (double[])state.Clone();
        var phi = -(to.Rotation.Y - from.Rotation.Y) * Math.PI / 180;
        double c = Math.Cos(phi), s = Math.Sin(phi);
        var (fx, fy, fz) = Mj(from.Position);
        var (tx, ty, tz) = Mj(to.Position);
        var (fvx, fvy, fvz) = Mj(from.Velocity);
        var (tvx, tvy, tvz) = Mj(to.Velocity);
        double qw = Math.Cos(phi / 2), qz = Math.Sin(phi / 2);
        foreach (var j in joints)
        {
            if (j.Qpos < 0 || j.Qpos + 7 > nq || j.Dof < 0 || nq + j.Dof + 6 > state.Length) return null;
            var p = j.Qpos;
            // Position: around the old spot, then to the new one.
            double dx = state[p] - fx, dy = state[p + 1] - fy;
            moved[p] = tx + c * dx - s * dy;
            moved[p + 1] = ty + s * dx + c * dy;
            moved[p + 2] = state[p + 2] - fz + tz;
            // Orientation: the turn about Z, then the old orientation.
            double w = state[p + 3], x = state[p + 4], y = state[p + 5], z = state[p + 6];
            moved[p + 3] = qw * w - qz * z;
            moved[p + 4] = qw * x - qz * y;
            moved[p + 5] = qw * y + qz * x;
            moved[p + 6] = qw * z + qz * w;
            // Speed: this spot's speed in place of the old spot's (the angular part is in the body's own frame).
            var v = nq + j.Dof;
            double vx = state[v] - fvx, vy = state[v + 1] - fvy;
            moved[v] = tvx + c * vx - s * vy;
            moved[v + 1] = tvy + s * vx + c * vy;
            moved[v + 2] = state[v + 2] - fvz + tvz;
        }
        return moved;
    }

    static (double X, double Y, double Z) Mj(Vector3 unity) => (unity.X, unity.Z, unity.Y);
}
