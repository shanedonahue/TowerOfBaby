using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Motion;

public enum MotionChainType
{
    Spine = 0,
    Arm = 1,
    Leg = 2,
    Tail = 3,
    Wing = 4,
    Custom = 5
}

public enum MotionContactType
{
    Foot = 0,
    Hand = 1,
    Paw = 2,
    Hoof = 3,
    Custom = 4
}

public sealed class MotionJointDefinition
{
    public string Id { get; }
    public int ParentIndex { get; }
    public Vector3 LocalRestPosition { get; }

    public MotionJointDefinition(string id, int parentIndex, Vector3 localRestPosition)
    {
        Id = id;
        ParentIndex = parentIndex;
        LocalRestPosition = localRestPosition;
    }
}

public sealed class MotionChainDefinition
{
    public string Id { get; }
    public MotionChainType Type { get; }
    public int RootJointIndex { get; }
    public int MidJointIndex { get; }
    public int EndJointIndex { get; }
    public Vector3 PreferredBendNormalLocal { get; }

    public MotionChainDefinition(
        string id,
        MotionChainType type,
        int rootJointIndex,
        int midJointIndex,
        int endJointIndex,
        Vector3 preferredBendNormalLocal)
    {
        Id = id;
        Type = type;
        RootJointIndex = rootJointIndex;
        MidJointIndex = midJointIndex;
        EndJointIndex = endJointIndex;
        PreferredBendNormalLocal = preferredBendNormalLocal;
    }
}

public sealed class MotionContactDefinition
{
    public string Id { get; }
    public MotionContactType Type { get; }
    public string ChainId { get; }
    public int JointIndex { get; }
    public Vector3 SupportOffsetLocal { get; }
    public float GroundClearance { get; }

    public MotionContactDefinition(
        string id,
        MotionContactType type,
        string chainId,
        int jointIndex,
        Vector3 supportOffsetLocal,
        float groundClearance)
    {
        Id = id;
        Type = type;
        ChainId = chainId;
        JointIndex = jointIndex;
        SupportOffsetLocal = supportOffsetLocal;
        GroundClearance = groundClearance;
    }
}

public sealed class MotionSkeletonDefinition
{
    public MotionJointDefinition[] Joints { get; }
    public MotionChainDefinition[] Chains { get; }
    public MotionContactDefinition[] Contacts { get; }

    private readonly Dictionary<string, int> _jointIndices;
    private readonly Dictionary<string, MotionChainDefinition> _chainsById;
    private readonly Dictionary<string, MotionContactDefinition> _contactsById;

    public MotionSkeletonDefinition(
        MotionJointDefinition[] joints,
        MotionChainDefinition[] chains,
        MotionContactDefinition[] contacts,
        Dictionary<string, int> jointIndices)
    {
        Joints = joints;
        Chains = chains;
        Contacts = contacts;
        _jointIndices = jointIndices;
        _chainsById = new Dictionary<string, MotionChainDefinition>(chains.Length);
        _contactsById = new Dictionary<string, MotionContactDefinition>(contacts.Length);

        foreach (MotionChainDefinition chain in chains)
        {
            _chainsById[chain.Id] = chain;
        }

        foreach (MotionContactDefinition contact in contacts)
        {
            _contactsById[contact.Id] = contact;
        }
    }

    public Vector3 GetJointRestPosition(string id)
    {
        return Joints[_jointIndices[id]].LocalRestPosition;
    }

    public MotionChainDefinition GetChain(string id)
    {
        return _chainsById[id];
    }

    public MotionContactDefinition GetContact(string id)
    {
        return _contactsById[id];
    }
}
