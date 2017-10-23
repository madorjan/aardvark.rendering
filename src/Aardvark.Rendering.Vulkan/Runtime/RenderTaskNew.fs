﻿namespace Aardvark.Rendering.Vulkan

open System
open System.Threading
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Rendering.Vulkan
open Microsoft.FSharp.NativeInterop
open Aardvark.Base.Incremental
open System.Diagnostics
open System.Collections.Generic
open Aardvark.Base.Runtime

#nowarn "9"
#nowarn "51"

type ICommandStreamResource =
    inherit IResourceLocation<VKVM.CommandStream>
    abstract member Stream : VKVM.CommandStream
    abstract member Resources : seq<IResourceLocation>

    abstract member GroupKey : list<obj>
    abstract member BoundingBox : IMod<Box3d>

module RenderCommands =
 
    [<RequireQualifiedAccess>]
    type Tree<'a> =
        | Empty
        | Leaf of 'a
        | Node of Option<'a> * list<Tree<'a>> 
     
    type ClearValues =
        {
            colors  : Map<Symbol, IMod<C4f>>
            depth   : Option<IMod<float>>
            stencil : Option<IMod<int>>
        }

    type PipelineState =
        {
            surface             : Aardvark.Base.Surface

            depthTest           : IMod<DepthTestMode>
            cullMode            : IMod<CullMode>
            blendMode           : IMod<BlendMode>
            fillMode            : IMod<FillMode>
            stencilMode         : IMod<StencilMode>
            multisample         : IMod<bool>
            writeBuffers        : Option<Set<Symbol>>
            globalUniforms      : IUniformProvider

            geometryMode        : IndexedGeometryMode
            vertexInputTypes    : Map<Symbol, Type>
            perGeometryUniforms : Map<string, Type>
        }
   

    type Geometry =
        {
            vertexAttributes    : Map<Symbol, IMod<IBuffer>>
            indices             : Option<Aardvark.Base.BufferView>
            uniforms            : Map<string, IMod>
            call                : IMod<list<DrawCallInfo>>
        }

    type TreeRenderObject(pipe : PipelineState, geometries : IMod<Tree<Geometry>>) =
        let id = newId()

        member x.Pipeline = pipe
        member x.Geometries = geometries

        interface IRenderObject with
            member x.AttributeScope = Ag.emptyScope
            member x.Id = id
            member x.RenderPass = RenderPass.main
            

    type RenderCommand =
        | Objects of aset<IRenderObject>
        | ViewDependent of pipeline : PipelineState * (Trafo3d -> Trafo3d -> list<Geometry>)

    type Command = 
        | Render of objects : aset<IRenderObject>
        | Clear of values : ClearValues
        | Blit of sourceAttachment : Symbol * target : IBackendTextureOutputView

    type PreparedPipelineState =
        {
            ppPipeline  : INativeResourceLocation<VkPipeline>
            ppLayout    : PipelineLayout
        }

    type PreparedGeometry =
        {
            pgOriginal      : Geometry
            pgDescriptors   : INativeResourceLocation<DescriptorSetBinding>
            pgAttributes    : INativeResourceLocation<VertexBufferBinding>
            pgIndex         : Option<INativeResourceLocation<IndexBufferBinding>>
            pgCall          : INativeResourceLocation<DrawCall>
            pgResources     : list<IResourceLocation>
        }

    type ResourceManager with
        member x.PreparePipelineState (renderPass : RenderPass, state : PipelineState) =
            let layout, program = x.CreateShaderProgram(renderPass, state.surface)

            let inputs = 
                layout.PipelineInfo.pInputs |> List.map (fun p ->
                    let name = Symbol.Create p.name
                    match Map.tryFind name state.vertexInputTypes with
                        | Some t -> (name, (false, t))
                        | None -> failf "could not get shader input %A" name
                )
                |> Map.ofList

            let inputState =
                x.CreateVertexInputState(layout.PipelineInfo, Mod.constant (VertexInputState.ofTypes inputs))

            let inputAssembly =
                x.CreateInputAssemblyState(Mod.constant state.geometryMode)

            let rasterizerState =
                x.CreateRasterizerState(state.depthTest, state.cullMode, state.fillMode)

            let colorBlendState =
                x.CreateColorBlendState(renderPass, state.writeBuffers, state.blendMode)

            let depthStencil =
                let depthWrite = 
                    match state.writeBuffers with
                        | None -> true
                        | Some s -> Set.contains DefaultSemantic.Depth s
                x.CreateDepthStencilState(depthWrite, state.depthTest, state.stencilMode)

            let pipeline = 
                x.CreatePipeline(
                    program,
                    renderPass,
                    inputState,
                    inputAssembly,
                    rasterizerState,
                    colorBlendState,
                    depthStencil,
                    state.writeBuffers
                )
            {
                ppPipeline  = pipeline
                ppLayout    = layout
            }

        member x.PrepareGeometry(state : PreparedPipelineState, g : Geometry) : PreparedGeometry =
            let resources = System.Collections.Generic.List<IResourceLocation>()

            let layout = state.ppLayout

            let descriptorSets, additionalResources = 
                x.CreateDescriptorSets(layout, UniformProvider.ofMap g.uniforms)

            resources.AddRange additionalResources

            let vertexBuffers = 
                layout.PipelineInfo.pInputs 
                    |> List.sortBy (fun i -> i.location) 
                    |> List.map (fun i ->
                        let sem = i.semantic 
                        match Map.tryFind sem g.vertexAttributes with
                            | Some b ->
                                x.CreateBuffer(b), 0L
                            | None ->
                                failf "geometry does not have buffer %A" sem
                    )

            let dsb = x.CreateDescriptorSetBinding(layout, Array.toList descriptorSets)
            let vbb = x.CreateVertexBufferBinding(vertexBuffers)

            let isIndexed, ibo =
                match g.indices with
                    | Some ib ->
                        let b = x.CreateIndexBuffer ib.Buffer
                        let ibb = x.CreateIndexBufferBinding(b, VkIndexType.ofType ib.ElementType)
                        resources.Add ibb
                        true, ibb |> Some
                    | None ->
                        false, None

            let call = x.CreateDrawCall(isIndexed, g.call)

            resources.Add dsb
            resources.Add vbb
            resources.Add call



            {
                pgOriginal      = g
                pgDescriptors   = dsb
                pgAttributes    = vbb
                pgIndex         = ibo
                pgCall          = call
                pgResources     = CSharpList.toList resources
            }

    type TreeCommandStreamResource(owner, key, pipe : PipelineState, things : IMod<Tree<Geometry>>, resources : ResourceSet, manager : ResourceManager, renderPass : RenderPass, stats : nativeptr<V2i>) =
        inherit AbstractResourceLocation<VKVM.CommandStream>(owner, key)
         
        let id = newId()

        let mutable stream = Unchecked.defaultof<VKVM.CommandStream>
        let mutable entry = Unchecked.defaultof<VKVM.CommandStream>
        let preparedPipeline = manager.PreparePipelineState(renderPass, pipe)

        let bounds = lazy (Mod.constant Box3d.Invalid)
        let allResources = ReferenceCountingSet<IResourceLocation>()
        let mutable state = Tree.Empty

        let isActive =
            let isActive = NativePtr.alloc 1
            NativePtr.write isActive 1
            isActive

        let prepare (g : Geometry) =
            let res = manager.PrepareGeometry(preparedPipeline, g)
            for r in res.pgResources do 
                if allResources.Add r then 
                    resources.AddAndUpdate r
                    r.Update(AdaptiveToken.Top) |> ignore

            let stream = new VKVM.CommandStream()
            stream.IndirectBindDescriptorSets(res.pgDescriptors.Pointer) |> ignore
            stream.IndirectBindVertexBuffers(res.pgAttributes.Pointer) |> ignore
            match res.pgIndex with
                | Some ibb -> stream.IndirectBindIndexBuffer(ibb.Pointer) |> ignore
                | None -> ()
            stream.IndirectDraw(stats, isActive, res.pgCall.Pointer) |> ignore


            res, stream

        let release (pg : PreparedGeometry, stream : VKVM.CommandStream) =
            for r in pg.pgResources do
                if allResources.Remove r then 
                    resources.Remove r
            stream.Dispose()

        let isIdentical (pg : PreparedGeometry, stream : VKVM.CommandStream) (o : Geometry) =
            System.Object.ReferenceEquals(pg.pgOriginal, o)

        let update (t : Tree<Geometry>) =
            
            let rec destroy (t : Tree<_>) =
                match t with
                    | Tree.Empty -> ()
                    | Tree.Leaf v -> release v
                    | Tree.Node(s,c) ->
                        s |> Option.iter release
                        c |> List.iter destroy

            let rec update (o : Tree<_>) (n : Tree<Geometry>) =
                match o, n with
                    
                    | _, Tree.Empty ->
                        destroy o
                        Tree.Empty

                    | Tree.Empty, Tree.Leaf g -> Tree.Leaf (prepare g)
                    | Tree.Empty, Tree.Node(self, children) ->
                        let self =
                            match self with
                                | Some self -> Some (prepare self)
                                | None -> None
                        let children = children |> List.map (update Tree.Empty)
                        Tree.Node(self, children)


                    | Tree.Leaf o, Tree.Leaf n -> 
                        if isIdentical o n then 
                            Tree.Leaf o
                        else
                            release o
                            Tree.Leaf(prepare n)

                    | Tree.Leaf o, Tree.Node(ns,nc) ->
                        let self = 
                            match ns with
                                | Some ns ->
                                    if isIdentical o ns then 
                                        Some o
                                    else
                                        release o
                                        Some (prepare ns)
                                | None ->
                                    None
                        let children = nc |> List.map (update Tree.Empty)
                        Tree.Node(self, children)

                    | Tree.Node(os, oc), Tree.Leaf(n) ->
                        oc |> List.iter destroy
                        let self = 
                            match os with
                                | None -> prepare n
                                | Some os ->
                                    if isIdentical os n then os
                                    else
                                        release os
                                        prepare n
                        Tree.Leaf self
                    | Tree.Node(os, oc), Tree.Node(ns, nc) ->
                        let self = 
                            match os, ns with
                                | None, Some ns -> Some (prepare ns)
                                | Some os, None -> release os; None
                                | None, None -> None
                                | Some os, Some ns ->
                                    if isIdentical os ns then 
                                        Some os
                                    else
                                        release os
                                        Some (prepare ns)
                        let children = List.map2 update oc nc
                        Tree.Node(self, children)
            
            state <- update state t

            let rec link (last : VKVM.CommandStream) (t : Tree<PreparedGeometry * VKVM.CommandStream>) =
                match t with
                    | Tree.Empty -> last
                    | Tree.Leaf(_,v) -> 
                        last.Next <- Some v
                        v
                    | Tree.Node(Some(_,s), children) ->
                        last.Next <- Some s
                        children |> List.fold link s
                    | Tree.Node(None, children) ->
                        children |> List.fold link last
                        
            let final = link entry state
            final.Next <- None

        member x.Stream = stream
        member x.GroupKey = [preparedPipeline.ppPipeline :> obj; id :> obj]
        member x.BoundingBox = bounds.Value

        interface ICommandStreamResource with
            member x.Stream = x.Stream
            member x.Resources = allResources :> seq<_>
            member x.GroupKey = x.GroupKey
            member x.BoundingBox = x.BoundingBox

        override x.Create() =
            stream <- new VKVM.CommandStream()
            entry <- new VKVM.CommandStream()
            stream.Call(entry) |> ignore

            if allResources.Add preparedPipeline.ppPipeline then resources.Add preparedPipeline.ppPipeline
            
        override x.Destroy() = 
            stream.Dispose()
            entry.Dispose()
            for r in allResources do resources.Remove r
            allResources.Clear()
            
        override x.GetHandle token =
            let tree = things.GetValue token
            update tree
            { handle = stream; version = 0 }   



module RenderTaskNew =

    [<AbstractClass; Sealed; Extension>]
    type IRenderObjectExts private() =
        [<Extension>]
        static member ComputeBoundingBox (o : IRenderObject) : IMod<Box3d> =
            match o with
                | :? RenderObject as o ->
                    match Ag.tryGetAttributeValue o.AttributeScope "GlobalBoundingBox" with
                        | Success box -> box
                        | _ -> failwith "[Vulkan] could not get BoundingBox for RenderObject"
                    
                | :? MultiRenderObject as o ->
                    o.Children |> List.map IRenderObjectExts.ComputeBoundingBox |> Mod.mapN Box3d

                | :? PreparedMultiRenderObject as o ->
                    o.Children |> List.map IRenderObjectExts.ComputeBoundingBox |> Mod.mapN Box3d
                    
                | :? PreparedRenderObject as o ->
                    IRenderObjectExts.ComputeBoundingBox o.original

                | _ ->
                    failf "invalid renderobject %A" o

    module CommandStreams = 
        type CommandStreamResource(owner, key, o : IRenderObject, resources : ResourceSet, manager : ResourceManager, renderPass : RenderPass, stats : nativeptr<V2i>) =
            inherit AbstractResourceLocation<VKVM.CommandStream>(owner, key)
         
            let mutable stream = Unchecked.defaultof<VKVM.CommandStream>
            let mutable prep : PreparedMultiRenderObject = Unchecked.defaultof<_>

            let compile (o : IRenderObject) =
                let o = manager.PrepareRenderObject(renderPass, o)
                for o in o.Children do
                    for r in o.resources do resources.Add r
                                
                    stream.IndirectBindPipeline(o.pipeline.Pointer) |> ignore
                    stream.IndirectBindDescriptorSets(o.descriptorSets.Pointer) |> ignore

                    match o.indexBuffer with
                        | Some ib ->
                            stream.IndirectBindIndexBuffer(ib.Pointer) |> ignore
                        | None ->
                            ()

                    stream.IndirectBindVertexBuffers(o.vertexBuffers.Pointer) |> ignore
                    stream.IndirectDraw(stats, o.isActive.Pointer, o.drawCalls.Pointer) |> ignore
                o



            let bounds = lazy (o.ComputeBoundingBox())


            member x.Stream = stream
            member x.Object = prep
            member x.GroupKey = [prep.Children.[0].pipeline :> obj; prep.Id :> obj]
            member x.BoundingBox = bounds.Value

            interface ICommandStreamResource with
                member x.Stream = x.Stream
                member x.Resources = prep.Children |> Seq.collect (fun c -> c.resources)
                member x.GroupKey = x.GroupKey
                member x.BoundingBox = x.BoundingBox

            override x.Create() =
                stream <- new VKVM.CommandStream()
                let p = compile o
                prep <- p

            override x.Destroy() = 
                stream.Dispose()
                for o in prep.Children do
                    for r in o.resources do resources.Remove r
                prep <- Unchecked.defaultof<_>

            override x.GetHandle _ = 
                { handle = stream; version = 0 }   

    module Compiler = 
        open CommandStreams

        type RenderObjectCompiler(manager : ResourceManager, renderPass : RenderPass) =
            inherit ResourceSet()

            let stats : nativeptr<V2i> = NativePtr.alloc 1
            let cache = ResourceLocationCache<VKVM.CommandStream>(manager.ResourceUser)
            let mutable version = 0

            override x.InputChanged(t ,i) =
                base.InputChanged(t, i)
                match i with
                    | :? IResourceLocation<UniformBuffer> -> ()
                    | :? IResourceLocation -> version <- version + 1
                    | _ -> ()
 
            member x.Dispose() =
                cache.Clear()

            member x.Compile(o : IRenderObject) : ICommandStreamResource =
                let call = 
                    cache.GetOrCreate([o :> obj], fun owner key ->
                        match o with
                            | :? RenderCommands.TreeRenderObject as o ->
                                new RenderCommands.TreeCommandStreamResource(owner, key, o.Pipeline, o.Geometries, x, manager, renderPass, stats) :> ICommandStreamResource
                            | _ -> 
                                new CommandStreamResource(owner, key, o, x, manager, renderPass, stats) :> ICommandStreamResource
                    )
                call.Acquire()
                call |> unbox<ICommandStreamResource>
            
            member x.CurrentVersion = version

    module ChangeableCommandBuffers = 
        open Compiler

        [<AbstractClass>]
        type AbstractChangeableCommandBuffer(manager : ResourceManager, pool : CommandPool, renderPass : RenderPass, viewports : IMod<Box2i[]>) =
            inherit Mod.AbstractMod<CommandBuffer>()

            let device = pool.Device
            let compiler = RenderObjectCompiler(manager, renderPass)
            let mutable resourceVersion = 0
            let mutable cmdVersion = -1
            let mutable cmdViewports = [||]

            let cmdBuffer = pool.CreateCommandBuffer(CommandBufferLevel.Secondary)
            let dirty = HashSet<ICommandStreamResource>()

            abstract member Release : unit -> unit
            abstract member Prolog : VKVM.CommandStream
            abstract member Sort : AdaptiveToken -> bool
            default x.Sort _ = false

            override x.InputChanged(t : obj, o : IAdaptiveObject) =
                match o with
                    | :? ICommandStreamResource as r ->
                        lock dirty (fun () -> dirty.Add r |> ignore)
                    | _ ->
                        ()

            member x.Compile(o : IRenderObject) =
                let res = compiler.Compile(o)
                lock x (fun () ->
                    let o = x.OutOfDate
                    try x.EvaluateAlways AdaptiveToken.Top (fun t -> res.Update(t) |> ignore; res)
                    finally x.OutOfDate <- o
                )

            member x.Changed() =
                cmdVersion <- -1
                x.MarkOutdated()
            

            member x.Destroy(r : ICommandStreamResource) =
                lock dirty (fun () -> dirty.Remove r |> ignore)
                r.Release()

            member x.Dispose() =
                compiler.Dispose()
                dirty.Clear()
                cmdBuffer.Dispose()

            override x.Compute (t : AdaptiveToken) =
                // update all dirty programs 
                let dirty =
                    lock dirty (fun () ->
                        let res = dirty |> HashSet.toArray
                        dirty.Clear()
                        res
                    )

                for d in dirty do
                    d.Update(t) |> ignore

                // update all resources
                compiler.Update t |> ignore
                resourceVersion <- compiler.CurrentVersion

                // refill the CommandBuffer (if necessary)
                let vps = viewports.GetValue t
                let contentChanged      = cmdVersion < 0 || dirty.Length > 0
                let viewportChanged     = cmdViewports <> vps
                let versionChanged      = cmdVersion >= 0 && resourceVersion <> cmdVersion
                let orderChanged        = x.Sort t

                if contentChanged || versionChanged || viewportChanged || orderChanged then
                    let first = x.Prolog
                    let cause =
                        String.concat "; " [
                            if contentChanged then yield "content"
                            if versionChanged then yield "resources"
                            if viewportChanged then yield "viewport"
                            if orderChanged then yield "order"
                        ]
                        |> sprintf "{ %s }"

                    Log.line "[Vulkan] recompile commands: %s" cause
                    cmdViewports <- vps
                    cmdVersion <- resourceVersion

                    if viewportChanged then
                        first.SeekToBegin()
                        first.SetViewport(0u, vps |> Array.map (fun b -> VkViewport(float32 b.Min.X, float32 b.Min.X, float32 (1 + b.SizeX), float32 (1 + b.SizeY), 0.0f, 1.0f))) |> ignore
                        first.SetScissor(0u, vps |> Array.map (fun b -> VkRect2D(VkOffset2D(b.Min.X, b.Min.X), VkExtent2D(1 + b.SizeX, 1 + b.SizeY)))) |> ignore

                    cmdBuffer.Reset()
                    cmdBuffer.Begin(renderPass, CommandBufferUsage.RenderPassContinue)
                    cmdBuffer.AppendCommand()
                    first.Run(cmdBuffer.Handle)
                    cmdBuffer.End()

                cmdBuffer
            
        [<AbstractClass>]
        type AbstractChangeableSetCommandBuffer(manager : ResourceManager, pool : CommandPool, renderPass : RenderPass, viewports : IMod<Box2i[]>) =
            inherit AbstractChangeableCommandBuffer(manager, pool, renderPass, viewports)

            abstract member Add : IRenderObject -> bool
            abstract member Remove : IRenderObject -> bool

        type ChangeableUnorderedCommandBuffer(manager : ResourceManager, pool : CommandPool, renderPass : RenderPass, viewports : IMod<Box2i[]>) =
            inherit AbstractChangeableSetCommandBuffer(manager, pool, renderPass, viewports)

            let first = new VKVM.CommandStream()
            let trie = Trie<VKVM.CommandStream>()
            do trie.Add([], first)

            let cache = Dict<IRenderObject, ICommandStreamResource>()
            override x.Prolog = first

            override x.Release() =
                cache.Clear()
                first.Dispose()
                trie.Clear()

            override x.Add(o : IRenderObject) =
                if not (cache.ContainsKey o) then
                    let resource = x.Compile o
                    let key = resource.GroupKey
                    trie.Add(key, resource.Stream)
                    cache.[o] <- resource
                    x.Changed()
                    true
                else
                    false

            override x.Remove(o : IRenderObject) =
                match cache.TryRemove o with
                    | (true, r) ->
                        let key = r.GroupKey
                        trie.Remove key |> ignore
                        x.Destroy r 
                        x.Changed()
                        true
                    | _ ->
                        false

        type ChangeableOrderedCommandBuffer(manager : ResourceManager, pool : CommandPool, renderPass : RenderPass, viewports : IMod<Box2i[]>, sorter : IMod<Trafo3d -> Box3d[] -> int[]>) =
            inherit AbstractChangeableSetCommandBuffer(manager, pool, renderPass, viewports)
        
            let first = new VKVM.CommandStream()

            let cache = Dict<IRenderObject, IMod<Box3d> * ICommandStreamResource>()

            let mutable camera = Mod.constant Trafo3d.Identity


            override x.Add(o : IRenderObject) =
                if not (cache.ContainsKey o) then
                    if cache.Count = 0 then
                        match Ag.tryGetAttributeValue o.AttributeScope "ViewTrafo" with
                            | Success trafo -> camera <- trafo
                            | _ -> failf "could not get camera view"

                    let res = x.Compile o
                    let bb = res.BoundingBox
                    cache.[o] <- (bb, res)
                    x.Changed()
                    true
                else
                    false

            override x.Remove(o : IRenderObject) =
                match cache.TryRemove o with
                    | (true, (_,res)) -> 
                        x.Destroy res
                        x.Changed()
                        true
                    | _ -> 
                        false

            override x.Prolog = first

            override x.Release() =
                first.Dispose()

            override x.Sort t =
                let sorter = sorter.GetValue t
                let all = cache.Values |> Seq.toArray

                let boxes = Array.zeroCreate all.Length
                let streams = Array.zeroCreate all.Length
                for i in 0 .. all.Length - 1 do
                    let (bb, s) = all.[i]
                    let bb = bb.GetValue t
                    boxes.[i] <- bb
                    streams.[i] <- s.Stream

                let viewTrafo = camera.GetValue t
                let perm = sorter viewTrafo boxes
                let mutable last = first
                for i in perm do
                    let s = streams.[i]
                    last.Next <- Some s
                last.Next <- None


                true

    open ChangeableCommandBuffers

    type RenderTask(device : Device, renderPass : RenderPass, shareTextures : bool, shareBuffers : bool) =
        inherit AbstractRenderTask()

        let pool = device.GraphicsFamily.CreateCommandPool()
        let passes = SortedDictionary<Aardvark.Base.Rendering.RenderPass, AbstractChangeableSetCommandBuffer>()
        let viewports = Mod.init [||]
        
        let cmd = pool.CreateCommandBuffer(CommandBufferLevel.Primary)

        let locks = ReferenceCountingSet<ILockedResource>()

        let user =
            { new IResourceUser with
                member x.AddLocked l = lock locks (fun () -> locks.Add l |> ignore)
                member x.RemoveLocked l = lock locks (fun () -> locks.Remove l |> ignore)
            }

        let manager = new ResourceManager(user, device)

        static let sortByCamera (order : RenderPassOrder) (trafo : Trafo3d) (boxes : Box3d[]) =
            let sign = 
                match order with
                    | RenderPassOrder.BackToFront -> 1
                    | RenderPassOrder.FrontToBack -> -1
                    | _ -> failf "invalid order %A" order

            let compare (l : Box3d) (r : Box3d) =
                let l = trafo.Forward.TransformPos l.Center
                let r = trafo.Forward.TransformPos r.Center
                sign * compare l.Z r.Z


            boxes.CreatePermutationQuickSort(Func<_,_,_>(compare))

        member x.HookRenderObject(o : IRenderObject) =
            match o with
                | :? RenderObject as o -> 
                    x.HookRenderObject o:> IRenderObject

                | :? MultiRenderObject as o ->
                    MultiRenderObject(o.Children |> List.map x.HookRenderObject) :> IRenderObject

                | _ ->
                    o

        member x.Add(o : IRenderObject) =
            let o = x.HookRenderObject o
            let key = o.RenderPass
            let cmd =
                match passes.TryGetValue key with
                    | (true,c) -> c
                    | _ ->
                        let c = 
                            match key.Order with
                                | RenderPassOrder.BackToFront | RenderPassOrder.FrontToBack -> 
                                    ChangeableOrderedCommandBuffer(manager, pool, renderPass, viewports, Mod.constant (sortByCamera key.Order)) :> AbstractChangeableSetCommandBuffer
                                | _ -> 
                                    ChangeableUnorderedCommandBuffer(manager, pool, renderPass, viewports) :> AbstractChangeableSetCommandBuffer
                        passes.[key] <- c
                        x.MarkOutdated()
                        c
            cmd.Add(o)

        member x.Remove(o : IRenderObject) =
            let key = o.RenderPass
            match passes.TryGetValue key with
                | (true,c) -> 
                    c.Remove o
                | _ ->
                    false

        member x.Clear() =
            for c in passes.Values do
                c.Dispose()
            passes.Clear()
            locks.Clear()
            cmd.Reset()
            x.MarkOutdated()

        override x.Dispose() =
            transact (fun () ->
                x.Clear()
                cmd.Dispose()
                pool.Dispose()
                manager.Dispose()
            )

        override x.FramebufferSignature = Some (renderPass :> _)

        override x.Runtime = None

        override x.PerformUpdate(token : AdaptiveToken, rt : RenderToken) =
            ()

        override x.Use(f : unit -> 'r) =
            f()

        override x.Perform(token : AdaptiveToken, rt : RenderToken, desc : OutputDescription) =
            x.OutOfDate <- true
            let vp = Array.create renderPass.AttachmentCount desc.viewport
            transact (fun () -> viewports.Value <- vp)

            let fbo =
                match desc.framebuffer with
                    | :? Framebuffer as fbo -> fbo
                    | fbo -> failwithf "unsupported framebuffer: %A" fbo

            use tt = device.Token
            let passCmds = passes.Values |> Seq.map (fun p -> p.GetValue(token)) |> Seq.toList
            tt.Sync()

            cmd.Reset()
            cmd.Begin(renderPass, CommandBufferUsage.OneTimeSubmit)
            cmd.enqueue {
                let oldLayouts = Array.zeroCreate fbo.ImageViews.Length
                for i in 0 .. fbo.ImageViews.Length - 1 do
                    let img = fbo.ImageViews.[i].Image
                    oldLayouts.[i] <- img.Layout
                    if VkFormat.hasDepth img.Format then
                        do! Command.TransformLayout(img, VkImageLayout.DepthStencilAttachmentOptimal)
                    else
                        do! Command.TransformLayout(img, VkImageLayout.ColorAttachmentOptimal)

                do! Command.BeginPass(renderPass, fbo, false)
                do! Command.ExecuteSequential passCmds
                do! Command.EndPass

                for i in 0 .. fbo.ImageViews.Length - 1 do
                    let img = fbo.ImageViews.[i].Image
                    do! Command.TransformLayout(img, oldLayouts.[i])
            }   
            cmd.End()

            device.GraphicsFamily.RunSynchronously cmd
            
    type DependentRenderTask(device : Device, renderPass : RenderPass, objects : aset<IRenderObject>, shareTextures : bool, shareBuffers : bool) =
        inherit RenderTask(device, renderPass, shareTextures, shareBuffers)

        let reader = objects.GetReader()

        override x.Perform(token : AdaptiveToken, rt : RenderToken, desc : OutputDescription) =
            x.OutOfDate <- true
            let deltas = reader.GetOperations token
            if not (HDeltaSet.isEmpty deltas) then
                transact (fun () -> 
                    for d in deltas do
                        match d with
                            | Add(_,o) -> x.Add o |> ignore
                            | Rem(_,o) -> x.Remove o |> ignore
                )

            base.Perform(token, rt, desc)

        override x.Dispose() =
            reader.Dispose()
            base.Dispose()




