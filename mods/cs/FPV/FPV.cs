using Jibbers.Sdk;
using Jibbers.Sdk.Features.Fpv;

namespace Fpv;

/// <summary>Public mod entry point. The SDK feature owns engine access and lifecycle cleanup.</summary>
public sealed class FpvMod : FeatureMod<DroneController>
{
}
