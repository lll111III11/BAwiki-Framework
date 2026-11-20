#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wincodec.h>
#include <d2d1_1.h>
#include <d3d11_1.h>
#include <wrl/client.h>
#include <mutex>
#include <algorithm>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

using Microsoft::WRL::ComPtr;

static std::mutex g_mutex;
static ComPtr<ID3D11Device>        g_d3d;
static ComPtr<ID3D11DeviceContext> g_ctx;
static ComPtr<ID2D1Factory1>       g_d2d;
static ComPtr<ID2D1Device>         g_d2dDev;
static ComPtr<ID2D1DeviceContext>  g_dc;
static ComPtr<IWICImagingFactory>  g_wic;
static bool g_ready = false;
static bool g_hardware = false;
static bool g_tried = false;

static bool InitOnce()
{
    if (g_tried) return g_ready;
    g_tried = true;

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hrCo) && hrCo != RPC_E_CHANGED_MODE) return false;

    if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&g_wic))))
        return false;

    D3D_FEATURE_LEVEL fl = {};
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    HRESULT hrDev = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                      D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, _countof(levels),
                                      D3D11_SDK_VERSION, &g_d3d, &fl, &g_ctx);
    if (SUCCEEDED(hrDev))
    {
        g_hardware = true;
    }
    else
    {

        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
                                     D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, _countof(levels),
                                     D3D11_SDK_VERSION, &g_d3d, &fl, &g_ctx)))
            return false;
        g_hardware = false;
    }

    ComPtr<IDXGIDevice> dxgiDev;
    if (FAILED(g_d3d.As(&dxgiDev))) return false;

    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, IID_PPV_ARGS(&g_d2d)))) return false;
    if (FAILED(g_d2d->CreateDevice(dxgiDev.Get(), &g_d2dDev))) return false;
    if (FAILED(g_d2dDev->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &g_dc))) return false;

    g_ready = true;
    return true;
}

extern "C" __declspec(dllexport) int __cdecl BawikiGpuThumb(const wchar_t* src, const wchar_t* dst,
                                                           int maxDim, int quality)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!src || !dst || maxDim <= 0) return 1;
    if (!InitOnce()) return 2;

    ComPtr<IWICBitmapDecoder> dec;
    if (FAILED(g_wic->CreateDecoderFromFilename(src, nullptr, GENERIC_READ,
                                               WICDecodeMetadataCacheOnDemand, &dec)))
        return 3;
    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(dec->GetFrame(0, &frame))) return 4;
    ComPtr<IWICFormatConverter> conv;
    if (FAILED(g_wic->CreateFormatConverter(&conv))) return 5;
    if (FAILED(conv->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA,
                                WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
        return 6;

    UINT sw = 0, sh = 0;
    conv->GetSize(&sw, &sh);
    if (!sw || !sh) return 7;
    float scale = std::min(1.0f, (float)maxDim / (float)std::max(sw, sh));
    int tw = std::max(1, (int)(sw * scale));
    int th = std::max(1, (int)(sh * scale));

    ComPtr<ID2D1Bitmap1> srcBmp;
    if (FAILED(g_dc->CreateBitmapFromWicBitmap(conv.Get(), nullptr, &srcBmp))) return 8;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = tw; td.Height = th; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> targetTex;
    if (FAILED(g_d3d->CreateTexture2D(&td, nullptr, &targetTex))) return 9;

    ComPtr<IDXGISurface> surf;
    if (FAILED(targetTex.As(&surf))) return 10;

    D2D1_BITMAP_PROPERTIES1 tp = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    ComPtr<ID2D1Bitmap1> target;
    if (FAILED(g_dc->CreateBitmapFromDxgiSurface(surf.Get(), &tp, &target))) return 11;

    g_dc->SetTarget(target.Get());
    g_dc->BeginDraw();
    g_dc->Clear(D2D1::ColorF(1.0f, 1.0f, 1.0f, 1.0f));
    D2D1_RECT_F rect = D2D1::RectF(0, 0, (float)tw, (float)th);
    g_dc->DrawBitmap(srcBmp.Get(), &rect, 1.0f, D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, nullptr);
    HRESULT hr = g_dc->EndDraw();
    g_dc->SetTarget(nullptr);
    if (FAILED(hr)) return 12;

    D3D11_TEXTURE2D_DESC sd = td;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(g_d3d->CreateTexture2D(&sd, nullptr, &staging))) return 13;
    g_ctx->CopyResource(staging.Get(), targetTex.Get());

    D3D11_MAPPED_SUBRESOURCE map = {};
    if (FAILED(g_ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return 14;

    ComPtr<IWICBitmap> bmp;
    hr = g_wic->CreateBitmapFromMemory(tw, th, GUID_WICPixelFormat32bppPBGRA,
                                       map.RowPitch, map.RowPitch * th,
                                       (BYTE*)map.pData, &bmp);
    g_ctx->Unmap(staging.Get(), 0);
    if (FAILED(hr)) return 15;

    ComPtr<IWICFormatConverter> outConv;
    if (FAILED(g_wic->CreateFormatConverter(&outConv))) return 16;
    if (FAILED(outConv->Initialize(bmp.Get(), GUID_WICPixelFormat24bppBGR,
                                   WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
        return 17;

    ComPtr<IWICStream> stream;
    if (FAILED(g_wic->CreateStream(&stream))) return 18;
    if (FAILED(stream->InitializeFromFilename(dst, GENERIC_WRITE))) return 19;

    ComPtr<IWICBitmapEncoder> enc;
    if (FAILED(g_wic->CreateEncoder(GUID_ContainerFormatJpeg, nullptr, &enc))) return 20;
    if (FAILED(enc->Initialize(stream.Get(), WICBitmapEncoderNoCache))) return 21;

    ComPtr<IWICBitmapFrameEncode> fe;
    ComPtr<IPropertyBag2> pb;
    if (FAILED(enc->CreateNewFrame(&fe, &pb))) return 22;
    if (FAILED(fe->Initialize(pb.Get()))) return 23;
    fe->SetSize(tw, th);
    WICPixelFormatGUID fmt = GUID_WICPixelFormat24bppBGR;
    if (FAILED(fe->SetPixelFormat(&fmt))) return 24;
    if (FAILED(fe->WriteSource(outConv.Get(), nullptr))) return 25;
    if (FAILED(fe->Commit())) return 26;
    if (FAILED(enc->Commit())) return 27;

    (void)quality;
    return 0;
}

extern "C" __declspec(dllexport) int __cdecl BawikiGpuAvailable()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return (InitOnce() && g_hardware) ? 1 : 0;
}
