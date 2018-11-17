﻿open Aardvark.Base
open Aardvark.Base.Rendering
open Aardvark.Base.Incremental
open Aardvark.SceneGraph
open Aardvark.Application
open Microsoft.FSharp.Quotations
open ConjugateGradient
open Aardvark.Rendering.Vulkan
open Aardvark.Application.Slim
open System
open System.Collections.Concurrent


module ConjugateGradientShaders =
    open Microsoft.FSharp.Quotations
    open FShade

    [<LocalSize(X = 8, Y = 8)>]
    let polynomial2d<'f, 'v when 'f :> Formats.IFloatingFormat> (call : Expr<V2i -> 'v>) (toV4 : Expr<'v -> V4d>) (res : Image2d<'f>) =
        compute {
            let id = getGlobalId().XY
            if id.X < res.Size.X && id.Y < res.Size.Y then
                let v = (%call) id
                res.[id] <- (%toV4) v
        }
        

    let srcSampler =
        sampler2d {
            texture uniform?src
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
            filter Filter.MinMagLinear
        }
    let weightSampler =
        sampler2d {
            texture uniform?weight
            addressU WrapMode.Border
            addressV WrapMode.Border
            borderColor (C4f(0.0f, 0.0f, 0.0f, 0.0f))
            filter Filter.MinMagLinear
        }

    let weightTimesSrcSampler =
        sampler2d {
            texture uniform?weightTimesSrc
            addressU WrapMode.Border
            addressV WrapMode.Border
            borderColor (C4f(0.0f, 0.0f, 0.0f, 0.0f))
            filter Filter.MinMagLinear
        }

    [<LocalSize(X = 8, Y = 8)>]
    let restrict<'fmt when 'fmt :> Formats.IFloatingFormat> (dst : Image2d<'fmt>) (factor : float) =
        compute {
            let id = getGlobalId().XY
            let dstSize = dst.Size
            let srcSize = srcSampler.Size

            if id.X < dstSize.X && id.Y < dstSize.Y then
                let tc = (V2d(id) + V2d.Half) / V2d(dstSize)
                let d = 0.25 / V2d dstSize

                let vp0 = srcSampler.SampleLevel(tc + V2d( d.X,  0.0), 0.0)
                let vn0 = srcSampler.SampleLevel(tc + V2d(-d.X,  0.0), 0.0)
                let v0p = srcSampler.SampleLevel(tc + V2d( 0.0,  d.Y), 0.0)
                let v0n = srcSampler.SampleLevel(tc + V2d( 0.0, -d.Y), 0.0)
                let value = (vp0 + vn0 + v0n + v0p) / 4.0
                
                //let value = srcSampler.SampleLevel(tc, 0.0)

                dst.[id] <- factor * value
        }


    [<LocalSize(X = 8, Y = 8)>]
    let mul2d<'fmt when 'fmt :> Formats.IFloatingFormat> (l : Image2d<'fmt>) (r : Image2d<'fmt>) (dst : Image2d<'fmt>) (dstWeight : Image2d<'fmt>) =
        compute {
            let id = getGlobalId().XY
            let dstSize = dst.Size
            if id.X < dstSize.X && id.Y < dstSize.Y then
                dst.[id] <- l.[id] * r.[id]
        }

    [<ReflectedDefinition>]
    let inline interpolate4 (v : V2d) (p00 : V4d) (p01 : V4d) (p10 : V4d) (p11 : V4d) =
        let px0 = p00 + v.X * (p10 - p00)
        let px1 = p01 + v.X * (p11 - p01)
        px0 + v.Y * (px1 - px0)

    [<ReflectedDefinition>]
    let inline interpolate1 (v : V2d) (p00 : float) (p01 : float) (p10 : float) (p11 : float) =
        let px0 = p00 + v.X * (p10 - p00)
        let px1 = p01 + v.X * (p11 - p01)
        px0 + v.Y * (px1 - px0)


    [<LocalSize(X = 8, Y = 8)>]
    let restrictWeight<'fmt when 'fmt :> Formats.IFloatingFormat> (dst : Image2d<'fmt>) (dstWeight : Image2d<'fmt>) =
        compute {
            let id = getGlobalId().XY
            let dstSize = dst.Size
            let srcSize = srcSampler.Size

            if id.X < dstSize.X && id.Y < dstSize.Y then
                let tc = (V2d(id) + V2d.Half) / V2d(dstSize)
                let d = 0.25 / V2d dstSize

                let wp0 = weightSampler.SampleLevel(tc + V2d( d.X,  0.0), 0.0).X
                let wn0 = weightSampler.SampleLevel(tc + V2d(-d.X,  0.0), 0.0).X
                let w0p = weightSampler.SampleLevel(tc + V2d( 0.0,  d.Y), 0.0).X
                let w0n = weightSampler.SampleLevel(tc + V2d( 0.0, -d.Y), 0.0).X
                let weightAvg = (wp0 + wn0 + w0n + w0p) / 4.0
                
                let vp0 = weightTimesSrcSampler.SampleLevel(tc + V2d( d.X,  0.0), 0.0)
                let vn0 = weightTimesSrcSampler.SampleLevel(tc + V2d(-d.X,  0.0), 0.0)
                let v0p = weightTimesSrcSampler.SampleLevel(tc + V2d( 0.0,  d.Y), 0.0)
                let v0n = weightTimesSrcSampler.SampleLevel(tc + V2d( 0.0, -d.Y), 0.0)
                let v = (vp0 + vn0 + v0n + v0p) / 4.0




                //let weightAvg = weightSampler.SampleLevel(tc, 0.0).X
                //let v = weightTimesSrcSampler.SampleLevel(tc, 0.0)

                let value = 
                    if weightAvg < 0.00001 then 
                        V4d.Zero
                    else 
                        //let v = interpolate4 frac (v00 * w00) (v01 * w01) (v10 * w10) (v11 * w11)
                        v / weightAvg

                dst.[id] <- value
                dstWeight.[id] <- V4d.IIII * weightAvg
        }

    [<LocalSize(X = 8, Y = 8)>]
    let interpolate<'fmt when 'fmt :> Formats.IFloatingFormat> (factor : V4d) (dst : Image2d<'fmt>) =
        compute {
            let id = getGlobalId().XY
            let dstSize = dst.Size
            let srcSize = srcSampler.Size

            if id.X < dstSize.X && id.Y < dstSize.Y then
                let tc = (V2d(id) + V2d.Half) / V2d(dstSize)
                let v = factor * srcSampler.SampleLevel(tc, 0.0)
                dst.[id] <- v 
        }
    [<LocalSize(X = 8, Y = 8)>]

    let divergence<'fmt when 'fmt :> Formats.IFloatingFormat> (dst : Image2d<'fmt>) =
        compute {
            let id = getGlobalId().XY
            let dstSize = dst.Size
            let srcSize = srcSampler.Size

            if id.X < dstSize.X && id.Y < dstSize.Y then
                let tc = (V2d(id) + V2d.Half) / V2d(dstSize)

                let d = 1.0 / V2d srcSize

                let v00  = srcSampler.SampleLevel(tc,0.0)
                let vp0  = srcSampler.SampleLevel(tc + V2d( d.X, 0.0 ),0.0)
                let vn0  = srcSampler.SampleLevel(tc + V2d(-d.X, 0.0 ),0.0)
                let v0p  = srcSampler.SampleLevel(tc + V2d( 0.0, d.Y ),0.0)
                let v0n  = srcSampler.SampleLevel(tc + V2d( 0.0,-d.Y ),0.0)

                let div = 4.0 * v00 - vp0 - vn0 - v0p - v0n
                
                dst.[id] <- div 
        }

type ConjugateGradientConfig =
    {
        gradientTolerance       : float
        stepTolerance           : float
        maxIterations           : int
        maxLineSearchIterations : int
    }

type ConjugateGradientSolver2d<'f, 'v when 'v : unmanaged and 'f :> FShade.Formats.IFloatingFormat> (runtime : IRuntime, residual' : Term.TermParameter2d -> Term<V2i> -> Term<V2i>) =
    static let ceilDiv (a : int) (b : int) =
        if a % b = 0 then a / b
        else 1 + a / b
    
    static let ceilDiv2 (a : V2i) (b : V2i) =
        V2i(
            ceilDiv a.X b.X,
            ceilDiv a.Y b.Y
        )

    static let getFormat =
        LookupTable.lookupTable [
            typeof<FShade.Formats.r32f>, TextureFormat.R32f
            typeof<FShade.Formats.rg32f>, TextureFormat.Rg32f
            typeof<FShade.Formats.rgba32f>, TextureFormat.Rgba32f
        ]

    static let format = getFormat typeof<'f>

    static let doWhile (cond : unit -> bool) (action : unit -> unit) =
        action()
        while cond() do action()

    let real = RealInstances.instance<'v>
    let rreal = ReflectedReal.instance<'v>
    
    let tools = TensorTools<'v>.Get(runtime)
    
    let poly'  = residual' (Term.TermParameter2d "x") (Term.Uniform "h")

    //let epoly = Term.toReflectedCall Term.Read.image residual
    //let upoly = Term.parameters residual |> HMap.toList |> List.map fst
    
    let epoly' = Term.toReflectedCall Term.Read.image poly'
    let upoly' = Term.parameters poly' |> HMap.toList |> List.map fst

    let poly'' =
        let d = Term.TermParameter2d("d")
        
        let mutable sum = Term<V2i>.Zero
        let all = Term.allDerivatives "x" poly'
        for (c, p) in HMap.toSeq all do
            sum <- sum + p * d.[c.X, c.Y]
        sum

    let epoly'' = Term.toReflectedCall Term.Read.image poly''
    let upoly'' = Term.parameters poly'' |> HMap.toList |> List.map fst


    let negativeV4 = 
        let toV4 = rreal.toV4
        let neg = rreal.neg
        <@ fun v -> (%toV4) ((%neg) v) @>
        
    let negativeDerivative = runtime.CreateComputeShader (ConjugateGradientShaders.polynomial2d<'f, 'v> epoly' negativeV4)
    let derivative = runtime.CreateComputeShader (ConjugateGradientShaders.polynomial2d<'f, 'v> epoly' rreal.toV4)
    let secondMulD = runtime.CreateComputeShader (ConjugateGradientShaders.polynomial2d<'f, 'v> epoly'' rreal.toV4)
    
    let createTexture (img : PixImage) =
        let t = runtime.CreateTexture(img.Size, TextureFormat.ofPixFormat img.PixFormat TextureParams.empty, 1, 1)
        runtime.Upload(t, 0, 0, img)
        t

    member x.Tools = tools

    member x.NegativeDerivative(h : float, inputs : Map<string, ITextureSubResource>, dst : ITextureSubResource) =
        use input = runtime.NewInputBinding negativeDerivative
        input.["res"] <- dst
        input.["h"] <- real.fromFloat h

        for used in upoly' do
            let r = inputs.[used]
            input.[used] <- r
            input.[sprintf "%sLevel" used] <- 0
            input.[sprintf "%sSize" used] <- r.Size.XY
            
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind negativeDerivative
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch(ceilDiv2 dst.Size.XY negativeDerivative.LocalSize.XY)
        ]

    member x.Derivative(h : float, inputs : Map<string, ITextureSubResource>, dst : ITextureSubResource) =
        use input = runtime.NewInputBinding derivative
        input.["res"] <- dst
        input.["h"] <- real.fromFloat h

        for used in upoly' do
            let r = inputs.[used]
            input.[used] <- r
            input.[sprintf "%sLevel" used] <- 0
            input.[sprintf "%sSize" used] <- r.Size.XY
            
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind derivative
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch(ceilDiv2 dst.Size.XY negativeDerivative.LocalSize.XY)
        ]

    member x.SecondMulD(h : float, inputs : Map<string, ITextureSubResource>, d : ITextureSubResource, dst : ITextureSubResource) =
        use input = runtime.NewInputBinding secondMulD
        input.["res"] <- dst
        input.["h"] <- real.fromFloat h

        input.["d"] <- d
        input.["dLevel"] <- 0
        input.["dSize"] <- d.Size.XY

        for used in upoly' do
            let r = inputs.[used]
            input.[used] <- r
            input.[sprintf "%sLevel" used] <- 0
            input.[sprintf "%sSize" used] <- r.Size.XY
            
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind secondMulD
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch(ceilDiv2 dst.Size.XY negativeDerivative.LocalSize.XY)
        ]


    member internal this.SolveInternal(h : float, inputs : Map<string, ITextureSubResource>, x : ITextureSubResource, cfg : ConjugateGradientConfig) =
        let size = x.Size.XY
        let n = size.X * size.Y
        
        use __ = runtime.NewInputBinding secondMulD

        let mutable i = 0
        let mutable j = 0
        let mutable k = 0

        
        let inputs = Map.add "x" x inputs 
        let r = inputs.["__r"]
        let d = inputs.["__d"]
        let temp = inputs.["__temp"]
        //let r = runtime.CreateTexture(size, format, 1, 1)
        //let d = runtime.CreateTexture(size, format, 1, 1)
        //let temp = runtime.CreateTexture(size, format, 1, 1)


        // r <- -f'(x)
        this.NegativeDerivative(h, inputs, r)

        // d <- r
        runtime.Copy(r, d)
        
        
        let mutable deltaOld = real.zero
        let mutable deltaNew = tools.LengthSquared r
        let delta0 = deltaNew

        let mutable deltaD = deltaNew
        let mutable alpha = real.zero

        let eps = cfg.stepTolerance
        let eps2Delta0 = real.mul (real.fromFloat (eps * eps)) delta0
        let eps2 = real.fromFloat (eps * eps)

        let absEps = cfg.gradientTolerance * cfg.gradientTolerance |> real.fromFloat

        while i < cfg.maxIterations && real.isGreater deltaNew eps2Delta0 && real.isGreater deltaNew absEps do
            j <- 0

            // deltaD <- <d|d>
            deltaD <- tools.LengthSquared d

            //this.Residual(inputs, temp.[TextureAspect.Color, 0, 0])
            //let sumSq = tools.Sum(temp.[TextureAspect.Color, 0, 0])
            //printfn "res^2: %A (%A)" sumSq deltaD


            let alphaTest() =
                let alpha2DeltaD = real.mul (real.pow alpha 2) deltaD
                real.isGreater alpha2DeltaD eps2

            doWhile (fun () -> j < cfg.maxLineSearchIterations && alphaTest())  (fun () -> 
                // a <- <f'(x) | d >
                this.Derivative(h, inputs, temp)
                let a = tools.Dot(temp, d)
                
                // b <- <d | f''(x) * d >
                this.SecondMulD(h, inputs, d, temp)
                let b = tools.Dot(temp, d)

                // alpha <- -a / b
                alpha <- real.neg (real.div a b) //(dot d (f'' x d))

                // x <- x + alpha*d
                tools.MultiplyAdd(d, alpha, x, real.one)
                
                j <- j + 1
            )
            
            // r <- -f'(x)
            this.NegativeDerivative(h, inputs, r)
            deltaOld <- deltaNew
            deltaNew <- tools.LengthSquared r
            let beta = real.div deltaNew deltaOld

            // d <- r + beta * d
            tools.MultiplyAdd(r, real.one, d, beta)
            
            k <- k + 1
            if k = n then //|| real.isNegative (tools.Dot(r, d)) then
                runtime.Copy(r, d)
                k <- 0

            i <- i + 1


        printfn "iter %dx%d: %d" size.X size.Y i




    member this.Solve(inputs : Map<string, ITextureSubResource>, x : ITextureSubResource, cfg : ConjugateGradientConfig) =
        let r = runtime.CreateTexture(x.Size.XY, format, 1, 1)
        let d = runtime.CreateTexture(x.Size.XY, format, 1, 1)
        let temp = runtime.CreateTexture(x.Size.XY, format, 1, 1)

        try
            let inputs = 
                inputs
                |> Map.add "__r" r.[TextureAspect.Color, 0, 0]
                |> Map.add "__d" d.[TextureAspect.Color, 0, 0]
                |> Map.add "__temp" temp.[TextureAspect.Color, 0, 0]
            this.SolveInternal(1.0, inputs, x, cfg)
        finally 
            runtime.DeleteTexture r
            runtime.DeleteTexture d
            runtime.DeleteTexture temp



    member this.Solve(inputs : Map<string, PixImage>, x : PixImage, cfg : ConjugateGradientConfig) =
        let inputs = inputs |> Map.map (fun _ img -> (createTexture img).[TextureAspect.Color, 0, 0])
        let x = createTexture x

        this.Solve(inputs, x.[TextureAspect.Color, 0, 0], cfg)

        let res = runtime.Download(x, 0, 0)
        runtime.DeleteTexture x
        inputs |> Map.iter (fun _ t -> runtime.DeleteTexture t.Texture)
        res

type MultigridConfig =
    {
        gradientTolerance       : float
        stepTolerance           : float
        cycles                  : int
        smoothIterations        : int
        solveIterations         : int
        correctIterations       : int
        useGuess                : bool
        maxSolveSize            : V2i
        debugPath               : Option<string>
    }

type MultigridSolver2d<'f, 'v when 'v : unmanaged and 'f :> FShade.Formats.IFloatingFormat> (runtime : IRuntime, residual : Term.TermParameter2d -> Term<V2i> -> Term<V2i>) = 
    static let ceilDiv (a : int) (b : int) =
        if a % b = 0 then a / b
        else 1 + a / b
    
    static let ceilDiv2 (a : V2i) (b : V2i) =
        V2i(
            ceilDiv a.X b.X,
            ceilDiv a.Y b.Y
        )

    static let getFormat =
        LookupTable.lookupTable [
            typeof<FShade.Formats.r32f>, TextureFormat.R32f
            typeof<FShade.Formats.rg32f>, TextureFormat.Rg32f
            typeof<FShade.Formats.rgba32f>, TextureFormat.Rgba32f
        ]

    static let format = getFormat typeof<'f>
    static let real = RealInstances.instance<'v>
    
    let mul = runtime.CreateComputeShader (ConjugateGradientShaders.mul2d<'f>)
    let restrictWeight = runtime.CreateComputeShader (ConjugateGradientShaders.restrictWeight<'f>)
    let restrict = runtime.CreateComputeShader (ConjugateGradientShaders.restrict<'f>)
    let interpolate = runtime.CreateComputeShader (ConjugateGradientShaders.interpolate<'f>)
    let divergence = runtime.CreateComputeShader (ConjugateGradientShaders.divergence<'f>)
    
    let parts, cg = 
        let mutable parts = Map.empty
        
        let res (x : Term.TermParameter2d) (h : Term<V2i>) =
            let res = residual x h
            let r = Term.derivative "x" V2i.Zero res
            
            let rec allIsolations (b : Term<_>) =
                match b with
                    | Zero ->
                        []
                    | _ -> 
                        let (f, bf) = Term.isolate "h" b
                        
                        match f with
                            | One -> 
                                let (d, c) = Term.factorize "h" b
                                let hasX = Set.contains "x" (Term.names d) && Set.contains "x" (Term.names c)
                                if hasX then
                                    match c, d with
                                        | Zero, _ | _, Zero -> 
                                            [(Power(Uniform "h", Value 0.0), b)]
                                        | _ -> 
                                            (Power(Uniform "h", Value 0.0), c) :: allIsolations d
                                else
                                    [(Power(Uniform "h", Value 0.0), b)]
                            | f -> 
                                allIsolations bf |> List.map (fun (fi,b) ->
                                    Term.simplify (f * fi), b
                                )

            let residuals = 
                allIsolations r |> List.map (fun (f, ex) ->
                    match f with
                        | Power(Uniform "h", Value e) when Fun.IsTiny (Fun.Frac e) ->
                            -int e, ex

                        | _ ->
                            failwith "sadasda"
                )

            let overall =
                residuals |> List.sumBy (fun (e, ex) ->
                    let (d, c) = Term.factorize "x" ex

                    let name = sprintf "b_%d" (int e)
                    parts <- Map.add name (e, d, c) parts

                    let parameter = Term.TermParameter2d(name)
                    
                    (d + parameter.[0,0]) * (Uniform "h") ** -e
                )

            let f = Term.simplify overall
            Log.warn "0 = %s" (Term.toString f)

            f

        let cg = ConjugateGradientSolver2d<'f, 'v>(runtime, res)


        let final = 
            parts |> Map.map (fun name (e, d,c) ->
                let d,c = 
                    if e = 0 then
                        d,c
                    else 
                        let d = d * (Uniform "h") ** -e |> Term.simplify
                        let c = c * (Uniform "h") ** -e |> Term.simplify
                        d, c

                Log.warn "b_%d[0,0]  = %s" e (Term.toString c)
                Log.warn "Ax_%d[0,0] = %s" e (Term.toString d)

                (e,d,c)
            )
            
        final, cg
        
    let uconstantPart =
        parts 
        |> Map.toSeq
        |> Seq.map (fun (name,(e,d,c)) -> Term.parameterNames c)
        |> Set.unionMany
        
    let udependentPart =
        parts 
        |> Map.toSeq
        |> Seq.map (fun (name,(e,d,c)) -> Set.union (Term.parameterNames d) (Term.parameterNames c))
        |> Set.unionMany
        


    let computeResiduals0 = 
        let rreal = ReflectedReal.instance<'v>
        parts |> Map.map (fun name (e, d, c) ->
            runtime.CreateComputeShader(ConjugateGradientShaders.polynomial2d<'f, 'v> (Term.toReflectedCall Term.Read.image c) rreal.toV4)
        )
        
    let computeResiduals = 
        let rreal = ReflectedReal.instance<'v>
        parts |> Map.map (fun name (e, d, c) ->
            let t = d + c |> Term.simplify
            runtime.CreateComputeShader(ConjugateGradientShaders.polynomial2d<'f, 'v> (Term.toReflectedCall Term.Read.image t) rreal.toV4)
        )

    let createTexture (img : PixImage) =
        let t = runtime.CreateTexture(img.Size, TextureFormat.ofPixFormat img.PixFormat TextureParams.empty, 1, 1)
        runtime.Upload(t, 0, 0, img)
        t

   
    member x.ComputeResidualsZero(h : float, inputs : Map<string, ITextureSubResource>, dst : Map<string, ITextureSubResource>) =
        dst |> Map.iter (fun e dst ->
            match Map.tryFind e computeResiduals0 with
                | Some computeResidual0 ->
                    use input = runtime.NewInputBinding computeResidual0
                    input.["res"] <- dst
                    input.["h"] <- real.fromFloat h

                    for used in uconstantPart do
                        let r = inputs.[used]
                        input.[used] <- r
                        input.[sprintf "%sLevel" used] <- 0
                        input.[sprintf "%sSize" used] <- r.Size.XY
            
                    input.Flush()

                    runtime.Run [
                        ComputeCommand.Bind computeResidual0
                        ComputeCommand.SetInput input
                        ComputeCommand.Dispatch(ceilDiv2 dst.Size.XY computeResidual0.LocalSize.XY)
                    ]
                | None ->
                    ()
        )

    member x.ComputeResiduals(h : float, inputs : Map<string, ITextureSubResource>, dst : Map<string, ITextureSubResource>) =
        dst |> Map.iter (fun e dst ->
            match Map.tryFind e computeResiduals with
                | Some computeResidual ->
                    use input = runtime.NewInputBinding computeResidual
                    input.["res"] <- dst
                    input.["h"] <- real.fromFloat h
                    

                    for used in udependentPart do
                        let r = inputs.[used]
                        input.[used] <- r
                        input.[sprintf "%sLevel" used] <- 0
                        input.[sprintf "%sSize" used] <- r.Size.XY
            
                    input.Flush()

                    runtime.Run [
                        ComputeCommand.Bind computeResidual
                        ComputeCommand.SetInput input
                        ComputeCommand.Dispatch(ceilDiv2 dst.Size.XY computeResidual.LocalSize.XY)
                    ]
                | None ->
                    ()
        )


    member x.Restrict(src : ITextureSubResource, srcWeight : ITextureSubResource, dst : ITextureSubResource, dstWeight : ITextureSubResource, temp : ITextureSubResource) =
        use input = runtime.NewInputBinding mul
        input.["l"] <- src
        input.["r"] <- srcWeight
        input.["dst"] <- temp
        input.Flush()
        runtime.Run [
            ComputeCommand.Bind mul
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch (ceilDiv2 src.Size.XY mul.LocalSize.XY)
        ]
        

        use input = runtime.NewInputBinding restrictWeight
        input.["src"] <- src
        input.["weight"] <- srcWeight
        input.["weightTimesSrc"] <- temp
        input.["dst"] <- dst
        input.["dstWeight"] <- dstWeight
        input.Flush()
        runtime.Run [
            ComputeCommand.Bind restrictWeight
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch (ceilDiv2 dst.Size.XY restrictWeight.LocalSize.XY)
        ]

    member x.Restrict(src : ITextureSubResource, dst : ITextureSubResource, factor : float) =
        use input = runtime.NewInputBinding restrict
        input.["src"] <- src
        input.["dst"] <- dst
        input.["factor"] <- factor
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind restrict
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch (ceilDiv2 dst.Size.XY restrict.LocalSize.XY)
        ]

    member x.Interpolate(src : ITextureSubResource, dst : ITextureSubResource, factor : float) =
        use input = runtime.NewInputBinding interpolate
        input.["src"] <- src
        input.["dst"] <- dst
        input.["factor"] <- V4d.IIII * factor
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind interpolate
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch (ceilDiv2 dst.Size.XY interpolate.LocalSize.XY)
        ]

    member x.Divergence(src : ITextureSubResource, dst : ITextureSubResource) =
        use input = runtime.NewInputBinding divergence
        input.["src"] <- src
        input.["dst"] <- dst
        input.Flush()

        runtime.Run [
            ComputeCommand.Bind divergence
            ComputeCommand.SetInput input
            ComputeCommand.Dispatch (ceilDiv2 dst.Size.XY divergence.LocalSize.XY)
        ]

    member x.NextLevel(m : Map<string, ITextureSubResource>) =
        m |> Map.map (fun name img ->
            img.Texture.[img.Aspect, img.Level + 1, img.Slice]
        )
        

    member x.Restrict (m : Map<string, ITextureSubResource>) =
        m |> Map.map (fun name img ->
            let next = img.Texture.[img.Aspect, img.Level + 1, img.Slice]
            if not (name.StartsWith "__") then
                //if name.StartsWith "b_" then
                //    let e = name.Substring(2) |> int
                //    x.Restrict(img, next, 1.0 / float (1 <<< e))

                if name.StartsWith "w_" then
                    if not (Map.containsKey (name.Substring(2)) m) then
                        x.Restrict(img, next, 1.0)
                        
                else
                    let wname = "w_" + name
                    match Map.tryFind wname m with
                        | None ->
                            x.Restrict(img, next, 1.0)
                        | Some wimg ->
                            let wnext = wimg.Texture.[wimg.Aspect, wimg.Level + 1, wimg.Slice]
                            let temp = m.["__temp"]
                            x.Restrict(img, wimg, next, wnext, temp)
            next
        )

    member private x.Cycle(inputs : Map<string, ITextureSubResource>, bPing : Map<string, ITextureSubResource>, bPong : Map<string, ITextureSubResource> , iter : int, level : int, size : V2i, inputSize : V2i, cfg : MultigridConfig) =
        let hv = V2d inputSize / V2d size
        let h = 0.5 * (hv.X + hv.Y)
        

        match cfg.debugPath with
            | Some path -> 
                let needed = Set.remove "x" udependentPart
                inputs |> Map.iter (fun name t ->
                    if not (name.StartsWith "__") && Set.contains name needed then
                        let dst = PixImage<float32>(Col.Format.RGBA, t.Size.XY)
                        runtime.Download(t.Texture, t.Level, t.Slice, dst)
                        if name.StartsWith "b_" then
                            dst.GetMatrix<C4f>().SetMap(dst.GetMatrix<C4f>(), fun v -> ((v.ToV4f() + V4f.IIII) * 0.5f).ToC4f()) |> ignore
                        
                        let name = sprintf @"%d_%s_input_%dx%d.jpg" iter name size.X size.Y
                        dst.SaveAsImage (Path.combine [path; name])
                )

                bPing |> Map.iter (fun name t ->
                    let dst = PixImage<float32>(Col.Format.RGBA, t.Size.XY)
                    runtime.Download(t.Texture, t.Level, t.Slice, dst)
                    dst.GetMatrix<C4f>().SetMap(dst.GetMatrix<C4f>(), fun v -> ((v.ToV4f() + V4f.IIII) * 0.5f).ToC4f()) |> ignore
                    let name = sprintf @"%d_%s_ping_%dx%d.jpg" iter name size.X size.Y
                    dst.SaveAsImage (Path.combine [path; name]) 
                )
            | None -> 
                ()

        cg.Tools.Set(inputs.["x"], V4d.Zero)

        let cgConfig =
            {
                maxIterations = 0
                maxLineSearchIterations = 1
                gradientTolerance = cfg.gradientTolerance
                stepTolerance = cfg.stepTolerance
            }

        let mutable bPing = bPing
        let mutable bPong = bPong

        if size.AnyGreater cfg.maxSolveSize then
            let temp = inputs.["__temp"]
            let sol = inputs.["x"]
            
            if cfg.smoothIterations > 0 then
                failwith "[Multigrid] smooth iterations not implemented atm."
                // TODO: wrong
                cg.SolveInternal(h, Map.union inputs bPing, sol, { cgConfig with maxIterations = cfg.smoothIterations })
                
                // evacuate the current solution to temp
                runtime.Copy(sol, V3i.Zero, temp, V3i.Zero, sol.Size)
                
                // recompute residuals to bPong
                x.ComputeResiduals(h, Map.union inputs bPing, bPong)

                match cfg.debugPath with
                    | Some path -> 
                        bPong |> Map.iter (fun name t ->
                            let dst = PixImage<float32>(Col.Format.RGBA, t.Size.XY)
                            runtime.Download(t.Texture, t.Level, t.Slice, dst)
                            dst.GetMatrix<C4f>().SetMap(dst.GetMatrix<C4f>(), fun v -> ((v.ToV4f() + V4f.IIII) * 0.5f).ToC4f()) |> ignore
                            let name = sprintf @"%d_%s_pong_%dx%d.jpg" iter name size.X size.Y
                            dst.SaveAsImage (Path.combine [path; name]) 
                        )
                    | _ ->
                        ()

                // swap bPing and bPong
                Fun.Swap(&bPing, &bPong)
                
            let bPingHalf = x.Restrict(bPing)
            let bPongHalf = x.NextLevel(bPong)
            let inputsHalf = x.NextLevel(inputs)
            
            let half = V2i(max 1 (size.X / 2), max 1 (size.Y / 2))
            x.Cycle(inputsHalf, bPingHalf, bPongHalf, iter, level + 1, half, inputSize, cfg)

            x.Interpolate(inputsHalf.["x"], sol, 1.0)
           
            if cfg.smoothIterations > 0 then
                cg.Tools.MultiplyAdd(temp, real.one, sol, real.one)
                Fun.Swap(&bPing, &bPong)

            if cfg.correctIterations > 0 then
                cg.SolveInternal(h, Map.union inputs bPing, sol, { cgConfig with maxIterations = cfg.correctIterations })
            
           
        else
            if cfg.solveIterations > 0 then
                cg.SolveInternal(h, Map.union inputs bPing, inputs.["x"], { cgConfig with maxIterations = cfg.solveIterations })

        match cfg.debugPath with
            | Some path -> 
                let name = "x"
                let t = inputs.[name]
                let dst = PixImage<float32>(Col.Format.RGBA, t.Size.XY)
                runtime.Download(t.Texture, t.Level, t.Slice, dst)

                dst.GetMatrix<C4f>().SetMap(dst.GetMatrix<C4f>(), fun v -> ((v.ToV4f() + V4f.IIII) * 0.5f).ToC4f()) |> ignore

                let name = sprintf @"%d_%s_output_%dx%d.jpg" iter name size.X size.Y
                dst.SaveAsImage (Path.combine [path; name])
            | None ->
                ()
            
    member this.CreateTexture(size : V2i) =
        let res = runtime.CreateTexture(size, format, 1, 1)
        res
          
    member this.CreateTempTexture(size : V2i) =
        let levels = 1 + int(Fun.Floor(Fun.Log2 (max size.X size.Y)))
        let res = runtime.CreateTexture(size, format, levels, 1)
        res

    member this.CreateTexture(img : PixImage) =
        let res = runtime.CreateTexture(img.Size, TextureFormat.ofPixFormat img.PixFormat TextureParams.empty, 1, 1)
        runtime.Upload(res, 0, 0, img)
        res
        

    member this.CreateTempTexture(img : PixImage) =
        let levels = 1 + int(Fun.Floor(Fun.Log2 (max img.Size.X img.Size.Y)))
        let res = runtime.CreateTexture(img.Size, TextureFormat.ofPixFormat img.PixFormat TextureParams.empty, levels, 1)
        runtime.Upload(res, 0, 0, img)
        res

    member x.Tools = cg.Tools

    member this.Solve(inputs : Map<string, ITextureSubResource>, sum : ITextureSubResource, cfg : MultigridConfig) =
        use __ = runtime.NewInputBinding restrict

        let size = sum.Size
        let levels = 1 + int(Fun.Floor(Fun.Log2 (max sum.Size.X sum.Size.Y)))
        
        let ip =
            inputs |> Map.map (fun name i ->
                if i.Texture.MipMapLevels >= levels then
                    i
                else
                    let res = runtime.CreateTexture(size.XY, format, levels, 1)
                    runtime.Copy(i, V3i.Zero, res.[TextureAspect.Color, 0, 0], V3i.Zero, size)
                    res.[TextureAspect.Color, 0, 0]
            )

        let bPing = 
            parts |> Map.map (fun name (e,_,_) ->
                let t = runtime.CreateTexture(size.XY, format, levels, 1)
                t.[TextureAspect.Color, 0, 0]
            )

        let bPong =
            if cfg.smoothIterations > 0 then
                parts |> Map.map (fun name (e,_,_) ->
                    let t = runtime.CreateTexture(size.XY, format, levels, 1)
                    t.[TextureAspect.Color, 0, 0]
                )
            else
                Map.empty
            
        let r,deleteR = 
            match Map.tryFind "__r" inputs with
                | Some r -> r.Texture, false
                | None ->
                    let r = runtime.CreateTexture(size.XY, format, levels, 1)
                    r, true

        let d,deleteD = 
            match Map.tryFind "__d" inputs with
                | Some d -> d.Texture, false
                | None ->
                    let d = runtime.CreateTexture(size.XY, format, levels, 1)
                    d, true

        let temp,deleteTemp = 
            match Map.tryFind "__temp" inputs with
                | Some temp -> temp.Texture, false
                | None ->
                    let temp = runtime.CreateTexture(size.XY, format, levels, 1)
                    temp, true

        let x = runtime.CreateTexture(size.XY, format, levels, 1)

        let ip =
            ip
            |> Map.add "x" x.[TextureAspect.Color, 0, 0]
            |> Map.add "__r" r.[TextureAspect.Color, 0, 0]
            |> Map.add "__d" d.[TextureAspect.Color, 0, 0]
            |> Map.add "__temp" temp.[TextureAspect.Color, 0, 0]

        if not cfg.useGuess then 
            cg.Tools.Set(sum, V4d.Zero)
            this.ComputeResidualsZero(1.0, Map.union ip bPing, bPing)

        do // restrict all inputs
            let mutable v = ip
            for l in 1 .. levels - 1 do v <- this.Restrict(v)

        for i in 0 .. cfg.cycles - 1 do
            if i <> 0 || cfg.useGuess then 
                this.ComputeResiduals(1.0, Map.add "x" sum ip, bPing)
                
            cg.Tools.Set(x.[TextureAspect.Color, 0, 0], V4d.Zero)

            this.Cycle(ip, bPing, bPong, i, 0, size.XY, size.XY, cfg)

            cg.Tools.MultiplyAdd(x.[TextureAspect.Color, 0, 0], real.one, sum, real.one)

            
        runtime.DeleteTexture x
        if deleteR then runtime.DeleteTexture r
        if deleteD then runtime.DeleteTexture d
        if deleteTemp then runtime.DeleteTexture temp
        bPing |> Map.iter (fun _ t -> runtime.DeleteTexture t.Texture)
        bPong |> Map.iter (fun _ t -> runtime.DeleteTexture t.Texture)
        for (k,t) in Map.toSeq ip do
            match Map.tryFind k inputs with
                | Some it -> if t.Texture <> it.Texture then runtime.DeleteTexture t.Texture
                | _ -> ()

        cg.Tools.Average(sum)
    

    member this.Solve(inputs : Map<string, PixImage>, x : PixImage, cfg : MultigridConfig) =
        let inputs = inputs |> Map.map (fun _ img -> (createTexture img).[TextureAspect.Color, 0, 0])
        let x = createTexture x

        let avg = this.Solve(inputs, x.[TextureAspect.Color, 0, 0], cfg)

        let res = runtime.Download(x, 0, 0)
        runtime.DeleteTexture x
        inputs |> Map.iter (fun _ t -> runtime.DeleteTexture t.Texture)
        res









// This example illustrates how to render a simple triangle using aardvark.
let inline printMat (name : string) (m : IMatrix< ^a >) =
    let table = 
        [
            for y in 0L .. m.Dim.Y - 1L do
                yield [
                    for x in 0L .. m.Dim.X - 1L do
                        let str = sprintf "%.3f" m.[V2l(x,y)]
                        if str.StartsWith "-" then yield str
                        else yield " " + str
                ]
        ]

    let maxLength = table |> Seq.collect (fun row -> row |> Seq.map (fun s -> s.Length)) |> Seq.max
    let pad (str : string) =
        if str.Length < maxLength then 
            let m = maxLength - str.Length
            let b = m // m / 2
            let a = 0 // m - b

            System.String(' ', b) + str + System.String(' ', a)
        else    
            str

    let rows = 
        table |> List.map (fun row ->
            row |> List.map pad |> String.concat "  "
        )
    printfn "%s (%dx%d)" name m.Dim.Y m.Dim.X
    for r in rows do
        printfn "   %s" r





[<EntryPoint>]
let main argv = 
    ////let test = 0.25 * ((-x<float>.[-1] + 2.0 * x<float>.[0] - x<float>.[1] - 50.0) ** 2)
    
    ////let a = test.WithoutConstant("x")
    ////let b = -test.ConstantPart("x")
    
    ////printfn "%A = 0" test
    ////printfn ""
    ////printfn "%A = %A" a b
    ////printfn "%A = %A" (a.Derivative("x", 0)) (b.Derivative("x", 0))
    ////printfn "%A = %A" (a.Derivative("x", 1)) (b.Derivative("x", 1))




    //// f(x) = (A(x) - b)^2


    //// A(x) - b 



    ////let A = test.WithoutConstant("x")
    ////let b = test.ConstantPart("x")

    ////let final = 
    ////    //let test = test.Derivative("x", 0)
    ////    let coords = A.FreeParameters.["x"]
    ////    let s = coords |> Seq.sumBy (fun c -> A.Rename(fun i -> i - c))
    ////    let s = s + b
    ////    s.Derivative("x", 0)

    ////printfn "%A" final
    
    ////Environment.Exit 0


    use app = new HeadlessVulkanApplication()
    //app.Runtime.ShaderCachePath <- None
    let runtime = app.Runtime :> IRuntime


    let div = Term.TermParameter2d("div")
    let v = Term.TermParameter2d("v")
    let w_v = Term.TermParameter2d("w_v")


    // 1*x^2 + 0*y^2


    // (x - a)^2 + (x - b)^2 + 2(x-a)(x-b)
    // ((x-a) + (x-b))^2

    // f(x) = (A1x - b1)^2 + (A2x - b2)^2 + ....


    // df/dx = 2A1T(A1x - b1) + 2A2T(A2x - b2) + ....

    // A1T*A1x + A2T*A2x - (A1T*b1 + A2T*b2) = 0
    // (A1T*A1 + A2T*A2)x - (A1T*b1 + A2T*b2) = f'(x) = 0


    
    let polya (x : Term.TermParameter2d) (h : Term<V2i>) = 
        let cdiv = (4.0 * x.[0,0] - x.[-1,0] - x.[1,0] - x.[0,-1] - x.[0,1]) / h**2
        0.125 * (cdiv - div.[0,0]) ** 2 + 
        0.5 * w_v.[0,0] * (x.[0,0] - v.[0,0]) ** 2
        
    let fmt = Col.Format.RGBA
    let solver = MultigridSolver2d<FShade.Formats.rgba32f, V3f>(runtime, polya)
    
    let inputPix = PixImage.Create @"C:\temp\a\bla.png"
    let inputPix = inputPix.ToPixImage<byte>(Col.Format.RGBA)
    let size = inputPix.Size


    inputPix.SaveAsImage @"C:\temp\a\input.png"
    
    let input = solver.CreateTempTexture(inputPix)

    let div = 
        let tex = runtime.CreateTexture(size,TextureFormat.Rgba32f, 1, 1)
        solver.Divergence(input.[TextureAspect.Color,0,0],tex.[TextureAspect.Color,0,0])
        tex

    
    let edgeSize = 3L
    let isXEdge (v : V2l) = v.X < edgeSize || v.X >= int64 size.X - edgeSize
    let isYEdge (v : V2l) = v.Y < edgeSize  || v.Y >= int64 size.Y - edgeSize
    let isCorner (v : V2l) = isXEdge v && isYEdge v
    let isEdge (v : V2l) = isXEdge v || isYEdge v
    
    let c = V2l(size) / 2L
    let isCenter (v : V2l) =
        v = c || v = c - V2l.IO || v = c - V2l.OI || v = c - V2l.II

    let inputMat = inputPix.GetMatrix<C4b>()
    let v = PixImage<float32>(fmt, size)
    v.GetMatrix<C4f>().SetByCoord (fun (c : V2l) -> 
        if isEdge c then inputMat.[c].ToC4f()
        else C4f(0.0f, 0.0f, 0.0f, 0.0f)
    ) |> ignore
    
    let w_v = PixImage<float32>(fmt, size)
    w_v.GetMatrix<C4f>().SetByCoord (fun (c : V2l) -> 
        if isEdge c then C4f(1.0f, 1.0f, 1.0f, 1.0f)
        else C4f(0.0f, 0.0f, 0.0f, 0.0f)
    ) |> ignore

    let divPix = PixImage<float32>(Col.Format.RGBA, size)
    runtime.Download(div, 0, 0, divPix)

    let textures =
        Map.ofList [
            "v", solver.CreateTempTexture v
            "w_v", solver.CreateTempTexture w_v
            "div", div
        ]
        
    let textures =
        textures
        |> Map.add "__r" (solver.CreateTempTexture size)
        |> Map.add "__d" (solver.CreateTempTexture size)
        |> Map.add "__temp" (solver.CreateTempTexture size)

    let views = textures |> Map.map (fun _ t -> t.[TextureAspect.Color, 0, 0])
    let res = solver.CreateTexture(inputPix.ToPixImage<float32>(Col.Format.RGBA))
    

    let config =
        {
            cycles = 8
            maxSolveSize = V2i(4, 4)
            stepTolerance = 1E-5
            gradientTolerance = 1E-10
            smoothIterations = 0
            solveIterations = 8
            correctIterations = 8
            useGuess = false
            debugPath = None //Some @"C:\temp\a\debug"
        }

    Log.startTimed "solve"
    let avg = solver.Solve(views, res.[TextureAspect.Color, 0, 0], config)
    Log.stop()
    
    //runtime.DeleteTexture(res)
    //let res = solver.CreateTexture x
    //Log.startTimed "solve again"
    //solver.Solve(views, res.[TextureAspect.Color, 0, 0], 1, 1E-8)
    //Log.stop()
    let x = PixImage<float32>(fmt, size)
    runtime.Download(res, 0, 0, x)
    runtime.DeleteTexture res
    textures |> Map.iter (fun _ t -> runtime.DeleteTexture t)

    //let tavg = solver.Tools.Average(input.[TextureAspect.Color, 0, 0])
    //x.GetMatrix<C4f>().SetMap(x.GetMatrix<C4f>(), fun v -> (v.ToV3f() + (tavg - avg)).ToC4f()) |> ignore

    //let c = x.ToPixImage<float32>().GetChannel(Col.Channel.Gray)
    //printMat "x" c
    
    let f = PixImage<byte>(fmt, size)
    f.GetMatrix<C4b>().SetMap(x.GetMatrix<C4f>(), fun v -> C4b(byte (clamp 0.0f 1.0f v.R * 255.0f), byte (clamp 0.0f 1.0f v.G * 255.0f), byte (clamp 0.0f 1.0f v.B * 255.0f))) |> ignore
    f.SaveAsImage @"C:\temp\a\z_result.png"

    //for i in 0 .. 10 do
    //    x <- solver.Solve(inputs, x, 1E-3, 5, 1)
        
    //    let c = x.ToPixImage<float32>().GetChannel(Col.Channel.Gray)
    //    printMat "x" c
        
    //x <- solver.Solve(inputs, x, 1E-3, 20, 1)
    //let c = x.ToPixImage<float32>().GetChannel(Col.Channel.Gray)
    //printMat "x" c
        
        
    //x <- solver.Solve(inputs, x, 1E-3, 20, 1)
    //let c = x.ToPixImage<float32>().GetChannel(Col.Channel.Gray)
    //printMat "x" c

    //let term : Term<V2i> =
    //    let a (x : int) (y : int) = Term.parameter "x" (V2i(x,y))
    //    let h = Term.uniform "h"

    //    0.125 * ((4.0 * a 0 0 - a -1 0 - a 1 0 - a 0 -1 - a 0 1) / (h ** 2)) ** 2
    //    |> Term.derivative "x" V2i.Zero
    //    //|> Term.derivative "x" V2i.Zero

    //let code = Term.toCCode Term.Read.image term
    //printfn "%s" code



    0
