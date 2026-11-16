using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenRecorder.Capture;

/// <summary>Bridges Vortice D3D11 objects and WinRT IDirect3DDevice / IDirect3DSurface.</summary>
internal static unsafe class Direct3DHelper
{
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    public static ID3D11Device CreateD3DDevice()
    {
        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        ];

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? device);
        if (hr.Failure || device is null)
        {
            hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device);
            hr.CheckError();
        }

        // The Media Foundation encoder uses the device from its own threads.
        using (var mt = device!.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }
        return device;
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var ptr));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public static IDirect3DSurface CreateDirect3DSurface(ID3D11Texture2D texture)
    {
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var ptr));
        try
        {
            return MarshalInterface<IDirect3DSurface>.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessIid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in accessIid, out var access));
            try
            {
                Guid texIid = Texture2DIid;
                IntPtr tex;
                // IDirect3DDxgiInterfaceAccess::GetInterface is vtable slot 3.
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
                Marshal.ThrowExceptionForHR(getInterface(access, &texIid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            MarshalInterface<IDirect3DSurface>.DisposeAbi(abi);
        }
    }
}
