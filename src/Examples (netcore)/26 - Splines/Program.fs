﻿open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Application

[<ReflectedDefinition>]
module SplineShader =
    open FShade

    [<GLSLIntrinsic("cbrt({0})")>]
    let cbrt (v : float) : float = onlyInShaderCode "cbrt"


    let realRootsOfNormed (c2 : float) (c1 : float) (c0 : float) =
        // ------ eliminate quadric term (x = y - c2/3): x^3 + p x + q = 0
        let d = c2 * c2
        let p3 = 1.0/3.0 * (* p *)(-1.0/3.0 * d + c1)
        let q2 = 1.0/2.0 * (* q *)((2.0/27.0 * d - 1.0/3.0 * c1) * c2 + c0)
        let p3c = p3 * p3 * p3
        let shift = 1.0/3.0 * c2
        let d = q2 * q2 + p3c

        if d < 0.0 then
            let phi = 1.0 / 3.0 * Fun.Acos(-q2 / Fun.Sqrt(-p3c))
            let t = 2.0 * Fun.Sqrt(-p3)
            let r0 = t * Fun.Cos(phi) - shift
            let r1 = -t * Fun.Cos(phi + Constant.Pi / 3.0) - shift
            let r2 = -t * Fun.Cos(phi - Constant.Pi / 3.0) - shift
            (r0, r1, r2)
        else
            let d = Fun.Sqrt(d)                 // one single and one double root
            let uav = (d - q2) ** (1.0 / 3.0) - (d + q2) ** (1.0 / 3.0)
            let s0 = uav - shift
            let s1 = -0.5 * uav - shift
            (s0, s1, s1)

    let realRootsOf (c3 : float) (c2 : float) (c1 : float) (c0 : float) =
        realRootsOfNormed (c2 / c3) (c1 / c3) (c0 / c3)

    let clipSpline (a : V2d) (b : V2d) (c : V2d) (d : V2d) (tmin : ref<float>) (tmax : ref<float>) =
        
        let (txl0, txl1, txl2) = realRootsOf (d.X + 3.0*b.X - 3.0*c.X - a.X) (3.0*(a.X - 2.0*b.X + c.X)) (3.0 * (b.X - a.X)) (a.X - 1.0)
        let (txh0, txh1, txh2) = realRootsOf (d.X + 3.0*b.X - 3.0*c.X - a.X) (3.0*(a.X - 2.0*b.X + c.X)) (3.0 * (b.X - a.X)) (a.X + 1.0)
        
        let (tyl0, tyl1, tyl2) = realRootsOf (d.Y + 3.0*b.Y - 3.0*c.Y - a.Y) (3.0*(a.Y - 2.0*b.Y + c.Y)) (3.0 * (b.Y - a.Y)) (a.Y - 1.0)
        let (tyh0, tyh1, tyh2) = realRootsOf (d.Y + 3.0*b.Y - 3.0*c.Y - a.Y) (3.0*(a.Y - 2.0*b.Y + c.Y)) (3.0 * (b.Y - a.Y)) (a.Y + 1.0)

        let mutable min = 1.0
        let mutable max = 0.0

        if txl0 >= 0.0 && txl0 <= 1.0 then
            if txl0 < min then min <- txl0
            elif txl0 > max then max <- txl0

        if txl1 >= 0.0 && txl1 <= 1.0 then
            if txl1 < min then min <- txl1
            elif txl1 > max then max <- txl1
            
        if txl2 >= 0.0 && txl2 <= 1.0 then
            if txl2 < min then min <- txl2
            elif txl2 > max then max <- txl2

        if txh0 >= 0.0 && txh0 <= 1.0 then
            if txh0 < min then min <- txh0
            elif txh0 > max then max <- txh0

        if txh1 >= 0.0 && txh1 <= 1.0 then
            if txh1 < min then min <- txh1
            elif txh1 > max then max <- txh1
            
        if txh2 >= 0.0 && txh2 <= 1.0 then
            if txh2 < min then min <- txh2
            elif txh2 > max then max <- txh2


        if tyl0 >= 0.0 && tyl0 <= 1.0 then
            if tyl0 < min then min <- tyl0
            elif tyl0 > max then max <- tyl0

        if tyl1 >= 0.0 && tyl1 <= 1.0 then
            if tyl1 < min then min <- tyl1
            elif tyl1 > max then max <- tyl1
            
        if tyl2 >= 0.0 && tyl2 <= 1.0 then
            if tyl2 < min then min <- tyl2
            elif tyl2 > max then max <- tyl2

        if tyh0 >= 0.0 && tyh0 <= 1.0 then
            if tyh0 < min then min <- tyh0
            elif tyh0 > max then max <- tyh0

        if tyh1 >= 0.0 && tyh1 <= 1.0 then
            if tyh1 < min then min <- tyh1
            elif tyh1 > max then max <- tyh1
            
        if tyh2 >= 0.0 && tyh2 <= 1.0 then
            if tyh2 < min then min <- tyh2
            elif tyh2 > max then max <- tyh2

        tmin := min
        tmax := max

    let evalSpline (a : V4d) (b : V4d) (c : V4d) (d : V4d) (t : float) =
        (1.0-t)*((1.0-t)*((1.0-t)*a + t*b) + t*((1.0-t)*b + t*c)) +
        t      *((1.0-t)*((1.0-t)*b + t*c) + t*((1.0-t)*c + t*d))

    let evalProjectiveSpline (a : V4d) (b : V4d) (c : V4d) (d : V4d) (t : float) =
        let res = evalSpline a b c d t
        0.5 * (res.XY / res.W + V2d.II)

    let transformProj (viewportSize : V2i) (viewProj : M44d) (pt : V3f) =
        let a = viewProj * V4d(V3d pt, 1.0)
        V2d viewportSize * 0.5 * a.XY / a.W

    let fromConjugateDiameters (a : V2d) (b : V2d) =
        let ab = Vec.dot a b
        if abs ab < 1E-5 then
            a,b
        else
            let a2 = a.LengthSquared
            let b2 = b.LengthSquared
            let t = 0.5 * atan2 (2.0*ab) (a2 - b2)
            let ct = cos t
            let st = sin t
            let v0 = a * ct + b * st
            let v1 = b * ct - a * st
            v0,v1

    [<LocalSize(X = 64)>]
    let prepare (cpIn : V4f[]) (div : int[]) (ts : V2f[]) (count : int) (viewProj : M44d) (viewportSize : V2i) (threshold : float) =
        compute {
            let id = getGlobalId().X

            if id < count then
                let i0 = 4 * id
                let c3 = V4d cpIn.[i0 + 3]
                if c3.W <= 0.0 then
                    // circle
                    let c = transformProj viewportSize viewProj cpIn.[i0 + 0].XYZ
                    let rx = transformProj viewportSize viewProj (cpIn.[i0 + 0].XYZ + cpIn.[i0 + 1].XYZ) - c 
                    let ry = transformProj viewportSize viewProj (cpIn.[i0 + 0].XYZ + cpIn.[i0 + 2].XYZ) - c 
                    
                    let rx,ry = fromConjugateDiameters rx ry
                    let a = Vec.length rx
                    let b = Vec.length ry
                    let lam = (a-b)/(a+b)
                    let l = lam*lam
                    let len = (a+b) * Constant.Pi //* (1.0 + (3.0 * l)/(10.0 + sqrt (4.0 - 3.0 * l)))

                    let dd = clamp 1 8192 (int (len / threshold))
                    div.[id] <- dd
                    ts.[id] <- V2f(0.0f, float32 Constant.PiTimesTwo)
                else
                    let p0 = viewProj * V4d cpIn.[i0 + 0]
                    let p1 = viewProj * V4d cpIn.[i0 + 1]
                    let p2 = viewProj * V4d cpIn.[i0 + 2]
                    let p3 = viewProj * c3

                    // TODO: proper clipping
                    let mutable tmin = 0.0
                    let mutable tmax = 1.0
                    // !!! clipSline is wrong !!!
                    // clipSpline (p0.XY / p0.W) (p1.XY / p1.W) (p2.XY / p2.W) (p3.XY / p3.W) &&tmin &&tmax
                

                    let steps = 8
                    let mutable tc = tmin
                    let mutable pc = evalProjectiveSpline p0 p1 p2 p3 tc
                    let step = (tmax - tmin) / float steps
                    let mutable approxLen = 0.0
                    for i in 0 .. steps - 1 do
                        let tn = tc + step
                        let pn = evalProjectiveSpline p0 p1 p2 p3 tn
                        approxLen <- approxLen + Vec.length(V2d viewportSize * (pn - pc))
                        tc <- tn
                        pc <- pn

                    let dd = clamp 1 8192 (int (approxLen / threshold))
                    div.[id] <- dd
                    ts.[id] <- V2f(tmin, tmax)
        }
        
    [<LocalSize(X = 64)>]
    let evalulate (scannedDiv : int[]) (scannedCount : int) (ts : V2f[]) (cps : V4f[]) (cols : V4f[]) (lines : V4f[]) (outCols : V4f[]) =
        compute {
            let id = getGlobalId().X
            let count = scannedDiv.[scannedCount - 1]
            if id < count then
                let mutable l = 0
                let mutable r = scannedCount - 1
                let mutable m = (l + r) / 2
                while l <= r do
                    let s = scannedDiv.[m]
                    if s < id then l <- m + 1
                    elif s > id then r <- m - 1
                    else
                        l <- m + 1
                        r <- m
                    m <- (l + r) / 2
                    
                let splineId = l

                let baseIndex = 
                    if splineId > 0 then scannedDiv.[splineId - 1]
                    else 0

                let indexInSpline =
                    id - baseIndex

                let cnt = scannedDiv.[splineId] - baseIndex

                let ts = ts.[splineId]
                let tmin = float ts.X
                let tmax = float ts.Y

                let ts = (tmax - tmin) / float cnt
                let t0 = float indexInSpline * ts + tmin
                let t1 = t0 + ts

                let i0 = 4 * splineId
                let c3 = V4d cps.[i0 + 3]
                if c3.W <= 0.0 then
                    let center = V4d cps.[i0 + 0]
                    let rx = V4d cps.[i0 + 1]
                    let ry = V4d cps.[i0 + 2]

                    let c0 = cols.[i0 + 0]
                    let c1 = cols.[i0 + 3]
                    let p0 = center.XYZ + cos t0 * rx.XYZ + sin t0 * ry.XYZ
                    let p1 = center.XYZ + cos t1 * rx.XYZ + sin t1 * ry.XYZ
                    
                    lines.[2*id+0] <- V4f(V3f p0, 1.0f)
                    lines.[2*id+1] <- V4f(V3f p1, 1.0f)
                    outCols.[2*id+0] <- c0
                    outCols.[2*id+1] <- c1
                else
                
                    let a = V4d cps.[i0 + 0]
                    let b = V4d cps.[i0 + 1]
                    let c = V4d cps.[i0 + 2]
                    let d = V4d cps.[i0 + 3]
                    
                    let c0 = cols.[i0 + 0]
                    let c1 = cols.[i0 + 3]
                    let p0 = evalSpline a b c d t0
                    let p1 = evalSpline a b c d t1

                    lines.[2*id+0] <- V4f p0
                    lines.[2*id+1] <- V4f p1
                    outCols.[2*id+0] <- c0
                    outCols.[2*id+1] <- c1
        }


module Sg =
    open Aardvark.Base.Ag
    open Aardvark.SceneGraph.Semantics

    type SplineNode(viewportSize : IMod<V2i>, threshold : IMod<float>, controlPoints : IMod<V4d[]>, controlColors : IMod<C4b[]>) =
        interface ISg
        member x.ViewportSize = viewportSize
        member x.Threshold = threshold
        member x.ControlPoints = controlPoints
        member x.ControlColors = controlColors

    [<Semantic>]
    type SplineSemantics() =
        
        static let cache = System.Collections.Concurrent.ConcurrentDictionary<IRuntime, ParallelPrimitives * IComputeShader * IComputeShader>()

        static let get(runtime : IRuntime) =
            cache.GetOrAdd(runtime, fun runtime ->
                let prepare = runtime.CreateComputeShader SplineShader.prepare
                let evaluate = runtime.CreateComputeShader SplineShader.evalulate
                let prim = ParallelPrimitives runtime
                prim, prepare, evaluate
            )

        static let subdiv (runtime : IRuntime) (threshold : IMod<float>) (size : IMod<V2i>) (viewProj : IMod<Trafo3d>) (cpsArray : IMod<V4d[]>) (colorArray : IMod<C4b[]>) : IOutputMod<IBuffer<V4f> * IBuffer<V4f> * int> =
            let prim, prepare, evaluate = get runtime
            
            
            let mutable res : Option<IBuffer<V4f>> = None
            let mutable res2 : Option<IBuffer<V4f>> = None
        
            let compute (cols : IBuffer<V4f>) (cps : IBuffer<V4f>) (t : AdaptiveToken) =
                let cpsArray = cpsArray.GetValue t
                let cnt = cpsArray.Length / 4
                use div = runtime.CreateBuffer<int>(cnt)
                use ts = runtime.CreateBuffer<V2f>(cnt)
                let viewProj = viewProj.GetValue t
                let size = size.GetValue t
                let threshold = threshold.GetValue t

                use ip = runtime.NewInputBinding prepare
                ip.["cpIn"] <- cps
                ip.["div"] <- div
                ip.["ts"] <- ts
                ip.["count"] <- cnt
                ip.["viewProj"] <- viewProj
                ip.["viewportSize"] <- size
                ip.["threshold"] <- threshold
                ip.Flush()

                runtime.Run [
                    ComputeCommand.Bind prepare
                    ComputeCommand.SetInput ip
                    ComputeCommand.Dispatch(ceilDiv cnt 64)
                ]


                prim.Scan(<@ (+) @>, div, div)
            
                let total = div.[cnt-1 .. cnt-1].Download().[0]
                let cap = max 16 (Fun.NextPowerOfTwo(total * 2))
            
                let res =
                    match res with
                        | Some b when b.Count = cap -> 
                            b
                        | Some b ->
                            Log.warn "size: %d" cap
                            b.Dispose()
                            let b = runtime.CreateBuffer<V4f>(cap)
                            res <- Some b
                            b
                        | None ->
                            Log.warn "size: %d" cap
                            let b = runtime.CreateBuffer<V4f>(cap)
                            res <- Some b
                            b

                let res2 =
                    match res2 with
                        | Some b when b.Count = cap -> 
                            b
                        | Some b ->
                            Log.warn "size: %d" cap
                            b.Dispose()
                            let b = runtime.CreateBuffer<V4f>(cap)
                            res2 <- Some b
                            b
                        | None ->
                            Log.warn "size: %d" cap
                            let b = runtime.CreateBuffer<V4f>(cap)
                            res2 <- Some b
                            b
                        

                //let evalulate (scannedDiv : int[]) (scannedCount : int) (ts : V2f[]) (cps : V4f[]) (lines : V4f[])
                use ip = runtime.NewInputBinding evaluate
                ip.["scannedDiv"] <- div
                ip.["scannedCount"] <- cnt
                ip.["ts"] <- ts
                ip.["cps"] <- cps
                ip.["cols"] <- cols
                ip.["lines"] <- res
                ip.["outCols"] <- res2
                ip.Flush()

                runtime.Run [
                    ComputeCommand.Bind evaluate
                    ComputeCommand.SetInput ip
                    ComputeCommand.Dispatch(ceilDiv total 64)
                ]

                res, res2, total

            let overall =
                let mutable cps : Option<IBuffer<V4f>> = None
                let mutable cls : Option<IBuffer<V4f>> = None
                let mutable cpsDirty = true
                { new AbstractOutputMod<IBuffer<V4f> * IBuffer<V4f> * int>() with
                    override x.InputChanged(_,o) =
                        if o = (cpsArray :> IAdaptiveObject) then cpsDirty <- true
                        ()

                    member x.Create() = 
                        Log.warn "create"
                        transact (fun () -> x.MarkOutdated())

                    member x.Destroy() = 
                        Log.warn "destroy"
                        cps |> Option.iter (fun d -> d.Dispose())
                        cps <- None
                        cls <- None
                        res |> Option.iter (fun d -> d.Dispose())
                        res <- None
                        res2 <- None
                        cpsDirty <- true
                        transact (fun () -> x.MarkOutdated())

                    member x.Compute(t,rt) = 
                        
                        if cpsDirty then
                            cpsDirty <- false
                            let cpsArray = cpsArray.GetValue t
                            let colArray = colorArray.GetValue t
                            let cap = Fun.NextPowerOfTwo(cpsArray.Length) |> max 16
                            let cps = 
                                match cps with
                                | Some cps when cps.Count = cap -> cps
                                | Some cp ->
                                    cp.Dispose()
                                    let cp = runtime.CreateBuffer<V4f>(cap)
                                    cps <- Some cp
                                    cp
                                | None ->
                                    let cp = runtime.CreateBuffer<V4f>(cap)
                                    cps <- Some cp
                                    cp
                            let cls = 
                                match cls with
                                | Some cls when cls.Count = cap -> cls
                                | Some cp ->
                                    cp.Dispose()
                                    let cp = runtime.CreateBuffer<V4f>(cap)
                                    cls <- Some cp
                                    cp
                                | None ->
                                    let cp = runtime.CreateBuffer<V4f>(cap)
                                    cls <- Some cp
                                    cp   
                            
                            cps.Upload(cpsArray |> Array.map (fun v -> V4f v))
                            cls.Upload(colArray |> Array.map (fun c -> c.ToC4f().ToV4f()))

                        compute cls.Value cps.Value t
                }
            

            overall :> IOutputMod<_>
            //let buffer =
            //    { new AbstractOutputMod<IBuffer>() with
            //        member x.Create() = overall.Acquire()
            //        member x.Destroy() = overall.Release()
        
            //        member x.Compute(t,rt) = 
            //            let (b,_) = overall.GetValue(t)
            //            b.Buffer :> IBuffer
            //    }
            //let count =
            //    { new AbstractOutputMod<int>() with
            //        member x.Create() = overall.Acquire()
            //        member x.Destroy() = overall.Release()
        
            //        member x.Compute(t,rt) = 
            //            let (_,c) = overall.GetValue(t)
            //            2 * c
            //    }
            //buffer :> IOutputMod<_>, count :> IOutputMod<_>
            
        member x.GlobalBoundingBox(s : SplineNode) =
            let t = s.ModelTrafo
            Mod.map2 (fun (trafo : Trafo3d) (cps : V4d[]) -> Box3d(cps |> Array.map (Vec.xyz >> trafo.Forward.TransformPos))) t s.ControlPoints

        member x.LocalBoundingBox(s : SplineNode) =
            s.ControlPoints |> Mod.map (Seq.map Vec.xyz >> Box3d)

        member x.RenderObjects(s : SplineNode) : aset<IRenderObject> =
            let runtime = s.Runtime
            let viewProj = Mod.map2 (*) s.ViewTrafo s.ProjTrafo
            let mvp = Mod.map2 (*) s.ModelTrafo viewProj

            //let mm = s.ControlPoints |> Mod.map (subdiv runtime s.Threshold s.ViewportSize mvp)
            let cps = s.ControlPoints
            let cls : IMod<C4b[]> = s.ControlColors

            let mutable last : Option<IOutputMod<_>> = None

            let bind (view : IBuffer<V4f> * IBuffer<V4f> * int -> 'a) =
                { new AbstractOutputMod<'a>() with

                    member x.Create() =
                        let om = subdiv runtime s.Threshold s.ViewportSize mvp cps cls
                        om.Acquire()
                        last <- Some om
                        transact (fun () -> x.MarkOutdated())

                    member x.Destroy() =
                        match last with
                        | Some o -> 
                            o.RemoveOutput x
                            o.Release()
                            last <- None
                        | _ -> 
                            ()
                        transact (fun () -> x.MarkOutdated())

                    member x.Compute(t,rt) = 
                        let om = 
                            if Option.isNone last then
                                let om = subdiv runtime s.Threshold s.ViewportSize mvp cps cls
                                om.Acquire()
                                last <- Some om
                                om
                            else
                                last.Value
                                
                        let r = om.GetValue(t)
                        view r
                }
                


            let posbuffer = bind (fun (b,_,_) -> b.Buffer :> IBuffer)
            let colbuffer = bind (fun (_,b,_) -> b.Buffer :> IBuffer)
            let count = bind (fun (_,_,c) -> 2*c)
                

            let o = RenderObject.create()
            o.DrawCallInfos <- count |> Mod.map (fun c -> [DrawCallInfo(FaceVertexCount = c, InstanceCount = 1)])
            o.Mode <- IndexedGeometryMode.LineList
            o.VertexAttributes <-
                let oa = o.VertexAttributes
                let posView = BufferView(posbuffer, typeof<V4f>)
                let colView = BufferView(colbuffer, typeof<V4f>)
                { new IAttributeProvider with
                    member x.Dispose() = oa.Dispose()
                    member x.All = oa.All
                    member x.TryGetAttribute(sem : Symbol) =
                        if sem = DefaultSemantic.Positions then Some posView
                        elif sem = DefaultSemantic.Colors then Some colView
                        else oa.TryGetAttribute sem
                }

            ASet.single (o :> IRenderObject)



[<EntryPoint>]
let main argv = 
    Ag.initialize()
    Aardvark.Init()
    
    let win =
        window {
            backend Backend.GL
            display Display.Mono
            debug false
            samples 8
        }




    let cps =
        Mod.init [|
            V4d.OOOI; V4d.OOII; V4d.IOII; V4d.IOOI
            V4d(0,1,0,1); V4d(0,1,1,1); V4d(1,1,-1,1); V4d(1,1,0,1)

            V4d(0,0,1,1); V4d(2,0,0,1); V4d(0,2,0,1); V4d.Zero

        |]

    let cls = 
        Mod.init [|
            C4b.Red; C4b.Red; C4b.Red; C4b.Red; 
            C4b.Green; C4b.Green; C4b.Green; C4b.Green;
            C4b.Blue; C4b.Blue; C4b.Blue; C4b.Blue;         
        |]

    let threshold = Mod.init 10.0
    let active = Mod.init true

    let rand = RandomSystem()
    let bounds = Box3d(-V3d.III, V3d.III)

    win.Keyboard.DownWithRepeats.Values.Add (fun k ->
        match k with
        | Keys.OemPlus -> transact (fun () -> threshold.Value <- threshold.Value + 1.0); Log.warn "threshold: %A" threshold.Value
        | Keys.OemMinus -> transact (fun () -> threshold.Value <- max 1.0 (threshold.Value - 1.0)); Log.warn "threshold: %A" threshold.Value
        
        | Keys.Space -> transact (fun () -> active.Value <- not active.Value)
        
        | Keys.Enter -> 
            transact (fun () -> 
                if rand.UniformDouble() > 0.5 then
                    cps.Value <- Array.append cps.Value (Array.init 4 (fun _ -> V4d(rand.UniformV3d(bounds), 1.0)))
                    cls.Value <- Array.append cls.Value (Array.init 4 (fun _ -> rand.UniformC3f().ToC4f().ToC4b()))
                else
                    cps.Value <- Array.append cps.Value 
                        [|
                            rand.UniformV3d(bounds).XYZO
                            (rand.UniformV3dDirection() * 5.0).XYZO
                            (rand.UniformV3dDirection() * 0.5).XYZO
                            V4d.NONO
                        |]
                    cls.Value <- Array.append cls.Value (Array.init 4 (fun _ -> rand.UniformC3f().ToC4f().ToC4b()))
            )
        | Keys.Back -> 
            if cps.Value.Length > 4 then
                transact (fun () -> 
                    cps.Value <- Array.take (cps.Value.Length - 4) cps.Value
                )
        
        | _ -> ()
    )

    let sg =
        Sg.ofList [
            for x in 0 .. 0 do
                yield 
                    Sg.SplineNode(win.Sizes, threshold, cps,cls) :> ISg
                    |> Sg.uniform "LineWidth" (Mod.constant 6.0)
                    |> Sg.translate (float x * 2.0) 0.0 0.0
                    |> Sg.shader {
                        do! DefaultSurfaces.trafo
                        do! DefaultSurfaces.vertexColor
                        do! DefaultSurfaces.thickLine
                        do! DefaultSurfaces.thickLineRoundCaps
                    }
        ]

    let sg =
        active 
        |> Mod.map (function true -> sg | false -> Sg.empty)
        |> Sg.dynamic


    win.Scene <- sg
    win.Run()


    0
