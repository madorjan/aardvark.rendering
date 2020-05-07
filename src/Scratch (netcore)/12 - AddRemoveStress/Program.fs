﻿open System
open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.SceneGraph
open Aardvark.Application
open Aardvark.Application.Slim

open FSharp.Data.Adaptive
open System.Threading

// This example illustrates how to create a simple render window. 
// In contrast to the rest of the examples (beginning from 01-Triangle), we use
// the low level application API for creating a window and attaching 
// mouse and keyboard inputs.
// In your application you most likely want to use this API instead of the more abstract
// show/window computation expression builders (which reduces code duplication
// in this case) to setup applications.

[<EntryPoint>]
let main argv = 

    Aardvark.Init()

    // create an OpenGL/Vulkan application. Use the use keyword (using in C#) in order to
    // properly dipose resources on shutdown...
    use app = new VulkanApplication(true)
    //use app = new OpenGlApplication()
    // SimpleRenderWindow is a System.Windows.Forms.Form which contains a render control
    // of course you can a custum form and add a control to it.
    // Note that there is also a WPF binding for OpenGL. For more complex GUIs however,
    // we recommend using aardvark-media anyways..
    let win = app.CreateGameWindow(samples = 8)
    //win.Title <- "Hello Aardvark"

    // Given eye, target and sky vector we compute our initial camera pose
    let initialView = CameraView.LookAt(V3d(2.0,2.0,2.0), V3d.Zero, V3d.OOI)
    // the class Frustum describes camera frusta, which can be used to compute a projection matrix.
    let frustum = 
        // the frustum needs to depend on the window size (in oder to get proper aspect ratio)
        win.Sizes 
            // construct a standard perspective frustum (60 degrees horizontal field of view,
            // near plane 0.1, far plane 50.0 and aspect ratio x/y.
            |> AVal.map (fun s -> Frustum.perspective 60.0 0.1 50.0 (float s.X / float s.Y))

    // create a controlled camera using the window mouse and keyboard input devices
    // the window also provides a so called time mod, which serves as tick signal to create
    // animations - seealso: https://github.com/aardvark-platform/aardvark.docs/wiki/animation
    let cameraView = DefaultCameraController.control win.Mouse win.Keyboard win.Time initialView

    // create a quad using low level primitives (IndexedGeometry is our base type for specifying
    // geometries using vertices etc)
    let quadSg =
        let quad =
            IndexedGeometry(
                Mode = IndexedGeometryMode.TriangleList,
                IndexArray = ([|0;1;2; 0;2;3|] :> System.Array),
                IndexedAttributes =
                    SymDict.ofList [
                        DefaultSemantic.Positions,                  [| V3f(-1,-1,0); V3f(1,-1,0); V3f(1,1,0); V3f(-1,1,0) |] :> Array
                        DefaultSemantic.Normals,                    [| V3f.OOI; V3f.OOI; V3f.OOI; V3f.OOI |] :> Array
                        DefaultSemantic.DiffuseColorCoordinates,    [| V2f.OO; V2f.IO; V2f.II; V2f.OI |] :> Array
                    ]
            )
                
        // create a scenegraph, given a IndexedGeometry instance...
        quad |> Sg.ofIndexedGeometry


    let should = AVal.init false
    let should2 = AVal.init false

    let gouh = 
        should |> AVal.map (fun should -> 
            if should then 
               quadSg
                    // here we use fshade to construct a shader: https://github.com/aardvark-platform/aardvark.docs/wiki/FShadeOverview
                    |> Sg.effect [
                            DefaultSurfaces.trafo                 |> toEffect
                            DefaultSurfaces.constantColor C4f.Red |> toEffect
                            DefaultSurfaces.diffuseTexture        |> toEffect
                        ]
                    |> Sg.diffuseTexture DefaultTextures.checkerboard
            else Sg.empty
        ) |> Sg.dynamic


    //let t = gouh |> Sg.compile win.Runtime win.FramebufferSignature |> RenderTask.renderToColor (Mod.constant (V2i.II*1024)) 


    let uh = 
        Sg.box' C4b.White Box3d.Unit 
        |> Sg.scale 0.1
        // here we use fshade to construct a shader: https://github.com/aardvark-platform/aardvark.docs/wiki/FShadeOverview
        |> Sg.effect [
                DefaultSurfaces.trafo                 |> toEffect
                DefaultSurfaces.constantColor C4f.Red |> toEffect
                DefaultSurfaces.diffuseTexture        |> toEffect
            ]
        |> Sg.diffuseTexture DefaultTextures.checkerboard

    let sg =
        Sg.box' C4b.White Box3d.Unit 
            // here we use fshade to construct a shader: https://github.com/aardvark-platform/aardvark.docs/wiki/FShadeOverview
            |> Sg.effect [
                    DefaultSurfaces.trafo                 |> toEffect
                    DefaultSurfaces.constantColor C4f.Red |> toEffect
                    DefaultSurfaces.simpleLighting        |> toEffect
                ]
            |> Sg.andAlso gouh
            |> Sg.andAlso uh
            // extract our viewTrafo from the dynamic cameraView and attach it to the scene graphs viewTrafo 
            |> Sg.viewTrafo (cameraView  |> AVal.map CameraView.viewTrafo )
            // compute a projection trafo, given the frustum contained in frustum
            |> Sg.projTrafo (frustum |> AVal.map Frustum.projTrafo    )

    let sg = should2 |> AVal.map (fun should -> if should then sg else Sg.empty) |> Sg.dynamic


    let r = new System.Random()

    let changer () = 
        System.Threading.Thread.Sleep 2000
        while true do
            let g = r.NextDouble()
            if g < 0.3 then 
                transact (fun _ -> 
                    should.Value <- not should.Value
                )
            elif g < 0.6 then   
                transact (fun _ -> 
                    should2.Value <- not should2.Value
                )
            elif g < 0.8 then  
                transact (fun _ -> 
                    should.Value <- not should.Value
                    should2.Value <- not should.Value
                )
            else    
                transact (fun _ -> 
                    should.Value <- not should2.Value
                    should2.Value <- not should.Value
                )


    let t = Thread(ThreadStart changer)
    t.IsBackground <- true
    t.Start()


    win.Keyboard.DownWithRepeats.Values.Add(fun k-> 
        if k = Keys.T then
            transact (fun _ -> should.Value <- not should.Value)
        elif k = Keys.Z then transact (fun _ -> should2.Value <- not should2.Value)
    )


    let renderTask = 
        // compile the scene graph into a render task
        app.Runtime.CompileRender(win.FramebufferSignature, sg)

    // assign the render task to our window...
    win.RenderTask <- renderTask
    win.Run()
    0
