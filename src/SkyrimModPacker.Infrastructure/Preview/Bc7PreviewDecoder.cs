using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.Preview;

/// <summary>Read-only BC7 decompression through Windows' D3D11 software device (WARP), including all eight BC7 modes.</summary>
[SupportedOSPlatform("windows")]
internal static class Bc7PreviewDecoder
{
    private static readonly Lazy<byte[]> Shader = new(() => CompileShader(1));
    private static readonly Lazy<byte[]> SmallShader = new(() => CompileShader(2));
    public static PreviewTexture Decode(string path, byte[] data, int offset, int width, int height, CancellationToken token)
    {
        var columns = (width + 3) / 4; var rows = (height + 3) / 4;
        var downsample = (long)width * height > 4 * 1024 * 1024;
        for (var y = 0; y < rows; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < columns; x++) if (data[offset + (y * columns + x) * 16] == 0) throw new InvalidDataException("Invalid BC7 block mode: " + path);
        }
        var objects = new List<IntPtr>(); var initial = IntPtr.Zero;
        var sourcePin = default(GCHandle); var shaderPin = default(GCHandle);
        try
        {
            // Software-only device is independent of the user's GPU and needs no window or swap chain.
            Check(D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 1, [0xB000], 1, 7, out var device, out _, out var context), "create BC7 device");
            objects.Add(device); objects.Add(context); token.ThrowIfCancellationRequested();
            sourcePin = GCHandle.Alloc(data, GCHandleType.Pinned);
            var sourceData = new SubresourceData { Data = IntPtr.Add(sourcePin.AddrOfPinnedObject(), offset), RowPitch = (uint)(columns * 16) };
            initial = Marshal.AllocHGlobal(Marshal.SizeOf<SubresourceData>()); Marshal.StructureToPtr(sourceData, initial, false);
            // Read UNORM for both UNORM and sRGB DDS: WPF needs the original encoded colour bytes.
            var sourceDesc = new TextureDesc { Width = (uint)(columns * 4), Height = (uint)(rows * 4), Mips = 1, ArraySize = 1, Format = 98, SampleCount = 1, Usage = 1, Bind = 8 };
            Check(Method<CreateTexture>(device, 5)(device, ref sourceDesc, initial, out var source), "upload BC7 texture"); objects.Add(source);
            Check(Method<CreateView>(device, 7)(device, source, IntPtr.Zero, out var view), "create BC7 texture view"); objects.Add(view);
            if (downsample) { width = (width + 1) / 2; height = (height + 1) / 2; }
            var targetDesc = new TextureDesc { Width = (uint)width, Height = (uint)height, Mips = 1, ArraySize = 1, Format = 28, SampleCount = 1, Bind = 128 };
            Check(Method<CreateTexture>(device, 5)(device, ref targetDesc, IntPtr.Zero, out var target), "create decoded texture"); objects.Add(target);
            Check(Method<CreateView>(device, 8)(device, target, IntPtr.Zero, out var output), "create decoded texture view"); objects.Add(output);
            targetDesc.Usage = 3; targetDesc.Bind = 0; targetDesc.CpuAccess = 0x20000;
            Check(Method<CreateTexture>(device, 5)(device, ref targetDesc, IntPtr.Zero, out var staging), "create texture readback"); objects.Add(staging);
            var code = (downsample ? SmallShader : Shader).Value; shaderPin = GCHandle.Alloc(code, GCHandleType.Pinned);
            Check(Method<CreateShader>(device, 18)(device, shaderPin.AddrOfPinnedObject(), (nuint)code.Length, IntPtr.Zero, out var shader), "create BC7 shader"); objects.Add(shader);
            Method<SetResources>(context, 67)(context, 0, 1, [view]);
            Method<SetOutputs>(context, 68)(context, 0, 1, [output], IntPtr.Zero);
            Method<SetShader>(context, 69)(context, shader, IntPtr.Zero, 0);
            Method<Dispatch>(context, 41)(context, (uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
            Method<CopyResource>(context, 47)(context, staging, target);
            token.ThrowIfCancellationRequested();
            Check(Method<Map>(context, 14)(context, staging, 0, 1, 0, out var mapped), "read decoded BC7 texture");
            try
            {
                if (mapped.Data == IntPtr.Zero || mapped.RowPitch < width * 4 || mapped.RowPitch > width * 4 + 1048576) throw new InvalidDataException("Invalid BC7 readback layout.");
                var pixels = new byte[checked(width * height * 4)];
                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested(); Marshal.Copy(IntPtr.Add(mapped.Data, checked(y * (int)mapped.RowPitch)), pixels, y * width * 4, width * 4);
                    for (var x = 0; x < width; x++) { var at = (y * width + x) * 4; (pixels[at], pixels[at + 2]) = (pixels[at + 2], pixels[at]); }
                }
                return new(path, width, height, pixels);
            }
            finally { Method<Unmap>(context, 15)(context, staging, 0); }
        }
        finally
        {
            if (sourcePin.IsAllocated) sourcePin.Free(); if (shaderPin.IsAllocated) shaderPin.Free();
            if (initial != IntPtr.Zero) Marshal.FreeHGlobal(initial);
            for (var i = objects.Count - 1; i >= 0; i--) if (objects[i] != IntPtr.Zero) Marshal.Release(objects[i]);
        }
    }
    private static byte[] CompileShader(int step)
    {
        var source = Encoding.ASCII.GetBytes("Texture2D<float4> Source:register(t0); RWTexture2D<float4> Target:register(u0); [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID){uint w,h;Target.GetDimensions(w,h);if(p.x<w&&p.y<h)Target[p.xy]=Source.Load(int3(p.xy*" + step + ",0));}");
        var code = IntPtr.Zero; var errors = IntPtr.Zero;
        try
        {
            var hr = D3DCompile(source, (nuint)source.Length, "Bc7Preview", IntPtr.Zero, IntPtr.Zero, "main", "cs_5_0", 0, 0, out code, out errors);
            if (hr < 0) throw new InvalidDataException("BC7 shader: " + (errors == IntPtr.Zero ? hr.ToString("X8") : Marshal.PtrToStringAnsi(Method<BlobPointer>(errors, 3)(errors))));
            var size = Method<BlobSize>(code, 4)(code); if (size > 1048576) throw new InvalidDataException("BC7 shader exceeds preview budget.");
            var result = new byte[(int)size]; Marshal.Copy(Method<BlobPointer>(code, 3)(code), result, 0, result.Length); return result;
        }
        finally { if (code != IntPtr.Zero) Marshal.Release(code); if (errors != IntPtr.Zero) Marshal.Release(errors); }
    }
    // Slots follow the native ID3D11Device/ID3D11DeviceContext vtables in the Windows SDK.
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Check(int hr, string operation) { if (hr < 0) throw new InvalidDataException($"Cannot {operation} (Direct3D 0x{hr:X8}).", Marshal.GetExceptionForHR(hr)); }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, Mips, ArraySize, Format, SampleCount, SampleQuality, Usage, Bind, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct SubresourceData { public IntPtr Data; public uint RowPitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDesc desc, IntPtr initial, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateView(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShader(IntPtr self, IntPtr data, nuint length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetResources(IntPtr self, uint start, uint count, [In] IntPtr[] resources);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetOutputs(IntPtr self, uint start, uint count, [In] IntPtr[] resources, IntPtr counts);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShader(IntPtr self, IntPtr shader, IntPtr instances, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Dispatch(IntPtr self, uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Map(IntPtr self, IntPtr resource, uint subresource, uint type, uint flags, out MappedResource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Unmap(IntPtr self, IntPtr resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint BlobSize(IntPtr self);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, [In] uint[] levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint selectedLevel, out IntPtr context);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)] private static extern int D3DCompile(byte[] data, nuint length, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string profile, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}
