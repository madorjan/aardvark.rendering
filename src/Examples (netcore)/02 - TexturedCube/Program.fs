﻿open Aardvark.Base
open Aardvark.Base.Rendering
open FSharp.Data.Adaptive
open Aardvark.SceneGraph
open Aardvark.Application
open System.Runtime.InteropServices

#nowarn "9"

module ImageSharp =
    open SixLabors.ImageSharp
    open SixLabors.ImageSharp.Formats
    open SixLabors.ImageSharp.PixelFormats
    open SixLabors.ImageSharp.Advanced
    open Microsoft.FSharp.NativeInterop

    type ImageVisitor<'r> =
        abstract member Accept : Image<'fmt> -> 'r

    let load (path : string) =
        use stream = System.IO.File.OpenRead path
        let mutable fmt : IImageFormat = null
        let info = Image.Identify(stream)

        stream.Seek(0L, System.IO.SeekOrigin.Begin) |> ignore
        use img = Image.Load stream
        match img with
        | :? Image<Rgba32> as img ->
            let res = PixImage<byte>(Col.Format.RGBA, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Rgba32> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                img.GetPixelSpan().CopyTo(System.Span<Rgba32>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length / 4))
                res :> PixImage
            finally
                gc.Free()
        | :? Image<Rgb24> as img ->
            let res = PixImage<byte>(Col.Format.RGB, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Rgba32> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                img.GetPixelSpan().CopyTo(System.Span<Rgb24>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length / 3))
                res :> PixImage
            finally
                gc.Free()
        | :? Image<Alpha8> as img ->
            let res = PixImage<byte>(Col.Format.Gray, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Rgba32> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                img.GetPixelSpan().CopyTo(System.Span<Alpha8>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length))
                res :> PixImage
            finally
                gc.Free()
        | :? Image<Argb32> as img ->
            let res = PixImage<byte>(Col.Format.Gray, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Rgba32> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                let dst = System.Span<Rgba32>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length / 4)
                let src = img.GetPixelSpan()
                for i in 0 .. dst.Length - 1 do
                    dst.[i] <- Rgba32(src.[i].R, src.[i].G, src.[i].B, src.[i].A)
                res :> PixImage
            finally
                gc.Free()
        | :? Image<Rgba64> as img ->
            let res = PixImage<uint16>(Col.Format.RGBA, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Rgba64> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                img.GetPixelSpan().CopyTo(System.Span<Rgba64>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length / 4))
                res :> PixImage
            finally
                gc.Free()
        | :? Image<Gray16> as img ->
            let res = PixImage<uint16>(Col.Format.Gray, V2i(img.Width, img.Height))
            let gc = GCHandle.Alloc(res.Volume.Data, GCHandleType.Pinned)
            try
                let ptr : nativeptr<Gray16> = NativePtr.ofNativeInt (gc.AddrOfPinnedObject())
                img.GetPixelSpan().CopyTo(System.Span<Gray16>(NativePtr.toVoidPtr ptr, res.Volume.Data.Length))
                res :> PixImage
            finally
                gc.Free()
        | _ ->
            failwith "not implemented"




[<EntryPoint>]
let main argv = 
    // first we need to initialize Aardvark's core components
    
    Aardvark.Init()

    // lets define the bounds/color for our box
    // NOTE that the color is going to be ignored since we're using a texture
    let box = Box3d(-V3d.III, V3d.III)
    let color = C4b.Red

    let sg = 
        // thankfully aardvark defines a primitive box
        Sg.box (AVal.constant color) (AVal.constant box)

            // apply the texture as "DiffuseTexture"
            |> Sg.diffuseTexture DefaultTextures.checkerboard

            // apply a shader ...
            // * transforming all vertices
            // * looking up the DiffuseTexture 
            // * applying a simple lighting to the geometry (headlight)
            |> Sg.shader {
                do! DefaultSurfaces.trafo
                do! DefaultSurfaces.diffuseTexture
                do! DefaultSurfaces.simpleLighting
            }
    

    // show the scene in a simple window
    show {
        backend Backend.GL
        display Display.Mono
        debug false
        samples 8
        scene sg
    }

    0
