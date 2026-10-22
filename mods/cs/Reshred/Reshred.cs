using Jibbers.Sdk;
using Jibbers.Sdk.Features.Rewind;

namespace Reshred;

/// <summary>Public mod entry point. The SDK feature owns engine access and lifecycle cleanup.</summary>
public sealed class ReshredMod : FeatureMod<RewindController>
{
}
