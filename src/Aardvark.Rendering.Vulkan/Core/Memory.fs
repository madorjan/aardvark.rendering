﻿namespace Aardvark.Rendering.Vulkan

#nowarn "9"
#nowarn "51"

open System
open System.Threading
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open Microsoft.FSharp.NativeInterop
open Aardvark.Base

[<CompiledName("DevicePtr")>]
type deviceptr =
    class
        val mutable public IsView : bool
        val mutable public Memory : DeviceMemory
        val mutable public Handle : VkDeviceMemory
        val mutable public Size : int64
        val mutable public Offset : int64
        
        [<DefaultValue>]
        static val mutable private nullPtr : deviceptr

        member private x.Dispose(disposing : bool) =
            let mem = Interlocked.Exchange(&x.Memory, Unchecked.defaultof<_>)
            if Unchecked.notNull mem then
                if disposing || not x.IsView && x.Handle.IsValid then
                    if x.Offset <> 0L then failf "cannot free deviceptr with non-zero offset"
                    VkRaw.vkFreeMemory(mem.Device.Handle, x.Handle, NativePtr.zero)

                if disposing then GC.SuppressFinalize x
                x.Handle <- VkDeviceMemory.Null
                x.Offset <- 0L
                x.Size <- -1L

        member x.Dispose() = x.Dispose(true)

        member x.IsNull = Unchecked.isNull x.Memory

        interface IDisposable with
            member x.Dispose() = x.Dispose()

        override x.Finalize() =
            try x.Dispose(false)
            with _ -> ()


        static member (+) (ptr : deviceptr, offset : int64) =
            if ptr.IsNull then failf "cannot create view for null deviceptr"
            assert (offset <= ptr.Size)
            assert (ptr.Offset + offset >= 0L)
            new deviceptr(ptr.Memory, ptr.Handle, ptr.Offset + offset, ptr.Size - offset, true)

        static member (-) (ptr : deviceptr, offset : int64) =
            ptr + (-offset)

        static member Null = 
            if Unchecked.isNull deviceptr.nullPtr then
                deviceptr.nullPtr <- new deviceptr(Unchecked.defaultof<_>, VkDeviceMemory.Null, 0L, -1L, false)
            deviceptr.nullPtr

        internal new(mem, handle, off, size, isView) = { Memory = mem; Handle = handle; Offset = off; Size = size; IsView = isView }
    end


[<AbstractClass; Sealed; Extension>]
type MemoryExtensions private() =
    
    [<Extension>]
    static member Alloc(this : DeviceMemory, size : int64) =
        if this.Heap.TryAdd size then
            let mutable mem = VkDeviceMemory.Null
            let mutable info =
                VkMemoryAllocateInfo(
                    VkStructureType.MemoryAllocateInfo,
                    0n,
                    uint64 size,
                    uint32 this.TypeIndex
                )

            VkRaw.vkAllocateMemory(this.Device.Handle, &&info, NativePtr.zero, &&mem)
                |> check "vkAllocateMemory"

            new deviceptr(this, mem, 0L, size, false)
        else
            failf "out of memory (tried to allocate %A)" (size_t size)

    [<Extension>] 
    static member Sub(this : deviceptr, offset : int64, size : int64) =
        if this.IsNull then failf "cannot create view for null deviceptr"

        assert (size >= 0L)
        assert (offset + size <= this.Size)
        assert (this.Offset + offset >= 0L)
        new deviceptr(this.Memory, this.Handle, this.Offset + offset, size, true)

    [<Extension>] 
    static member Skip(this : deviceptr, offset : int64) =
        this + offset

    [<Extension>] 
    static member Take(this : deviceptr, size : int64) =
        if this.IsNull then failf "cannot create view for null deviceptr"

        assert (size >= 0L)
        assert (size <= this.Size)
        new deviceptr(this.Memory, this.Handle, this.Offset, size, true)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DevicePtr =
    
    let inline alloc (mem : DeviceMemory) (size : int64) = mem.Alloc size
    let inline free (ptr : deviceptr) = ptr.Dispose ()
    let inline skip (off : int64) (ptr : deviceptr) = ptr.Skip off
    let inline take (size : int64) (ptr : deviceptr) = ptr.Take size

    let inline isNull (ptr : deviceptr) = ptr.IsNull
    let inline isView (ptr : deviceptr) = ptr.IsView
    let inline handle (ptr : deviceptr) = ptr.Handle
    let inline offset (ptr : deviceptr) = ptr.Offset
    let inline size (ptr : deviceptr) = ptr.Size
    let inline memory (ptr : deviceptr) = ptr.Memory
    let inline device (ptr : deviceptr) = ptr.Memory.Device

    let copy (source : deviceptr) (target : deviceptr) (size : int64) =
        Command.custom (fun s ->
            let device = source.Memory.Device
            let createBuffer (ptr : deviceptr) (size : int64) (source : bool) =
                let align = ptr.Memory.Device.Physical.Properties.limits.minUniformBufferOffsetAlignment
                
                let m = align - 1UL |> int64
                let offset = ptr.Offset &&& ~~~m
                let add = ptr.Offset - offset

                let mutable info =
                    VkBufferCreateInfo(
                        VkStructureType.BufferCreateInfo,
                        0n,
                        VkBufferCreateFlags.None,
                        uint64 (size + add),
                        (if source then VkBufferUsageFlags.TransferSrcBit else VkBufferUsageFlags.TransferDstBit),
                        VkSharingMode.Exclusive,
                        0u, NativePtr.zero
                    )
                let mutable buffer = VkBuffer.Null
                VkRaw.vkCreateBuffer(device.Handle, &&info, NativePtr.zero, &&buffer)
                    |> check "vkCreateBuffer"
        
                let mutable reqs = VkMemoryRequirements()
                VkRaw.vkGetBufferMemoryRequirements(device.Handle, buffer, &&reqs)
                
                let mask = 1u <<< ptr.Memory.TypeIndex
                if mask &&& reqs.memoryTypeBits = 0u then
                    failf "incompatible memory for buffers: %A" ptr.Memory
  
                VkRaw.vkBindBufferMemory(device.Handle, buffer, ptr.Handle, uint64 offset)
                    |> check "vkBindBufferMemory"

                buffer, add

            let cmd = s.buffer

            let src, srcOff = createBuffer source size true
            let dst, dstOff = createBuffer target size false

            let mutable copy =
                VkBufferCopy(uint64 srcOff, uint64 dstOff, uint64 size)

            VkRaw.vkCmdCopyBuffer(
                cmd.Handle,
                src, dst, 1u, &&copy
            )

            let cleanup() = 
                VkRaw.vkDestroyBuffer(device.Handle, src, NativePtr.zero)
                VkRaw.vkDestroyBuffer(device.Handle, dst, NativePtr.zero)

            { s with cleanupActions = cleanup::s.cleanupActions }, ()
        )

    let map (ptr : deviceptr) (f : nativeint -> 'a) =
        let mutable res = 0n
        VkRaw.vkMapMemory(ptr.Memory.Device.Handle, ptr.Handle, uint64 ptr.Offset, uint64 ptr.Size, VkMemoryMapFlags.MinValue, &&res)
            |> check "vkMapMemory"

        try f res
        finally VkRaw.vkUnmapMemory(ptr.Memory.Device.Handle, ptr.Handle)