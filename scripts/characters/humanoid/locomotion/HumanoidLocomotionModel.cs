using Godot;
using TowerOfBaby.Characters.Humanoid.Rig;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

// Keep the first-pass walker small and explicit so stepping problems are easy to diagnose.
internal static class HumanoidLocomotionModel
{
    public const float IdleToWalkSpeedRatio = 0.08f;
    public const float RunStartSpeedRatio = 0.72f;
    public const float LocomotionBlendSharpness = 8.0f;
    public const float WalkStepDurationSeconds = 0.56f;
    public const float RunStepDurationSeconds = 0.36f;
    public const float WalkStepLengthRatio = 0.28f;
    public const float RunStepLengthRatio = 0.58f;
    public const float WalkStepHeightRatio = 0.08f;
    public const float RunStepHeightRatio = 0.12f;
    public const float StepTriggerDistanceRatio = 0.22f;
    public const float StepTriggerLateralRatio = 0.12f;
    public const float SwingRetargetWindow = 0.22f;
    public const float SupportNormalSharpness = 10.0f;
    public const float StanceFootSharpness = 22.0f;
    public const float IdleFootSharpness = 12.0f;
    public const float PelvisPositionSharpness = 10.0f;
    public const float PelvisRotationSharpness = 8.0f;
    public const float UpperBodySharpness = 7.0f;
    public const float MaxWalkTouchdownForwardRatio = 0.42f;
    public const float MaxRunTouchdownForwardRatio = 0.72f;
    public const float RearReachRatio = 0.24f;
    public const float LateralReachRatio = 0.18f;
    public const float PelvisSupportShiftRatio = 0.1f;
    public const float PelvisSpeedCompressionRatio = 0.02f;
    public const float PelvisForwardBiasRatio = 0.025f;
    public const float PelvisRollFromHeight = 0.12f;
    public const float PelvisRollFromSupport = 0.05f;
    public const float PelvisPitchFromSpeed = 0.05f;
    public const float TorsoLeanWalk = 0.06f;
    public const float TorsoLeanRun = 0.12f;
    public const float ArmSwingWalk = 0.22f;
    public const float ArmSwingRun = 0.5f;
    public const float AirLegHangRatio = 0.84f;
}

internal struct HumanoidGroundMotionFrame
{
    public Vector3 VisualOrigin;
    public Basis VisualBasis;
    public Vector3 VelocityPlanar;
    public Vector3 Forward;
    public Vector3 Right;
    public float Speed;
    public float SpeedRatio;
    public float RunBlend;
    public float StepDurationSeconds;
    public float StepLength;
    public float StepHeight;
    public float DesiredForwardInfluence;
}

// Runtime leg state is isolated here so future quadruped or custom-gait controllers can reuse the same pattern.
internal sealed class HumanoidLegMotionRuntime
{
    public HumanoidLegRig Rig { get; }
    public MotionChainDefinition Chain { get; }
    public MotionContactDefinition Contact { get; }
    public Vector3 HipOffsetFromPelvisLocal { get; }
    public Vector3 RestSupportPointLocal { get; }
    public float PhaseOffset { get; }

    public bool Initialized;
    public bool IsInStance = true;
    public bool WasInStance = true;
    public float StanceProgress;
    public float SwingProgress = 1.0f;
    public Vector3 PlantedSupportWorld = Vector3.Zero;
    public Vector3 CurrentSupportWorld = Vector3.Zero;
    public Vector3 SwingStartWorld = Vector3.Zero;
    public Vector3 SwingTargetWorld = Vector3.Zero;
    public Vector3 GroundNormalWorld = Vector3.Up;
    public Vector3 TargetGroundNormalWorld = Vector3.Up;

    public HumanoidLegMotionRuntime(
        HumanoidLegRig rig,
        MotionChainDefinition chain,
        MotionContactDefinition contact,
        Vector3 hipOffsetFromPelvisLocal,
        Vector3 restSupportPointLocal,
        float phaseOffset)
    {
        Rig = rig;
        Chain = chain;
        Contact = contact;
        HipOffsetFromPelvisLocal = hipOffsetFromPelvisLocal;
        RestSupportPointLocal = restSupportPointLocal;
        PhaseOffset = phaseOffset;
    }
}
