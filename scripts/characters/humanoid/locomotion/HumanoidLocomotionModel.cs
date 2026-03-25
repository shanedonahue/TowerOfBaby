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
    public const float ComEstimatePelvisBlend = 0.72f;
    public const float CapturePointLateralScale = 0.35f;
    public const float CapturePointPlacementGain = 0.55f;
    public const float CapturePointLateralGain = 0.18f;
    public const float CapturePointBiasClampRatio = 0.24f;
    public const float ComReleaseDistanceRatio = 0.16f;
    public const float RearReleaseSaturationThreshold = 0.88f;
    public const float RearReleaseHysteresis = 0.14f;
    public const float SupportSeekLookaheadWalkSeconds = 0.08f;
    public const float SupportSeekLookaheadRunSeconds = 0.16f;
    public const float SupportSeekBalanceBlend = 0.42f;
    public const float SwingCatchupScale = 0.42f;
    public const float FootContactInsetRatio = 0.08f;
    public const float HeelStrikeExitWeight = 0.12f;
    public const float ToeOffEnterWeight = 0.22f;
    public const float HeelStrikeWindowRatio = 0.28f;
    public const float HeelStrikePitchRadians = 0.24f;
    public const float ToeOffPitchRadians = 0.32f;
    public const float HeelPivotRatio = 0.12f;
    public const float ToePivotRatio = 0.18f;
    public const float ToeOffStart = 0.58f;
    public const float ToeOffSupportForwardRatio = 0.08f;
    public const float ToeOffNominalBlend = 0.18f;
    public const float ToeOffStickinessFactor = 0.42f;
    public const float PelvisSupportShiftRatio = 0.1f;
    public const float PelvisSpeedCompressionRatio = 0.02f;
    public const float PelvisForwardBiasRatio = 0.025f;
    public const float PelvisPushOffRatio = 0.04f;
    public const float PelvisRollFromHeight = 0.12f;
    public const float PelvisRollFromSupport = 0.05f;
    public const float PelvisPitchFromSpeed = 0.05f;
    public const float TorsoLeanWalk = 0.06f;
    public const float TorsoLeanRun = 0.12f;
    public const float ArmSwingWalk = 0.22f;
    public const float ArmSwingRun = 0.5f;
    public const float AirLegHangRatio = 0.84f;
}

internal enum HumanoidStanceFootPhase
{
    HeelStrike = 0,
    FootFlat = 1,
    ToeOff = 2
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
    public Vector3 SupportCenter;
    public Vector3 PlanarCom;
    public Vector3 BalanceTarget;
    public Vector3 BalanceError;
    public float SupportHeight;
    public float ComHeight;
    public float BalanceErrorForward;
    public float BalanceErrorLateral;
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
    public float StanceTimeSeconds = HumanoidLocomotionModel.WalkStepDurationSeconds;
    public float HeelStrikeWeight;
    public float ToeOffWeight;
    public float RearReachSaturation;
    public float RearReachDistance;
    public bool RearReleaseArmed;
    public float ComTrailDistance;
    public float PlannedTouchdownBias;
    public float BalanceTouchdownBias;
    public HumanoidStanceFootPhase StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
    public Vector3 HeelContactLocal { get; }
    public Vector3 ToeContactLocal { get; }
    public Vector3 HeelContactWorld = Vector3.Zero;
    public Vector3 ToeContactWorld = Vector3.Zero;
    public Vector3 FootPivotWorld = Vector3.Zero;
    public Vector3 ActiveSupportOffsetLocal = Vector3.Zero;
    public Basis FootBasisWorld = Basis.Identity;
    public float FootSkateDistance;
    public Vector3 LastStancePivotWorld = Vector3.Zero;
    public float EarlyReleaseDebugTimer;
    public Vector3 EarlyReleaseEventWorld = Vector3.Zero;
    public Vector3 DebugSupportTargetWorld = Vector3.Zero;

    public HumanoidLegMotionRuntime(
        HumanoidLegRig rig,
        MotionChainDefinition chain,
        MotionContactDefinition contact,
        Vector3 hipOffsetFromPelvisLocal,
        Vector3 restSupportPointLocal,
        Vector3 heelContactLocal,
        Vector3 toeContactLocal,
        float phaseOffset)
    {
        Rig = rig;
        Chain = chain;
        Contact = contact;
        HipOffsetFromPelvisLocal = hipOffsetFromPelvisLocal;
        RestSupportPointLocal = restSupportPointLocal;
        HeelContactLocal = heelContactLocal;
        ToeContactLocal = toeContactLocal;
        ActiveSupportOffsetLocal = contact.SupportOffsetLocal;
        PhaseOffset = phaseOffset;
    }
}

internal readonly struct HumanoidStepReleaseDecision
{
    public bool ShouldStart { get; init; }
    public bool IsEarlyRelease { get; init; }
    public float Urgency { get; init; }
}
