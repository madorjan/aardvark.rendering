﻿#if INTERACTIVE
#I @"../../../bin/Debug"
#I @"../../../bin/Release"
#load "LoadReferences.fsx"
#else
namespace Examples
#endif


open System
open Aardvark.Base
open Aardvark.Rendering.Interactive

open Default 

open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Application

module Polygons = 

    FsiSetup.initFsi (Path.combine [__SOURCE_DIRECTORY__; ".."; ".."; ".."; "bin";"Debug";"Examples.exe"])

    module Scope =
        open System.Threading

        type Scope = string
        type Context = list<Scope>
        let current = new ThreadLocal<Context>(fun _ -> [])

        let scoped n f =
            let old = current.Value
            current.Value <- n :: current.Value
            let r = f ()
            current.Value <- old
            r

        
        type Unique(seed : int, prefix : string) =
            let mutable current = seed
            member x.Fresh() =
                let freshId = sprintf "%s%d" prefix current
                current <- current + 1
                freshId

        let uniqueScope (n : Unique) f =
            let fresh = n.Fresh()
            scoped fresh (fun () -> f fresh)

        let empty () = 
            let prefix = current.Value |> String.concat ""
            Unique(0, prefix)
        let fesh (s : Unique) = s.Fresh() 
        
        let pmod (git : WorkingCopy) (name : string) v =
            git.pmod (sprintf "%s.%s" (current.Value |> String.concat "") name) v
            
        let pset (git : WorkingCopy) (name : string) =
            git.pset (sprintf "%s.%s" (current.Value |> String.concat "") name)

    [<AutoOpen>]
    module Interaction =
        type Operation = AddPoint of V3f | ClosePolygon | MovePoint of pmod<V3f> * V3f

    module Logics =

        open Interaction

        let (<~) (p : pmod<'a>) v = PMod.change p v
        
        type Polygon  = pmod<list<pmod<V3f>>>
        type Polygons = pset<Polygon>

        type Scene = { polygons : Polygons }
        type State = pmod<list<V3f>> * Scope.Unique

        type Logics = Scene * State

        let createScene (wc : WorkingCopy) = { polygons = Scope.pset wc "polygons" }
            
        let interact (wc : WorkingCopy) ((scene,(state,name)) : Logics) (commit : bool)  (op : Operation) =
            let changes =
                [
                    match op with
                        | AddPoint p   -> 
                            yield state <~ p :: state.Value
                        | ClosePolygon ->
                            let closedPolygon = state.Value |> List.toArray
                            if closedPolygon.Length > 0 then
                                let newPolygon = 
                                    Scope.uniqueScope name (fun scope ->
                                        state.Value 
                                            |> List.mapi (fun i v -> Scope.pmod wc (sprintf "%d" i) v) 
                                            |> List.rev
                                            |> Scope.pmod wc scope
                                )
                                yield PSet.add scene.polygons newPolygon
                            yield state <~ []
                        | MovePoint (p,newPos) ->
                            yield p <~ newPos
                ]
            for c in changes do wc.apply c
            let action = sprintf "%A" op

            if commit then
                wc.commit action
                Git2Dgml.visualizeHistory (Path.combine [ __SOURCE_DIRECTORY__; "polygons.dgml" ]) (wc.Branches |> Dictionary.toList) |> ignore


    [<AutoOpen>]
    module View =

        let git = Git.init ()

        let viewTrafo = viewTrafo ()
        let frustum = perspective ()
        let camera = Mod.map2 Camera.create viewTrafo frustum
    
        let camPick = 
            adaptive {
                let! camera        = camera
                let! pixelPosition = win.Mouse.Position
                let p = Camera.tryGetPickPointOnPlane camera (Plane3d(V3d.OOI,V3d.OOO)) pixelPosition
                return p
            }

        let hoverPosition = Mod.init None

        [<AutoOpen>]
        module Controller =
            let appState,workingState = 
                Scope.scoped "mainScene" (fun () -> 
                    Logics.createScene git, (Scope.pmod git "workingPolygon" [], Scope.empty ())
                )
            let moving = ref None
            let interact = Logics.interact git (appState,workingState) true
            let interactFast =  Logics.interact git (appState,workingState) false
             
            let polygons = (appState.polygons.CSet :> aset<_>).GetReader()
            win.Mouse.Move.Values.Subscribe(fun (last,current) ->
                polygons.GetDelta() |> ignore
                let pick = Camera.pickRay (Mod.force camera) current
                let nearbyRay = 
                    polygons.Content
                        |> Seq.collect (fun (p : Logics.Polygon) -> 
                            [ for px in p.Value do 
                                let mutable hit = RayHit3d.MaxRange
                                let hitD = 
                                    if pick.HitsSphere(V3d.op_Explicit px.Value, 0.1, 0.1, 10.0, &hit) 
                                    then hit.T
                                    else Double.MaxValue
                                yield p, px, hitD
                            ] )
                        |> Seq.sortBy (fun (_,_,d) -> d)
                        |> Seq.toList
                match nearbyRay with
                 | [] -> ()
                 | (p,bestPosition,d)::_ -> 
                    transact (fun () -> 
                        if d < Double.MaxValue then
                            Mod.change hoverPosition (Some <| (p, bestPosition, V3d.op_Explicit bestPosition.Value))
                        else Mod.change hoverPosition None
                    )
                match !moving with
                 | Some point -> 
                    MovePoint (point, camPick |> Mod.force |> Option.get |> V3f.op_Explicit) |> interactFast  
                 | _ -> ()
            ) |> ignore

            win.Mouse.Click.Values.Subscribe(fun c ->
                match c with 
                 | MouseButtons.Left  -> interact (camPick |> Mod.force |> Option.map V3f.op_Explicit |> Option.get |> AddPoint) 
                 | MouseButtons.Right -> interact ClosePolygon 
                 | MouseButtons.Middle -> 
                    match !moving, Mod.force hoverPosition with
                     | None, Some (poly,point,p) -> moving := Some point
                     | Some point, _ -> 
                        interact <| MovePoint (point, camPick |> Mod.force |> Option.get |> V3f.op_Explicit)   
                        moving := None
                     | None, None -> ()
                 | _ -> ()

            ) |> ignore


        [<AutoOpen>]
        module Visualization =
            let endPoint = camPick |> Mod.map (Option.map V3f.op_Explicit)

            let lineGeometry (color : C4b) (points : list<V3f>) (endPoint : Option<V3f>) =
                Helpers.lineLoopGeometry color (points |> List.append (endPoint |> Option.toList) |> List.toArray)

            let polygonVis =
                aset {
                    for p in appState.polygons do
                        let lines = 
                            Mod.custom (fun self -> 
                                let positions = (p :> IMod<_>).GetValue self
                                let lines = List.foldBack (fun (x:pmod<_>) s -> (x :> IMod<_>).GetValue self :: s) positions [] |> List.toArray
                                Helpers.lineLoopGeometry C4b.White lines
                         )
                        yield Sg.dynamic lines
                }

            let scene =
                Mod.map2 (lineGeometry C4b.Red) (fst workingState) endPoint 
                    |> Sg.dynamic
                    |> Sg.andAlso (polygonVis |> Sg.set)


        let conditionally (size:float) color (m : IMod<_>) =
            Sphere.solidSphere color 5  |> Sg.trafo (Mod.constant <| Trafo3d.Scale size)
                |> Sg.trafo (m |> Mod.map (Option.defaultValue Trafo3d.Identity << Option.map Trafo3d.Translation))
                |> Sg.onOff (m |> Mod.map Option.isSome)    

        let pickSphere  = conditionally 0.040 C4b.Green camPick
        let hoverSphere = conditionally 0.041 C4b.Yellow (Mod.map (Option.map (fun (_,_,p) -> p)) hoverPosition)
        let groundPlane = Helpers.quad C4b.Gray

        let sg =
            groundPlane 
                |> Sg.andAlso pickSphere
                |> Sg.andAlso hoverSphere
                |> Sg.effect [
                    DefaultSurfaces.trafo |> toEffect                   
                    DefaultSurfaces.simpleLighting |> toEffect
                   ]
                |> Sg.andAlso ( 
                    scene  
                        |> Sg.uniform "LineWidth" (Mod.constant 5.0)
                        |> Sg.uniform "ViewportSize" (Mod.constant (V2i(800,600)))
                        |> Sg.effect [ 
                            DefaultSurfaces.trafo |> toEffect; 
                            DefaultSurfaces.constantColor C4f.Red |> toEffect
                            DefaultSurfaces.thickLine |> toEffect 
                           ] 
                   )
                |> Sg.camera camera

    let run () =
        FsiSetup.initFsi (Path.combine [__SOURCE_DIRECTORY__; ".."; ".."; ".."; "bin";"Debug";"Examples.exe"])

        setSg sg
        win.Run()

open Polygons

module TestOperations =
    git.merge "urdar"

#if INTERACTIVE
setSg sg
printfn "Done. Modify sg and call setSg again in order to see the modified rendering result."
#else
#endif
