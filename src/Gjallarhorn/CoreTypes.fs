﻿namespace Gjallarhorn

open Gjallarhorn.Internal
open Gjallarhorn.Validation

open System
open System.Collections.Generic

/// Type which allows tracking of multiple disposables at once
type CompositeDisposable() =
    let disposables = ResizeArray<_>()

    /// Add a new disposable to this tracker
    member __.Add (disposable : IDisposable) = disposables.Add(disposable)
    /// Remove a disposable from this tracker without disposing of it
    member __.Remove (disposable : IDisposable) = disposables.Remove(disposable)

    /// Dispose all of our tracked disposables and remove them all 
    member __.Dispose() =
        disposables
        |> Seq.iter (fun d -> d.Dispose())
        disposables.Clear()

    interface IDisposable with
        /// Dispose all of our tracked disposables and remove them all 
        member this.Dispose() = this.Dispose()

module internal DisposeHelpers =
    let getValue (provider : ISignal<_> option) typeNameFun =
        match provider with 
        | Some(v) -> v.Value
        | None -> raise <| ObjectDisposedException(typeNameFun())        

    let setValue (provider : IMutatable<_> option) mapping value typeNameFun =
        match provider with 
        | Some(v) -> v.Value <- mapping(value)
        | None -> raise <| ObjectDisposedException(typeNameFun())        

    let disposeIfDisposable (v : obj) =
        match v with
        | :? IDisposable as d -> 
            d.Dispose()
        | _ -> ()
        
    let dispose (provider : #ISignal<'a> option) disposeProviderOnDispose (self : IDependent) =
            match provider with
            | None -> ()
            | Some(v) ->
                v.Untrack self
                
                if disposeProviderOnDispose then
                    disposeIfDisposable v

// A lightweight wrapper for a mutable value which provides a mechanism for change notification as needed
type internal Mutable<'a>(value : 'a) =

    let mutable v = value

    // Stores dependencies remotely to not use any space in the object (no memory overhead requirements)
    member private this.Dependencies with get() = Dependencies.createRemote this
    
    member this.Value 
        with get() = v
        and set(value) =
            if not(EqualityComparer<'a>.Default.Equals(v, value)) then            
                v <- value
                this.Dependencies.Signal(this)

    interface IObservable<'a> with
        member this.Subscribe obs = 
            this.Dependencies.Add obs
            { 
                new IDisposable with
                    member __.Dispose() = this.Dependencies.Remove obs
            }
    interface ITracksDependents with
        member this.Track dep = this.Dependencies.Add dep
        member this.Untrack dep = this.Dependencies.Remove dep
    interface IDependent with
        member __.RequestRefresh _ = ()
        member this.HasDependencies with get() = this.Dependencies.HasDependencies
    interface ISignal<'a> with
        member __.Value with get() = v

    interface IMutatable<'a> with
        member this.Value with get() = v and set(v) = this.Value <- v
        
type internal MappingSignal<'a,'b>(valueProvider : ISignal<'a>, mapping : 'a -> 'b, disposeProviderOnDispose : bool) as self =
    let dependencies = Dependencies.create [| valueProvider |] self
    let mutable lastValue = mapping valueProvider.Value
    let mutable valueProvider = Some(valueProvider)

    member private this.Signal() = dependencies.Signal this |> ignore

    member private this.UpdateAndGetValue () =
        let value () = 
            DisposeHelpers.getValue valueProvider (fun _ -> self.GetType().FullName)
            |> mapping
        let value = value()
        if not <| EqualityComparer<_>.Default.Equals(lastValue, value) then
            lastValue <- value
            this.Signal()
        lastValue

    override this.Finalize() =
        (this :> IDisposable).Dispose()
        GC.SuppressFinalize this        

    interface IObservable<'b> with
        member __.Subscribe obs = 
            dependencies.Add obs
            { 
                new IDisposable with
                    member __.Dispose() = dependencies.Remove obs
            }

    interface ITracksDependents with
        member __.Track dep = dependencies.Add dep
        member __.Untrack dep = dependencies.Remove dep

    interface ISignal<'b> with
        member this.Value with get() = this.UpdateAndGetValue ()

    interface IDependent with
        member this.RequestRefresh _ =             
            this.UpdateAndGetValue ()
            |> ignore
        member __.HasDependencies with get() = dependencies.HasDependencies

    interface IDisposable with
        member this.Dispose() =
            DisposeHelpers.dispose valueProvider disposeProviderOnDispose this
            valueProvider <- None
            dependencies.RemoveAll()

type internal Mapping2Signal<'a,'b,'c>(valueProvider1 : ISignal<'a>, valueProvider2 : ISignal<'b>, mapping : 'a -> 'b -> 'c) as self =
    let dependencies = Dependencies.create [| valueProvider1 ; valueProvider2 |] self

    let mutable lastValue = mapping valueProvider1.Value valueProvider2.Value
    let mutable valueProvider1 = Some(valueProvider1)
    let mutable valueProvider2 = Some(valueProvider2)

    member private this.Signal() = dependencies.Signal this |> ignore

    member private this.UpdateAndGetValue () =
        let value () = 
            let v1 = DisposeHelpers.getValue valueProvider1 (fun _ -> this.GetType().FullName)
            let v2 = DisposeHelpers.getValue valueProvider2 (fun _ -> this.GetType().FullName)
            mapping v1 v2
        let value = value()
        if not <| EqualityComparer<_>.Default.Equals(lastValue, value) then
            lastValue <- value
            this.Signal()
        lastValue

    override this.Finalize() =
        (this :> IDisposable).Dispose()
        GC.SuppressFinalize this

    interface IObservable<'c> with
        member __.Subscribe obs = 
            dependencies.Add obs
            { 
                new IDisposable with
                    member __.Dispose() = dependencies.Remove obs
            }

    interface ITracksDependents with
        member __.Track dep = dependencies.Add dep
        member __.Untrack dep = dependencies.Remove dep

    interface ISignal<'c> with
        member this.Value with get() = this.UpdateAndGetValue()

    interface IDependent with
        member this.RequestRefresh _ =
            this.UpdateAndGetValue()
            |> ignore
        member __.HasDependencies with get() = dependencies.HasDependencies

    interface IDisposable with
        member this.Dispose() =
            DisposeHelpers.dispose valueProvider1 false this
            DisposeHelpers.dispose valueProvider2 false this
            valueProvider1 <- None
            valueProvider2 <- None
            dependencies.RemoveAll()

type internal FilteredSignal<'a> (valueProvider : ISignal<'a>, filter : 'a -> bool, disposeProviderOnDispose : bool) as self =
    let dependencies = Dependencies.create [| valueProvider |] self

    let mutable v = valueProvider.Value

    let mutable valueProvider = Some(valueProvider)    

    member private this.Signal() = dependencies.Signal this |> ignore

    member private this.UpdateAndGetValue () =
        match valueProvider with
        | None -> ()
        | Some provider ->
            let value = provider.Value
            if (filter(value)) then
                if not <| EqualityComparer<'a>.Default.Equals(v, value) then
                    v <- value
                    this.Signal()
        v

    override this.Finalize() =
        (this :> IDisposable).Dispose()
        GC.SuppressFinalize this

    interface IObservable<'a> with
        member __.Subscribe obs = 
            dependencies.Add obs
            { 
                new IDisposable with
                    member __.Dispose() = dependencies.Remove obs
            }

    interface ITracksDependents with
        member __.Track dep = dependencies.Add dep
        member __.Untrack dep = dependencies.Remove dep

    interface ISignal<'a> with
        member this.Value with get() = this.UpdateAndGetValue()

    interface IDependent with
        member this.RequestRefresh _ = 
            this.UpdateAndGetValue()
            |> ignore
        member __.HasDependencies with get() = dependencies.HasDependencies
                
    interface IDisposable with
        member this.Dispose() =
            DisposeHelpers.dispose valueProvider disposeProviderOnDispose this
            valueProvider <- None
            dependencies.RemoveAll()

type internal CachedSignal<'a> (valueProvider : ISignal<'a>) as self =
    let dependencies = Dependencies.create [| valueProvider |] self

    let mutable v = valueProvider.Value

    // Caching acts like a subscription, since it has to update in case the
    // target is GCed
    // Note: Tracking does not hold a strong reference, so disposal is not necessary still
    do 
        valueProvider.Track self

    // Only store a weak reference to our provider
    let handle = WeakReference<_>(valueProvider)

    member private this.Signal() = dependencies.Signal this |> ignore

    member private this.UpdateAndGetValue () =        
        handle
        |> WeakRef.execute (fun provider ->
            let value = provider.Value
            if not <| EqualityComparer<'a>.Default.Equals(v, value) then
                v <- value
                this.Signal())
        |> ignore
        v

    override this.Finalize() =
        (this :> IDisposable).Dispose()
        GC.SuppressFinalize this

    interface IObservable<'a> with
        member __.Subscribe obs = 
            dependencies.Add obs
            { 
                new IDisposable with
                    member __.Dispose() = dependencies.Remove obs
            }

    interface ITracksDependents with
        member __.Track dep = dependencies.Add dep
        member __.Untrack dep = dependencies.Remove dep

    interface ISignal<'a> with
        member this.Value with get() = this.UpdateAndGetValue()

    interface IDependent with
        member this.RequestRefresh _ =
            this.UpdateAndGetValue()
            |> ignore
        member __.HasDependencies with get() = dependencies.HasDependencies

    interface IDisposable with
        member this.Dispose() =
            handle
            |> WeakRef.execute (fun v ->
                v.Untrack this                    
                handle.SetTarget(Unchecked.defaultof<ISignal<'a>>))
            |> ignore
            dependencies.RemoveAll()