using System.Globalization;
using System.Text;

namespace TowerOfBaby.Entities.Motion;

public static class LocomotionMetrics
{
    public static string BuildOverlayText(LocomotionTelemetrySnapshot snapshot)
    {
        if (snapshot == null)
        {
            return "Locomotion: unavailable";
        }

        StringBuilder builder = new();
        builder.AppendLine(
            $"Locomotion: desired {snapshot.DesiredMovement.Length():0.00} | actual {snapshot.ActualMovement.Length():0.00} | stance {snapshot.StanceWidth:0.00}");
        builder.AppendLine(
            $"Facing {FormatVector(snapshot.FacingDirection)} | ground {FormatVector(snapshot.GroundNormal)}");
        builder.AppendLine(BuildFootLine(snapshot.LeftFoot));
        builder.Append(BuildFootLine(snapshot.RightFoot));
        return builder.ToString();
    }

    public static string BuildCsvHeader()
    {
        return "root_speed_desired,root_speed_actual,stance_width,left_state,left_violation,left_skate,left_current_step_s,left_last_step_s,left_step_count,left_reason,right_state,right_violation,right_skate,right_current_step_s,right_last_step_s,right_step_count,right_reason";
    }

    public static string BuildCsvValues(LocomotionTelemetrySnapshot snapshot)
    {
        if (snapshot == null)
        {
            return ",,,,,,,,,,,,,,,,";
        }

        return string.Join(",",
            snapshot.DesiredMovement.Length().ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.ActualMovement.Length().ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.StanceWidth.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.LeftFoot.State.ToString(),
            snapshot.LeftFoot.ThresholdViolation.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.LeftFoot.FootSkateDistance.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.LeftFoot.CurrentStepDuration.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.LeftFoot.LastStepDuration.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.LeftFoot.StepCount.ToString(CultureInfo.InvariantCulture),
            Escape(snapshot.LeftFoot.DecisionReason),
            snapshot.RightFoot.State.ToString(),
            snapshot.RightFoot.ThresholdViolation.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.RightFoot.FootSkateDistance.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.RightFoot.CurrentStepDuration.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.RightFoot.LastStepDuration.ToString("0.000", CultureInfo.InvariantCulture),
            snapshot.RightFoot.StepCount.ToString(CultureInfo.InvariantCulture),
            Escape(snapshot.RightFoot.DecisionReason));
    }

    private static string BuildFootLine(LocomotionFootTelemetry telemetry)
    {
        return
            $"{telemetry.Side}: {telemetry.State} | v {telemetry.ThresholdViolation:0.00} | skate {telemetry.FootSkateDistance:0.000} | " +
            $"step {telemetry.CurrentStepDuration:0.00}/{telemetry.LastStepDuration:0.00}s | {telemetry.DecisionReason}";
    }

    private static string FormatVector(Godot.Vector3 value)
    {
        return $"({value.X:0.00},{value.Y:0.00},{value.Z:0.00})";
    }

    private static string Escape(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace(',', ';');
    }
}
