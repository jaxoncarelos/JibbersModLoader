using Jibbers.Sdk;
using NMatrix = System.Numerics.Matrix4x4;

namespace JibbersAlpineVision.Rendering;

/// <summary>Pure matrix math, testable without the game.</summary>
public static unsafe class CameraMath
{
    /// <summary>
    /// Inverse of (GPU projection without the render-texture flip) * view, as Unity's column-major matrix.
    /// A column-major Unity matrix read row-major is its transpose, so (P V)^T = V^T P^T, and the
    /// inverse of that transpose is the transposed inverse, which is the column-major inverse.
    /// </summary>
    public static Matrix4x4 InverseViewProjection(Matrix4x4 gpuProjection, Matrix4x4 view)
    {
        var transposed = Read(view) * Read(gpuProjection);
        if (!NMatrix.Invert(transposed, out var inverse)) throw new InvalidOperationException("camera matrix is singular");
        var result = new Matrix4x4();
        var source = (float*)&inverse;
        for (var i = 0; i < 16; i++) result.M[i] = source[i];
        return result;
    }

    static NMatrix Read(Matrix4x4 m)
    {
        var result = new NMatrix();
        var target = (float*)&result;
        for (var i = 0; i < 16; i++) target[i] = m.M[i];
        return result;
    }
}
