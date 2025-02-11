﻿namespace Aardvark.Rendering

open System
open System.Threading
open System.Collections.Concurrent
open Aardvark.Base
open FSharp.Data.Adaptive
open Microsoft.FSharp.NativeInterop

#nowarn "9"

type ResourceInfo =
    struct
        val mutable public AllocatedSize : Mem
        val mutable public UsedSize : Mem

        static member Zero = ResourceInfo(Mem.Zero, Mem.Zero)

        static member (+) (l : ResourceInfo, r : ResourceInfo) =
            ResourceInfo(
                l.AllocatedSize + r.AllocatedSize,
                l.UsedSize + r.UsedSize
            )

        static member (-) (l : ResourceInfo, r : ResourceInfo) =
            ResourceInfo(
                l.AllocatedSize - r.AllocatedSize,
                l.UsedSize - r.UsedSize
            )

        static member (*) (l : ResourceInfo, r : int) =
            ResourceInfo(
                l.AllocatedSize * r,
                l.UsedSize * r
            )

        static member (*) (l : ResourceInfo, r : float) =
            ResourceInfo(
                l.AllocatedSize * r,
                l.UsedSize * r
            )

        static member (*) (l : int, r : ResourceInfo) =
            ResourceInfo(
                l * r.AllocatedSize,
                l * r.UsedSize
            )

        static member (*) (l : float, r : ResourceInfo) =
            ResourceInfo(
                l * r.AllocatedSize,
                l * r.UsedSize
            )

        static member (/) (l : ResourceInfo, r : int) =
            ResourceInfo(
                l.AllocatedSize / r,
                l.UsedSize / r
            )

        static member (/) (l : ResourceInfo, r : float) =
            ResourceInfo(
                l.AllocatedSize / r,
                l.UsedSize / r
            )


        new(a,u) = { AllocatedSize = a; UsedSize = u }
        new(s) = { AllocatedSize = s; UsedSize = s }
    end


type IResource =
    inherit IAdaptiveObject
    inherit IDisposable
    abstract member Id : int
    abstract member AddRef : unit -> unit
    abstract member RemoveRef : unit -> unit
    abstract member Update : token : AdaptiveToken * rt : RenderToken -> unit
    abstract member Kind : ResourceKind
    abstract member IsDisposed : bool
    abstract member Info : ResourceInfo
    abstract member HandleType : Type

type IResource<'h> =
    inherit IResource
    abstract member Handle : aval<'h>

type IResource<'h, 'v when 'v : unmanaged> =
    inherit IResource<'h>
    abstract member Pointer : nativeptr<'v>

type ResourceDescription<'d, 'h, 'v when 'v : unmanaged> =
    {
        create : 'd -> 'h
        update : 'h -> 'd -> 'h
        delete : 'h -> unit
        view   : 'h -> 'v
        info   : 'h -> ResourceInfo
        kind   : ResourceKind
    }

[<AbstractClass>]
type Resource<'h, 'v when 'v : unmanaged>(kind : ResourceKind) =
    inherit AdaptiveObject()
    static let th = typeof<'h>

    let mutable current = None
    let handle = AVal.init Unchecked.defaultof<'h>
    let pointer : nativeptr<'v> = NativePtr.alloc 1

    let mutable refCount = 0
    let onDispose = new Event<unit>()

    let mutable info = ResourceInfo.Zero
    let lockObj = obj()
    let mutable wasDisposed = 0

    let t = new Transaction() // reusable transaction

    let destroy(x : Resource<_,_>) =
        let alreadyDisposed = Interlocked.CompareExchange(&wasDisposed,1,0)
        if alreadyDisposed = 1 then failwithf "doubleFree"

        onDispose.Trigger()
        x.Destroy handle.Value
        current <- None
        info <- ResourceInfo.Zero
        NativePtr.free pointer

        lock x (fun () ->
            x.Outputs.Clear()
            x.OutOfDate <- true
        )

    let setHandle (x : Resource<'h, 'v>) (h : 'h) : unit =
        let v : 'v = x.View(h)
        NativePtr.write pointer v

        handle.Level <- max (1 + x.Level) handle.Level
        if not (Unchecked.equals h handle.Value) then
            useTransaction t (fun () -> handle.Value <- h)
            t.Commit()
            t.Dispose()

    let id = newId()

    abstract member Create : AdaptiveToken * RenderToken * Option<'h> -> 'h
    abstract member Destroy : 'h -> unit
    abstract member GetInfo : 'h -> ResourceInfo
    abstract member View : 'h -> 'v


    member x.HandleType = th
    member x.IsDisposed = Option.isNone current

    member x.Info = info

    member x.Kind = kind

    member internal x.OnDispose = onDispose.Publish :> IObservable<_>

    member private x.PerformUpdate(token : AdaptiveToken, t : RenderToken) =
        if refCount <= 0 then
            // [hs 21.12.2020] was (and should be) exn. https://github.com/vrvis/PRo3D/issues/3
            // untill no artificial repro is found, we need to go with this.
            Log.warn "[Resource] cannot update unreferenced resource (refCount = %d, x.IsDisposed = %b, th = %A)" refCount x.IsDisposed th
        else
            let h = x.Create(token, t,current)
            info <- x.GetInfo h
            setHandle x h

            match current with
            | Some old when Unchecked.equals old h ->
                t.InPlaceResourceUpdate(kind)

            | Some old ->
                current <- Some h
                t.ReplacedResource(kind)

            | None ->
                current <- Some h
                t.CreatedResource(kind)


    member x.AddRef() =
        lock lockObj (fun () ->
            if Interlocked.Increment(&refCount) = 1 then
                x.ForceUpdate(AdaptiveToken.Top, RenderToken.Empty) |> ignore
        )

    member x.RemoveRef() =
        lock lockObj (fun () ->
            if Interlocked.Decrement(&refCount) = 0 then
                destroy x
        )

    member x.Update(token : AdaptiveToken, rt : RenderToken) =
        x.EvaluateAlways token (fun token ->
            if x.OutOfDate then
                x.PerformUpdate(token, rt)
        )

    member x.ForceUpdate(token : AdaptiveToken, rt : RenderToken) =
        x.EvaluateAlways token (fun token ->
            x.PerformUpdate(token, rt)
        )

    member x.Handle = handle :> aval<_>

    member x.Dispose() = x.RemoveRef()

    member x.ForceDispose() =
        lock lockObj (fun () ->
            if Interlocked.Exchange(&refCount, 0) <> 0 then
                destroy x
        )

    interface IDisposable with
        member x.Dispose() = x.RemoveRef()

    interface IResource<'h, 'v> with
        member x.Id = id
        member x.HandleType = th
        member x.IsDisposed = x.IsDisposed
        member x.Kind = kind
        member x.AddRef() = x.AddRef()
        member x.RemoveRef() = x.RemoveRef()
        member x.Handle = x.Handle
        member x.Update(token, rt) = x.Update(token, rt)
        member x.Info = x.Info
        member x.Pointer = pointer

and ResourceCache<'h, 'v when 'v : unmanaged>(parent : Option<ResourceCache<'h, 'v>>, renderTaskLock : Option<RenderTaskLock>) =
    let store = ConcurrentDictionary<list<obj>, Resource<'h, 'v>>()
    static let hNonPrimitive =
        not typeof<'h>.IsPrimitive && not typeof<'h>.IsEnum


    let acquireOutput (old : Option<'x>) (m : aval<'a>) =
        match old with
            | None ->
                match m with
                    | :? IAdaptiveResource<'a> as om -> om.Acquire()
                    | _ -> ()

            | _ ->
                ()

    let releaseOutput  (m : aval<'a>) =
        match m with
            | :? IAdaptiveResource<'a> as om -> om.Release()
            | _ -> ()

    let acquireLock (v : 'a) =
        match renderTaskLock, v :> obj with
            | Some rt, (:? ILockedResource as l) -> rt.Add l
            | _ -> ()

    let releaseLock (v : 'a) =
        match renderTaskLock, v :> obj with
            | Some rt, (:? ILockedResource as l) -> rt.Remove l
            | _ -> ()


    let eval (m : aval<'a>) (rt : RenderToken) (token : AdaptiveToken) =
        match m with
            | :? IAdaptiveResource<'a> as om -> om.GetValue(token, rt)
            | _ -> m.GetValue(token)

    let tryGetParent (key : list<obj>) =
        match parent with
            | Some v -> v.TryGet key
            | None -> None

    member x.TryGet(key : list<obj>) =
        match store.TryGetValue(key) with
            | (true,v) -> Some v
            | _ -> None

    member x.GetOrCreateLocalWrapped<'x, 'y when 'y : unmanaged>(key : list<obj>, create : unit -> Resource<'x, 'y>, wrap : Resource<'x, 'y> -> Resource<'h, 'v>) =
        let resource =
            store.GetOrAdd(key, fun _ ->
                let res = create()
                res.OnDispose.Add(fun () -> store.TryRemove key |> ignore)
                wrap res
            )
        resource.AddRef()
        resource :> IResource<_,_>

    member x.GetOrCreateLocal(key : list<obj>, create : unit -> Resource<'h, 'v>) =
        x.GetOrCreateLocalWrapped<'h, 'v>(key, create, id)

    member x.GetOrCreate(key : list<obj>, create : unit -> Resource<'h, 'v>) =
        match tryGetParent key with
            | Some r ->
                r.AddRef()
                r :> IResource<_,_>
            | None -> x.GetOrCreateLocal(key, create)

    member x.GetOrCreateWrapped<'x, 'y when 'y : unmanaged>(key : list<obj>, create : unit -> Resource<'x, 'y>, wrap : Resource<'x, 'y> -> Resource<'h, 'v>) =
        match tryGetParent key with
            | Some r ->
                r.AddRef()
                r :> IResource<_,_>
            | None ->
                x.GetOrCreateLocalWrapped(key, create, wrap)

    member x.GetOrCreateWrapped<'a, 'x, 'y when 'y : unmanaged>(dataMod : aval<'a>, additionalKeys : list<obj>, creator : unit -> ResourceDescription<'a, 'x, 'y>, wrap : Resource<'x, 'y> -> Resource<'h, 'v>) =
        let key = (dataMod :> obj)::additionalKeys
        match tryGetParent key with
            | Some v ->
                match dataMod with
                    | :? ILockedResource as r ->
                        x.GetOrCreateLocal(key, fun () ->
                            v.AddRef()
                            let mutable oldData = None
                            { new Resource<'h, 'v>(v.Kind) with
                                member x.View (h : 'h) =
                                    v.View(h)

                                member x.GetInfo(h : 'h) =
                                    v.GetInfo h

                                member x.Create(token : AdaptiveToken, rt : RenderToken, old : Option<'h>) =
                                    let newData = dataMod.GetValue token
                                    match oldData with
                                        | Some d -> releaseLock d
                                        | None -> ()
                                    oldData <- Some newData
                                    acquireLock newData
                                    acquireOutput old dataMod

                                    let stats = v.Update(token, rt)
                                    v.Handle.GetValue()

                                member x.Destroy(h : 'h) =
                                    match oldData with
                                        | Some d -> releaseLock d; oldData <- None
                                        | None -> ()
                                    releaseOutput dataMod
                                    lock v (fun () ->
                                        v.Outputs.Remove x |> ignore
                                        v.Handle.Outputs.Remove x |> ignore
                                    )
                                    v.RemoveRef()
                            }
                        )
                    | _ ->
                        v.AddRef()
                        v :> IResource<_,_>
            | None ->
                x.GetOrCreateLocal(key, fun _ ->
                    let desc = creator()
                    let mutable ownsHandle = false
                    let mutable oldData = None
                    let xNonPrimitive = not typeof<'x>.IsPrimitive && not typeof<'x>.IsEnum


                    let resource =
                        { new Resource<'x, 'y>(desc.kind) with
                            member x.View (h : 'x) =
                                desc.view h

                            member x.GetInfo (h : 'x) =
                                desc.info h

                            member x.Create(token : AdaptiveToken, rt : RenderToken, old : Option<'x>) =
                                acquireOutput old dataMod
                                let data = eval dataMod rt token

                                match oldData with
                                    | Some d -> releaseLock d
                                    | None -> ()
                                acquireLock data
                                oldData <- Some data

                                match old with
                                    | Some old ->
                                        match data :> obj with
                                            | :? 'x as handle when xNonPrimitive ->
                                                if ownsHandle then desc.delete old
                                                ownsHandle <- false
                                                handle

                                            | _ ->
                                                if ownsHandle then
                                                    let newHandle = desc.update old data
                                                    newHandle
                                                else
                                                    let newHandle = desc.create data
                                                    ownsHandle <- true
                                                    newHandle

                                    | None ->
                                        match data :> obj with
                                            | :? 'x as handle when xNonPrimitive ->
                                                ownsHandle <- false
                                                handle
                                            | _ ->
                                                let handle = desc.create data
                                                ownsHandle <- true
                                                handle

                            member x.Destroy(h : 'x) =
                                match oldData with
                                    | Some d -> releaseLock d
                                    | _ -> ()

                                releaseOutput dataMod
                                if ownsHandle then
                                    ownsHandle <- false
                                    desc.delete h
                        }

                    wrap resource
                )

    member x.GetOrCreate<'a>(dataMod : aval<'a>, additionalKeys : list<obj>, creator : unit -> ResourceDescription<'a, 'h, 'v>) =
        x.GetOrCreateWrapped<'a, 'h, 'v>(dataMod, additionalKeys, creator, id)

    member x.GetOrCreateDependent<'a, 'b when 'a : equality and 'b : unmanaged>(res : IResource<'a, 'b>, additionalKeys : list<obj>, desc : ResourceDescription<'a, 'h, 'v>) =
        let key = (res :> obj)::additionalKeys
        match tryGetParent key with
            | Some v ->
                v.AddRef()
                v :> IResource<_,_>
            | None ->
                x.GetOrCreateLocal(key, fun _ ->
                    let mutable ownsHandle = false

                    { new Resource<'h,'v>(desc.kind) with
                        member x.View(h : 'h) =
                            desc.view h

                        member x.GetInfo (h : 'h) =
                            desc.info h

                        member x.Create(token : AdaptiveToken, rt : RenderToken, old : Option<'h>) =

                            let stats = res.Update(token, rt)
                            let data = res.Handle.GetValue()

                            match old with
                                | Some old ->
                                    match data :> obj with
                                        | :? 'h as handle when hNonPrimitive ->
                                            if ownsHandle then desc.delete old
                                            ownsHandle <- false
                                            handle

                                        | _ ->
                                            if ownsHandle then
                                                let newHandle = desc.update old data
                                                newHandle
                                            else
                                                let newHandle = desc.create data
                                                ownsHandle <- true
                                                newHandle

                                | None ->
                                    match data :> obj with
                                        | :? 'h as handle when hNonPrimitive ->
                                            ownsHandle <- false
                                            handle
                                        | _ ->
                                            let handle = desc.create data
                                            ownsHandle <- true
                                            handle

                        member x.Destroy(h : 'h) =
                            res.RemoveRef()
                            res.Outputs.Remove x |> ignore
                            if ownsHandle then
                                ownsHandle <- false
                                desc.delete h
                    }
                )


    member x.GetOrCreate<'a>(dataMod : aval<'a>, creator : unit -> ResourceDescription<'a, 'h, 'v>) =
        x.GetOrCreate(dataMod, [], creator)

    member x.Count = store.Count
    member x.Clear() =
        let remaining = store |> Seq.map (fun (KeyValue(_,r)) -> r) |> Seq.toArray
        for r in remaining do
            Log.warn "leaking resource: %A" r
            r.ForceDispose()
        store.Clear()

//type ConstantResource<'h, 'v when 'h : equality and 'v : unmanaged>(kind : ResourceKind, handle : 'h, v : 'v) =
//    inherit ConstantObject()
//    static let th = typeof<'h>

//    let h = AVal.constant handle
//    let ptr = NativePtr.alloc 1
//    do NativePtr.write ptr v

//    member x.Handle = handle

//    override x.GetHashCode() = handle.GetHashCode()

//    override x.Equals o =
//        match o with
//            | :? ConstantResource<'h, 'v> as o -> o.Handle = x.Handle
//            | _ -> false

//    interface IDisposable with
//        member x.Dispose() = ()

//    interface IResource<'h, 'v> with
//        member x.HandleType = th
//        member x.IsDisposed = false
//        member x.Kind = kind
//        member x.AddRef() = ()
//        member x.RemoveRef() = ()
//        member x.Handle = h
//        member x.Update(token, caller) = ()
//        member x.Info = ResourceInfo.Zero
//        member x.Pointer = ptr

type InputSet(o : IAdaptiveObject) =
    let l = obj()
    let inputs = ReferenceCountingSet<IAdaptiveObject>()

    member x.Add(m : IAdaptiveObject) =
        lock l (fun () ->
            if inputs.Add m then
                m.Outputs.Add o |> ignore
        )

    member x.Remove (m : IAdaptiveObject) =
        lock l (fun () ->
            if inputs.Remove m then
                m.Outputs.Remove o |> ignore
        )

type ResourceInputSet() =
    inherit AdaptiveObject()


    let mutable updateBuffer = Array.zeroCreate<IResource> 0
    let dirty = System.Collections.Generic.HashSet<IResource>()
    let all = ReferenceCountingSet<IResource>()


    let updateOne (token : AdaptiveToken) (r : IResource) (t : RenderToken) =
        r.Update(token, t)

    let updateDirty (token : AdaptiveToken) (rt : RenderToken) =
        let rec run (level : int) (token : AdaptiveToken) (rt : RenderToken) =
            let struct(buffer, cnt) =
                lock all (fun () ->
                    let cnt = dirty.Count
                    if updateBuffer.Length < cnt then
                        updateBuffer <- Array.zeroCreate<_> cnt
                    dirty.CopyTo(updateBuffer, 0)
                    dirty.Clear()
                    updateBuffer, cnt
                )

            if level > 4 && cnt > 0 then
                Log.warn "nested shit"

            if cnt > 0 then
                for i in 0..cnt-1 do
                    let res = buffer.[i]
                    buffer.[i] <- Unchecked.defaultof<_>
                    if not res.IsDisposed then
                        updateOne token res rt

                run (level + 1) token rt

        run 0 token rt

    override x.InputChangedObject(transaction, object) =
        match object with
        | :? IResource as resource ->
            lock all (fun () -> dirty.Add resource |> ignore)
        | _ ->
            ()


    member x.Count = all.Count

    member x.Add (r : IResource) =
        let needsUpdate =
            lock all (fun () ->
                if all.Add r then
                    lock r (fun () ->
                        if r.OutOfDate then
                            dirty.Add r |> ignore
                            true

                        else
                            r.Outputs.Add x |> ignore
                            false
                    )
                else
                    false
            )

        if needsUpdate then
            Log.warn "adding outdated resource: %A" r.Kind
            x.EvaluateAlways AdaptiveToken.Top (fun token ->
                updateDirty token RenderToken.Empty
            )

    member x.Remove (r : IResource) =
        lock all (fun () ->
            if all.Remove r then
                dirty.Remove r |> ignore
                lock r (fun () -> r.Outputs.Remove x |> ignore)
        )

    member x.Update (token : AdaptiveToken, rt : RenderToken) =
        x.EvaluateAlways token  (fun token ->
            if x.OutOfDate then
                updateDirty token rt
        )

    member x.Dispose () =
        lock all (fun () ->
            for r in all do
                lock r (fun () ->
                    r.Outputs.Remove x |> ignore)

            all.Clear()
            dirty.Clear()
        )

    interface IDisposable with
        member x.Dispose() = x.Dispose()