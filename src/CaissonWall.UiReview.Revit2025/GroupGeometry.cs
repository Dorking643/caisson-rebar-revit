using System;
using System.Collections.Generic;

namespace CaissonWallUi;

public sealed class GroupMember
{
    public int Index { get; init; }
    public double AngleDeg { get; init; }
    public double RhoOffsetMm { get; init; }
    public double ZOffsetMm { get; init; }
}

/// <summary>Read-only member transforms for the UI preview. No model objects or source paths are changed.</summary>
public static class GroupGeometry
{
    public const int CountLimit = 1000;

    public static bool TryMembers(ReviewDraft draft, out IReadOnlyList<GroupMember> members, out string error)
        => TryMembers(draft, draft?.Group ?? new GroupParameters(), out members, out error);

    public static bool TryMembers(ReviewDraft draft, GroupParameters parameters,
        out IReadOnlyList<GroupMember> members, out string error)
    {
        members = Array.Empty<GroupMember>(); error = "";
        if (draft == null) { error = "No draft selected."; return false; }
        if (parameters == null) { error = "Group parameters are missing."; return false; }
        // Disabled settings may contain unfinished input; none of it applies to the original member.
        if (!parameters.Enabled)
        {
            members = Array.AsReadOnly(new[] { new GroupMember { Index = 0 } });
            return true;
        }
        if (draft.Kind == DraftKind.Ring) return RingMembers(draft, parameters, out members, out error);
        if (draft.Kind == DraftKind.Profile) return ProfileMembers(parameters, out members, out error);
        error = "Unsupported draft type."; return false;
    }

    static bool RingMembers(ReviewDraft draft, GroupParameters p, out IReadOnlyList<GroupMember> members, out string error)
    {
        members = Array.Empty<GroupMember>(); error = "";
        if (!ValidCount(p.Count, out error)) return false;
        if (p.RingAxis is not (RingArrayAxis.X or RingArrayAxis.Y)) { error = "Ring array direction must be X or Y."; return false; }
        if (!double.IsFinite(p.RingStepMm)) { error = "Ring spacing must be a finite value."; return false; }
        if (p.Count > 1 && p.RingStepMm == 0) { error = "Spacing cannot be zero for multiple rings."; return false; }
        if (draft.RingPoint == null || !double.IsFinite(draft.RingPoint.Rho) || !double.IsFinite(draft.RingPoint.Z))
        { error = "Ring coordinates must be finite values."; return false; }

        var result = new GroupMember[p.Count];
        for (int j = 0; j < p.Count; j++)
        {
            double offset = j * p.RingStepMm;
            double rhoOffset = p.RingAxis == RingArrayAxis.X ? offset : 0;
            double zOffset = p.RingAxis == RingArrayAxis.Y ? -offset : 0;
            double rho = draft.RingPoint.Rho + rhoOffset, z = draft.RingPoint.Z + zOffset;
            if (!double.IsFinite(offset) || !double.IsFinite(rho) || !double.IsFinite(z))
            { error = "Array coordinates exceed the numeric range."; return false; }
            if (rho <= 0) { error = $"Ring {j + 1} radius must be greater than zero."; return false; }
            result[j] = new GroupMember { Index = j, RhoOffsetMm = rhoOffset == 0 ? 0 : rhoOffset, ZOffsetMm = zOffset == 0 ? 0 : zOffset };
        }
        members = Array.AsReadOnly(result); return true;
    }

    static bool ProfileMembers(GroupParameters p, out IReadOnlyList<GroupMember> members, out string error)
    {
        members = Array.Empty<GroupMember>(); error = "";
        if (!double.IsFinite(p.StartAngleDeg)) { error = "Start angle must be a finite value."; return false; }
        int count;
        if (p.ProfileMode == ProfileArrayMode.Count)
        {
            if (!ValidCount(p.Count, out error)) return false;
            count = p.Count;
        }
        else if (p.ProfileMode == ProfileArrayMode.AngleStep)
        {
            if (!double.IsFinite(p.AngleStepDeg) || p.AngleStepDeg <= 0 || p.AngleStepDeg > 360)
            { error = "Angle step must be greater than 0 and no greater than 360°."; return false; }
            double ratio = 360.0 / p.AngleStepDeg;
            if (!double.IsFinite(ratio)) { error = $"Preview is limited to {CountLimit} bars."; return false; }
            // A rounded representation of 360/N must not introduce an extra member at 360°.
            // Only a few machine ULPs are corrected; intentionally non-dividing steps keep their final member.
            double nearest = Math.Round(ratio), ulp = Math.BitIncrement(ratio) - ratio;
            if (Math.Abs(ratio - nearest) <= 4 * ulp) ratio = nearest;
            double roundedCount = Math.Ceiling(ratio);
            if (roundedCount < 1 || roundedCount > CountLimit) { error = $"Preview is limited to {CountLimit} bars."; return false; }
            count = (int)roundedCount;
        }
        else { error = "Unsupported circular array mode."; return false; }

        double start = NormalizeAngle(p.StartAngleDeg);
        var result = new GroupMember[count];
        for (int j = 0; j < count; j++)
        {
            double delta = p.ProfileMode == ProfileArrayMode.Count ? 360.0 * j / count : j * p.AngleStepDeg;
            // Count determination above excludes the terminal 360° member.
            if (!double.IsFinite(delta) || delta >= 360)
            { error = "Angle step creates a duplicate endpoint. Check the input."; return false; }
            result[j] = new GroupMember { Index = j, AngleDeg = NormalizeAngle(start + delta) };
        }
        members = Array.AsReadOnly(result); return true;
    }

    static bool ValidCount(int count, out string error)
    {
        error = count < 1 ? "Count must be a positive integer." : count > CountLimit ? $"Preview is limited to {CountLimit} bars." : "";
        return error.Length == 0;
    }
    static double NormalizeAngle(double value)
    {
        double result = value % 360;
        if (result < 0) result += 360;
        return result == 0 || result >= 360 ? 0 : result;
    }
}
