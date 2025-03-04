using System.Diagnostics.CodeAnalysis;
using Content.Server.Buckle.Components;
using Content.Server.Interaction;
using Content.Server.Popups;
using Content.Server.Pulling;
using Content.Server.Storage.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Alert;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.DragDrop;
using Content.Shared.Hands.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.MobState.Components;
using Content.Shared.MobState.EntitySystems;
using Content.Shared.Pulling.Components;
using Content.Shared.Stunnable;
using Content.Shared.Vehicle.Components;
using Content.Shared.Verbs;
using JetBrains.Annotations;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Buckle.Systems;

[UsedImplicitly]
public sealed class BuckleSystem : SharedBuckleSystem
{
    [Dependency] private readonly IGameTiming _gameTiming = default!;

    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly ContainerSystem _containers = default!;
    [Dependency] private readonly InteractionSystem _interactions = default!;
    [Dependency] private readonly SharedMobStateSystem _mobState = default!;
    [Dependency] private readonly PopupSystem _popups = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly Shared.Standing.StandingStateSystem _standing = default!;

    public override void Initialize()
    {
        base.Initialize();

        UpdatesAfter.Add(typeof(InteractionSystem));
        UpdatesAfter.Add(typeof(InputSystem));

        SubscribeLocalEvent<StrapComponent, ComponentGetState>(OnStrapGetState);
        SubscribeLocalEvent<StrapComponent, EntInsertedIntoContainerMessage>(ContainerModifiedStrap);
        SubscribeLocalEvent<StrapComponent, EntRemovedFromContainerMessage>(ContainerModifiedStrap);

        SubscribeLocalEvent<BuckleComponent, ComponentStartup>(OnBuckleStartup);
        SubscribeLocalEvent<BuckleComponent, ComponentShutdown>(OnBuckleShutdown);
        SubscribeLocalEvent<BuckleComponent, ComponentGetState>(OnBuckleGetState);
        SubscribeLocalEvent<BuckleComponent, MoveEvent>(MoveEvent);
        SubscribeLocalEvent<BuckleComponent, InteractHandEvent>(HandleInteractHand);
        SubscribeLocalEvent<BuckleComponent, GetVerbsEvent<InteractionVerb>>(AddUnbuckleVerb);
        SubscribeLocalEvent<BuckleComponent, InsertIntoEntityStorageAttemptEvent>(OnEntityStorageInsertAttempt);
        SubscribeLocalEvent<BuckleComponent, CanDropEvent>(OnBuckleCanDrop);
        SubscribeLocalEvent<BuckleComponent, DragDropEvent>(OnBuckleDragDrop);
    }

    private void OnStrapGetState(EntityUid uid, StrapComponent component, ref ComponentGetState args)
    {
        args.State = new StrapComponentState(component.Position, component.BuckleOffset, component.BuckledEntities, component.MaxBuckleDistance);
    }

    private void AddUnbuckleVerb(EntityUid uid, BuckleComponent component, GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !component.Buckled)
            return;

        InteractionVerb verb = new()
        {
            Act = () => TryUnbuckle(uid, args.User, buckle: component),
            Text = Loc.GetString("verb-categories-unbuckle"),
            IconTexture = "/Textures/Interface/VerbIcons/unbuckle.svg.192dpi.png"
        };

        if (args.Target == args.User && args.Using == null)
        {
            // A user is left clicking themselves with an empty hand, while buckled.
            // It is very likely they are trying to unbuckle themselves.
            verb.Priority = 1;
        }

        args.Verbs.Add(verb);
    }

    private void OnBuckleStartup(EntityUid uid, BuckleComponent component, ComponentStartup args)
    {
        UpdateBuckleStatus(uid, component);
    }

    private void OnBuckleShutdown(EntityUid uid, BuckleComponent component, ComponentShutdown args)
    {
        component.BuckledTo?.Remove(component);
        TryUnbuckle(uid, uid, true, component);

        component.BuckleTime = default;
    }

    private void OnBuckleGetState(EntityUid uid, BuckleComponent component, ref ComponentGetState args)
    {
        args.State = new BuckleComponentState(component.Buckled, component.LastEntityBuckledTo, component.DontCollide);
    }

    private void HandleInteractHand(EntityUid uid, BuckleComponent component, InteractHandEvent args)
    {
        args.Handled = TryUnbuckle(uid, args.User, buckle: component);
    }

    private void MoveEvent(EntityUid uid, BuckleComponent buckle, ref MoveEvent ev)
    {
        var strap = buckle.BuckledTo;

        if (strap == null)
        {
            return;
        }

        var strapPosition = Transform(strap.Owner).Coordinates;

        if (ev.NewPosition.InRange(EntityManager, strapPosition, strap.MaxBuckleDistance))
        {
            return;
        }

        TryUnbuckle(uid, buckle.Owner, true, buckle);
    }

    private void ContainerModifiedStrap(EntityUid uid, StrapComponent strap, ContainerModifiedMessage message)
    {
        if (GameTiming.ApplyingState)
            return;

        foreach (var buckledEntity in strap.BuckledEntities)
        {
            if (!TryComp(buckledEntity, out BuckleComponent? buckled))
            {
                continue;
            }

            ContainerModifiedReAttach(buckledEntity, strap.Owner, buckled, strap);
        }
    }

    private void ContainerModifiedReAttach(EntityUid buckleId, EntityUid strapId, BuckleComponent? buckle = null, StrapComponent? strap = null)
    {
        if (!Resolve(buckleId, ref buckle, false) ||
            !Resolve(strapId, ref strap, false))
        {
            return;
        }

        var contained = _containers.TryGetContainingContainer(buckleId, out var ownContainer);
        var strapContained = _containers.TryGetContainingContainer(strapId, out var strapContainer);

        if (contained != strapContained || ownContainer != strapContainer)
        {
            TryUnbuckle(buckleId, buckle.Owner, true, buckle);
            return;
        }

        if (!contained)
        {
            ReAttach(buckleId, strap, buckle);
        }
    }

    public void OnEntityStorageInsertAttempt(EntityUid uid, BuckleComponent comp, InsertIntoEntityStorageAttemptEvent args)
    {
        if (comp.Buckled)
            args.Cancel();
    }

    private void OnBuckleCanDrop(EntityUid uid, BuckleComponent component, CanDropEvent args)
    {
        args.Handled = HasComp<StrapComponent>(args.Target);
    }

    private void OnBuckleDragDrop(EntityUid uid, BuckleComponent component, DragDropEvent args)
    {
        args.Handled = TryBuckle(uid, args.User, args.Target, component);
    }

    /// <summary>
    ///     Shows or hides the buckled status effect depending on if the
    ///     entity is buckled or not.
    /// </summary>
    private void UpdateBuckleStatus(EntityUid uid, BuckleComponent component)
    {
        if (component.Buckled)
        {
            var alertType = component.BuckledTo?.BuckledAlertType ?? AlertType.Buckled;
            _alerts.ShowAlert(uid, alertType);
        }
        else
        {
            _alerts.ClearAlertCategory(uid, AlertCategory.Buckled);
        }
    }

    private void SetBuckledTo(BuckleComponent buckle, StrapComponent? strap)
    {
        buckle.BuckledTo = strap;
        buckle.LastEntityBuckledTo = strap?.Owner;

        if (strap == null)
        {
            buckle.Buckled = false;
        }
        else
        {
            buckle.DontCollide = true;
            buckle.Buckled = true;
            buckle.BuckleTime = _gameTiming.CurTime;
        }

        _actionBlocker.UpdateCanMove(buckle.Owner);
        UpdateBuckleStatus(buckle.Owner, buckle);
        Dirty(buckle);
    }

    public bool CanBuckle(
        EntityUid buckleId,
        EntityUid user,
        EntityUid to,
        [NotNullWhen(true)] out StrapComponent? strap,
        BuckleComponent? buckle = null)
    {
        strap = null;

        if (user == to ||
            !Resolve(buckleId, ref buckle, false) ||
            !Resolve(to, ref strap, false))
        {
            return false;
        }

        var strapUid = strap.Owner;
        bool Ignored(EntityUid entity) => entity == buckleId || entity == user || entity == strapUid;

        if (!_interactions.InRangeUnobstructed(buckleId, strapUid, buckle.Range, predicate: Ignored, popup: true))
        {
            return false;
        }

        // If in a container
        if (_containers.TryGetContainingContainer(buckleId, out var ownerContainer))
        {
            // And not in the same container as the strap
            if (!_containers.TryGetContainingContainer(strap.Owner, out var strapContainer) ||
                ownerContainer != strapContainer)
            {
                return false;
            }
        }

        if (!HasComp<SharedHandsComponent>(user))
        {
            _popups.PopupEntity(Loc.GetString("buckle-component-no-hands-message"), user, Filter.Entities(user));
            return false;
        }

        if (buckle.Buckled)
        {
            var message = Loc.GetString(buckleId == user
                    ? "buckle-component-already-buckled-message"
                    : "buckle-component-other-already-buckled-message",
                ("owner", Identity.Entity(buckleId, EntityManager)));
            _popups.PopupEntity(message, user, Filter.Entities(user));

            return false;
        }

        var parent = Transform(to).ParentUid;
        while (parent.IsValid())
        {
            if (parent == user)
            {
                var message = Loc.GetString(buckleId == user
                    ? "buckle-component-cannot-buckle-message"
                    : "buckle-component-other-cannot-buckle-message", ("owner", Identity.Entity(buckleId, EntityManager)));
                _popups.PopupEntity(message, user, Filter.Entities(user));

                return false;
            }

            parent = Transform(parent).ParentUid;
        }

        if (!strap.HasSpace(buckle))
        {
            var message = Loc.GetString(buckleId == user
                ? "buckle-component-cannot-fit-message"
                : "buckle-component-other-cannot-fit-message", ("owner", Identity.Entity(buckleId, EntityManager)));
            _popups.PopupEntity(message, user, Filter.Entities(user));

            return false;
        }

        return true;
    }

    public bool TryBuckle(EntityUid buckleId, EntityUid user, EntityUid to, BuckleComponent? buckle = null)
    {
        if (!Resolve(buckleId, ref buckle, false))
            return false;

        if (!CanBuckle(buckleId, user, to, out var strap, buckle))
            return false;

        _audio.Play(strap.BuckleSound, Filter.Pvs(buckleId), buckleId);

        if (!strap.TryAdd(buckle))
        {
            var message = Loc.GetString(buckleId == user
                ? "buckle-component-cannot-buckle-message"
                : "buckle-component-other-cannot-buckle-message", ("owner", Identity.Entity(buckleId, EntityManager)));
            _popups.PopupEntity(message, user, Filter.Entities(user));
            return false;
        }

        if (TryComp<AppearanceComponent>(buckleId, out var appearance))
            _appearance.SetData(buckleId, BuckleVisuals.Buckled, true, appearance);

        ReAttach(buckleId, strap, buckle);
        SetBuckledTo(buckle, strap);

        var ev = new BuckleChangeEvent { Buckling = true, Strap = strap.Owner, BuckledEntity = buckleId };
        RaiseLocalEvent(ev.BuckledEntity, ev);
        RaiseLocalEvent(ev.Strap, ev);

        if (TryComp(buckleId, out SharedPullableComponent? ownerPullable))
        {
            if (ownerPullable.Puller != null)
            {
                _pulling.TryStopPull(ownerPullable);
            }
        }

        if (TryComp(to, out SharedPullableComponent? toPullable))
        {
            if (toPullable.Puller == buckleId)
            {
                // can't pull it and buckle to it at the same time
                _pulling.TryStopPull(toPullable);
            }
        }

        return true;
    }

    /// <summary>
    ///     Tries to unbuckle the Owner of this component from its current strap.
    /// </summary>
    /// <param name="buckleId">The entity to unbuckle.</param>
    /// <param name="user">The entity doing the unbuckling.</param>
    /// <param name="force">
    ///     Whether to force the unbuckling or not. Does not guarantee true to
    ///     be returned, but guarantees the owner to be unbuckled afterwards.
    /// </param>
    /// <param name="buckle">The buckle component of the entity to unbuckle.</param>
    /// <returns>
    ///     true if the owner was unbuckled, otherwise false even if the owner
    ///     was previously already unbuckled.
    /// </returns>
    public bool TryUnbuckle(EntityUid buckleId, EntityUid user, bool force = false, BuckleComponent? buckle = null)
    {
        if (!Resolve(buckleId, ref buckle, false) ||
            buckle.BuckledTo is not { } oldBuckledTo)
        {
            return false;
        }

        if (!force)
        {
            if (_gameTiming.CurTime < buckle.BuckleTime + buckle.UnbuckleDelay)
                return false;

            if (!_interactions.InRangeUnobstructed(user, oldBuckledTo.Owner, buckle.Range, popup: true))
                return false;

            if (HasComp<SleepingComponent>(buckleId) && buckleId == user)
                return false;

            // If the strap is a vehicle and the rider is not the person unbuckling, return.
            if (TryComp(oldBuckledTo.Owner, out VehicleComponent? vehicle) &&
                vehicle.Rider != user)
                return false;
        }

        SetBuckledTo(buckle, null);

        var xform = Transform(buckleId);
        var oldBuckledXform = Transform(oldBuckledTo.Owner);

        if (xform.ParentUid == oldBuckledXform.Owner)
        {
            _containers.AttachParentToContainerOrGrid(xform);
            xform.WorldRotation = oldBuckledXform.WorldRotation;

            if (oldBuckledTo.UnbuckleOffset != Vector2.Zero)
                xform.Coordinates = oldBuckledXform.Coordinates.Offset(oldBuckledTo.UnbuckleOffset);
        }

        if (TryComp(buckleId, out AppearanceComponent? appearance))
            _appearance.SetData(buckleId, BuckleVisuals.Buckled, false, appearance);

        if (HasComp<KnockedDownComponent>(buckleId)
            | (TryComp<MobStateComponent>(buckleId, out var mobState) && _mobState.IsIncapacitated(buckleId, mobState)))
        {
            _standing.Down(buckleId);
        }
        else
        {
            _standing.Stand(buckleId);
        }

        _mobState.EnterState(mobState, mobState?.CurrentState);

        oldBuckledTo.Remove(buckle);
        _audio.Play(oldBuckledTo.UnbuckleSound, Filter.Pvs(buckleId), buckleId);

        var ev = new BuckleChangeEvent { Buckling = false, Strap = oldBuckledTo.Owner, BuckledEntity = buckleId };
        RaiseLocalEvent(buckleId, ev);
        RaiseLocalEvent(oldBuckledTo.Owner, ev);

        return true;
    }

    /// <summary>
    ///     Makes an entity toggle the buckling status of the owner to a
    ///     specific entity.
    /// </summary>
    /// <param name="buckleId">The entity to buckle/unbuckle from <see cref="to"/>.</param>
    /// <param name="user">The entity doing the buckling/unbuckling.</param>
    /// <param name="to">
    ///     The entity to toggle the buckle status of the owner to.
    /// </param>
    /// <param name="force">
    ///     Whether to force the unbuckling or not, if it happens. Does not
    ///     guarantee true to be returned, but guarantees the owner to be
    ///     unbuckled afterwards.
    /// </param>
    /// <param name="buckle">The buckle component of the entity to buckle/unbuckle from <see cref="to"/>.</param>
    /// <returns>true if the buckling status was changed, false otherwise.</returns>
    public bool ToggleBuckle(
        EntityUid buckleId,
        EntityUid user,
        EntityUid to,
        bool force = false,
        BuckleComponent? buckle = null)
    {
        if (!Resolve(buckleId, ref buckle, false))
            return false;

        if (buckle.BuckledTo?.Owner == to)
        {
            return TryUnbuckle(buckleId, user, force, buckle);
        }

        return TryBuckle(buckleId, user, to, buckle);
    }
}
