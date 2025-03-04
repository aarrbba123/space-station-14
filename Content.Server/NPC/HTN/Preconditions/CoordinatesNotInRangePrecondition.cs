using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// Is the specified coordinate not in range of us.
/// </summary>
public sealed class CoordinatesNotInRangePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField("targetKey", required: true)] public string TargetKey = default!;

    [DataField("rangeKey", required: true)]
    public string RangeKey = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(NPCBlackboard.OwnerCoordinates, out var coordinates))
            return false;

        if (!blackboard.TryGetValue<EntityCoordinates>(TargetKey, out var target))
            return false;

        return !coordinates.InRange(_entManager, target, blackboard.GetValueOrDefault<float>(RangeKey));
    }
}

