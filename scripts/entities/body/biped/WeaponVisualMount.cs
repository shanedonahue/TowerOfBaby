using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Entities.Body.Biped;

public partial class WeaponVisualMount : Node3D
{
    [Export] public NodePath WeaponRootPath = new("Sword");
    [Export] public float TargetLength = 1.45f;
    [Export(PropertyHint.Range, "0.5,4.0,0.05")] public float ThicknessScale = 1.0f;
    [Export(PropertyHint.Range, "0.0,0.5,0.01")] public float GripFromBack = 0.2f;
    [Export] public bool BackAxisUsesPositiveDirection = true;
    [Export] public Vector3 WeaponRotationDegrees = new(90.0f, 0.0f, 0.0f);
    [Export] public Vector3 FineOffset = new(0.0f, 0.02f, 0.0f);
    [Export] public bool ForceVisible = true;
    [Export] public bool EnableDebugLog;

    private Node3D _weaponRoot = null!;

    public override void _Ready()
    {
        RefreshMount();
    }

    public bool RefreshMount()
    {
        Node weaponNode = GetNodeOrNull<Node>(WeaponRootPath);
        if (weaponNode == null)
        {
            GD.PushError($"Weapon mount failed | {GetPath()} could not find child '{WeaponRootPath}'.");
            return false;
        }

        GD.Print($"Weapon mount | sword child found by WeaponVisualMount at {weaponNode.GetPath()}");

        _weaponRoot = weaponNode as Node3D;
        if (_weaponRoot == null)
        {
            GD.PushError($"Weapon mount failed | child '{weaponNode.Name}' at {weaponNode.GetPath()} is not a Node3D.");
            return false;
        }

        if (!FitWeaponToGrip())
        {
            GD.PushError($"Weapon mount failed | no mesh bounds found under {_weaponRoot.GetPath()}.");
            return false;
        }

        return true;
    }

    private bool FitWeaponToGrip()
    {
        if (!TryGetCombinedLocalAabb(_weaponRoot, out Aabb localBounds))
        {
            return false;
        }

        int majorAxis = GetLargestAxis(localBounds.Size);
        float majorSize = GetAxisValue(localBounds.Size, majorAxis);
        if (majorSize <= 0.0001f)
        {
            GD.PushError($"Weapon mount failed | computed zero-sized bounds for {_weaponRoot.GetPath()}.");
            return false;
        }

        float scale = TargetLength / majorSize;
        _weaponRoot.RotationDegrees = WeaponRotationDegrees;
        Vector3 scaleVector = CreateScaleVector(majorAxis, scale, scale * Mathf.Max(ThicknessScale, 0.01f));
        _weaponRoot.Scale = scaleVector;

        Basis rotationBasis = _weaponRoot.Transform.Basis.Orthonormalized();
        float centerX = localBounds.Position.X + (localBounds.Size.X * 0.5f);
        float centerY = localBounds.Position.Y + (localBounds.Size.Y * 0.5f);
        float centerZ = localBounds.Position.Z + (localBounds.Size.Z * 0.5f);
        float minValue = GetAxisValue(localBounds.Position, majorAxis);
        float maxValue = minValue + majorSize;
        float backValue = BackAxisUsesPositiveDirection ? maxValue : minValue;
        float frontValue = BackAxisUsesPositiveDirection ? minValue : maxValue;
        float gripValue = Mathf.Lerp(backValue, frontValue, Mathf.Clamp(GripFromBack, 0.0f, 0.5f));

        Vector3[] localAxes =
        {
            Vector3.Right,
            Vector3.Up,
            Vector3.Back
        };
        float[] alignmentValues =
        {
            centerX,
            centerY,
            centerZ
        };
        alignmentValues[majorAxis] = gripValue;

        Vector3 offset = Vector3.Zero;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 axisDirection = rotationBasis * localAxes[axis];
            offset -= axisDirection * (alignmentValues[axis] * GetAxisValue(scaleVector, axis));
        }

        _weaponRoot.Position = offset + FineOffset;

        if (ForceVisible)
        {
            ForceMeshesVisible(_weaponRoot);
        }

        if (EnableDebugLog)
        {
            GD.Print(
                $"Weapon mount | node {_weaponRoot.Name} | bounds {localBounds} | major_axis {majorAxis} | scale {_weaponRoot.Scale} | position {_weaponRoot.Position} | rotation {_weaponRoot.RotationDegrees}");
        }

        return true;
    }

    private static bool TryGetCombinedLocalAabb(Node3D root, out Aabb combined)
    {
        bool hasBounds = false;
        combined = default;
        AccumulateBounds(root, root, ref hasBounds, ref combined);
        return hasBounds;
    }

    private static void AccumulateBounds(Node node, Node3D root, ref bool hasBounds, ref Aabb combined)
    {
        if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null)
        {
            Transform3D toRoot = root.GlobalTransform.AffineInverse() * meshInstance.GlobalTransform;
            Aabb transformed = TransformAabb(toRoot, meshInstance.GetAabb());
            if (!hasBounds)
            {
                combined = transformed;
                hasBounds = true;
            }
            else
            {
                combined = combined.Merge(transformed);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            AccumulateBounds(child, root, ref hasBounds, ref combined);
        }
    }

    private static Aabb TransformAabb(Transform3D transform, Aabb aabb)
    {
        List<Vector3> corners = new(8)
        {
            aabb.Position,
            aabb.Position + new Vector3(aabb.Size.X, 0.0f, 0.0f),
            aabb.Position + new Vector3(0.0f, aabb.Size.Y, 0.0f),
            aabb.Position + new Vector3(0.0f, 0.0f, aabb.Size.Z),
            aabb.Position + new Vector3(aabb.Size.X, aabb.Size.Y, 0.0f),
            aabb.Position + new Vector3(aabb.Size.X, 0.0f, aabb.Size.Z),
            aabb.Position + new Vector3(0.0f, aabb.Size.Y, aabb.Size.Z),
            aabb.Position + aabb.Size
        };

        Vector3 transformedMin = transform * corners[0];
        Vector3 transformedMax = transformedMin;
        for (int i = 1; i < corners.Count; i++)
        {
            Vector3 point = transform * corners[i];
            transformedMin = transformedMin.Min(point);
            transformedMax = transformedMax.Max(point);
        }

        return new Aabb(transformedMin, transformedMax - transformedMin);
    }

    private static void ForceMeshesVisible(Node node)
    {
        if (node is MeshInstance3D meshInstance)
        {
            meshInstance.Visible = true;
            meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        }

        foreach (Node child in node.GetChildren())
        {
            ForceMeshesVisible(child);
        }
    }

    private static int GetLargestAxis(Vector3 size)
    {
        if (size.Y >= size.X && size.Y >= size.Z)
        {
            return 1;
        }

        return size.X >= size.Z ? 0 : 2;
    }

    private static float GetAxisValue(Vector3 value, int axis)
    {
        return axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };
    }

    private static Vector3 CreateScaleVector(int majorAxis, float majorScale, float minorScale)
    {
        return majorAxis switch
        {
            0 => new Vector3(majorScale, minorScale, minorScale),
            1 => new Vector3(minorScale, majorScale, minorScale),
            _ => new Vector3(minorScale, minorScale, majorScale)
        };
    }
}
