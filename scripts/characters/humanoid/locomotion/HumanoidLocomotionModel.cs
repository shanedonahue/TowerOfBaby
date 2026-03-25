using Godot;
using TowerOfBaby.Characters.Humanoid.Rig;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

// Motion constants live outside the controller so the runtime pipeline stays focused on behavior.
internal static class HumanoidLocomotionModel
{
    public const float WalkRunTransitionDimensionlessSpeed = 0.52f;
    public const float FullRunDimensionlessSpeed = 0.92f;
    public const float LocomotionBlendSpeedRatio = 0.18f;
    public const float LocomotionBlendSharpness = 10.0f;
    public const float WalkFrequencyGain = 1.95f;
    public const float RunFrequencyGain = 2.7f;
    public const float MinStrideLengthRatio = 0.6f;
    public const float MaxWalkStrideLengthRatio = 1.0f;
    public const float MaxRunStrideLengthRatio = 1.55f;
    public const float WalkStanceFraction = 0.67f;
    public const float RunStanceFraction = 0.45f;
    public const float WalkStepClearanceRatio = 0.045f;
    public const float RunStepClearanceRatio = 0.1f;
    public const float IdleToWalkSpeedRatio = 0.08f;
    public const float SupportNormalSharpness = 12.0f;
    public const float StanceFootSharpness = 18.0f;
    public const float IdleFootSharpness = 10.0f;
    public const float SwingRetargetWindow = 0.18f;
    public const float SwingAdvanceExponent = 1.45f;
    public const float SwingRetractionRatio = 0.18f;
    public const float SwingExtraLiftRatio = 0.06f;
    public const float LateStanceReleaseStart = 0.52f;
    public const float LateStanceRetractionRatio = 0.09f;
    public const float LateStanceToeLiftRatio = 0.04f;
    public const float PelvisPositionSharpness = 10.0f;
    public const float PelvisRotationSharpness = 8.0f;
    public const float UpperBodySharpness = 7.0f;
    public const float MaxWalkTouchdownForwardRatio = 0.54f;
    public const float MaxRunTouchdownForwardRatio = 0.82f;
    public const float MaxWalkRearReachRatio = 0.2f;
    public const float MaxRunRearReachRatio = 0.3f;
    public const float MinRearReachRatio = 0.1f;
    public const float LateralReachRatio = 0.18f;
    public const float PelvisSupportShiftRatio = 0.18f;
    public const float PelvisSupportFollowRatio = 0.52f;
    public const float PelvisMaxForwardCorrectionRatio = 0.11f;
    public const float PelvisMaxLateralCorrectionRatio = 0.06f;
    public const float PelvisMaxVerticalCorrectionRatio = 0.08f;
    public const float PelvisSpeedCompressionRatio = 0.025f;
    public const float PelvisForwardBiasRatio = 0.04f;
    public const float PelvisRollFromHeight = 0.35f;
    public const float PelvisRollFromSupport = 0.08f;
    public const float PelvisPitchFromAccel = 0.08f;
    public const float TorsoLeanWalk = 0.08f;
    public const float TorsoLeanRun = 0.18f;
    public const float ArmSwingWalk = 0.18f;
    public const float ArmSwingRun = 0.92f;
    public const float AirLegHangRatio = 0.88f;
}

internal struct HumanoidGroundMotionFrame
{
    public Vector3 VisualOrigin;
    public Basis VisualBasis;
    public Vector3 TargetVelocityPlanar;
    public Vector3 VelocityPlanar;
    public Vector3 Forward;
    public Vector3 Right;
    public float Speed;
    public float SpeedRatio;
    public float RunBlend;
    public float CycleFrequencyHz;
    public float StrideLength;
    public float StepClearance;
    public float StanceFraction;
    public float ForwardAcceleration;
    public float LateralAcceleration;
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
