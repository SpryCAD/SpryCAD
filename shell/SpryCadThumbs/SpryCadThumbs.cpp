// Windows Explorer thumbnail handler for SpryCAD parts (.sprt).
//
// A part saved as a container is a ZIP holding model.json and thumbnail.png, the preview the app renders on
// save; the PNG is stored uncompressed (it is compressed already). This handler finds that entry, decodes it with
// WIC and hands Explorer a bitmap. Plain JSON parts have no preview: the handler declines and Explorer shows the
// file type icon. Explorer runs thumbnail handlers in an isolated surrogate process, on the file's stream.
//
// Built by build\make-thumbs.ps1 (cl.exe from the Visual Studio C++ build tools) and registered by the installer;
// for a development build, "regsvr32 SpryCadThumbs.dll" registers it for the current user.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <new>
#include <vector>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

// {2F5A5D18-E9B5-4929-90DF-C338D2FA19B5}: also in installer\sprycad.iss
static const CLSID CLSID_SpryThumbnail = { 0x2f5a5d18, 0xe9b5, 0x4929, { 0x90, 0xdf, 0xc3, 0x38, 0xd2, 0xfa, 0x19, 0xb5 } };
static const wchar_t* const ClsidText = L"{2F5A5D18-E9B5-4929-90DF-C338D2FA19B5}";
// the shell's thumbnail handler slot (IThumbnailProvider)
static const wchar_t* const ThumbnailShellEx = L"{e357fccd-a995-4576-b01f-234630154e96}";

static HMODULE g_module;
static long g_objects;   // live handler objects plus server locks: the DLL can unload at zero

// ---- reading the preview out of the container -----------------------------------------------------------------

static const size_t MaxFile = 512u << 20; // a part bigger than this gets no thumbnail

static HRESULT ReadAll(IStream* s, std::vector<BYTE>& out)
{
    STATSTG st{};
    if (SUCCEEDED(s->Stat(&st, STATFLAG_NONAME)))
    {
        if (st.cbSize.QuadPart > MaxFile) return E_OUTOFMEMORY;
        out.reserve(static_cast<size_t>(st.cbSize.QuadPart));
    }
    LARGE_INTEGER zero{};
    s->Seek(zero, STREAM_SEEK_SET, nullptr);
    BYTE buf[64 * 1024];
    for (;;)
    {
        ULONG n = 0;
        HRESULT hr = s->Read(buf, sizeof buf, &n);
        if (FAILED(hr)) return hr;
        if (n == 0) break;
        out.insert(out.end(), buf, buf + n);
        if (out.size() > MaxFile) return E_OUTOFMEMORY;
    }
    return S_OK;
}

static uint16_t U16(const BYTE* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }
static uint32_t U32(const BYTE* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<uint32_t>(p[3]) << 24); }

// The uncompressed thumbnail.png entry of a ZIP, found through its central directory. False for anything else
// (a plain JSON part, no preview saved, a compressed or damaged entry).
static bool FindThumbnail(const std::vector<BYTE>& z, const BYTE*& data, size_t& size)
{
    const size_t n = z.size();
    if (n < 22 || U32(&z[0]) != 0x04034b50) return false; // not a ZIP
    // end of central directory record: the last 22 bytes, or up to 64K earlier when the ZIP has a comment
    size_t eocd = SIZE_MAX;
    for (size_t i = n - 22;; i--)
    {
        if (U32(&z[i]) == 0x06054b50) { eocd = i; break; }
        if (i == 0 || n - i > 22 + 0xFFFF) break;
    }
    if (eocd == SIZE_MAX) return false;
    const uint16_t count = U16(&z[eocd + 10]);
    const size_t cdSize = U32(&z[eocd + 12]), cdOffset = U32(&z[eocd + 16]);
    if (cdOffset + cdSize > n) return false;
    static const char Name[] = "thumbnail.png";
    size_t p = cdOffset;
    for (unsigned k = 0; k < count; k++)
    {
        if (p + 46 > n || U32(&z[p]) != 0x02014b50) return false;
        const uint16_t method = U16(&z[p + 10]);
        const size_t compressed = U32(&z[p + 20]);
        const uint16_t nameLen = U16(&z[p + 28]), extraLen = U16(&z[p + 30]), commentLen = U16(&z[p + 32]);
        const size_t local = U32(&z[p + 42]);
        if (p + 46 + nameLen > n) return false;
        if (nameLen == sizeof Name - 1 && std::memcmp(&z[p + 46], Name, nameLen) == 0)
        {
            if (method != 0) return false; // only stored entries (the app writes the PNG uncompressed)
            if (local + 30 > n || U32(&z[local]) != 0x04034b50) return false;
            const size_t start = local + 30 + U16(&z[local + 26]) + U16(&z[local + 28]);
            if (start + compressed > n) return false;
            data = &z[start];
            size = compressed;
            return true;
        }
        p += 46 + nameLen + extraLen + commentLen;
    }
    return false;
}

// Decodes a PNG and scales it to fit cx x cx (keeping its proportions) into a 32-bit top-down DIB with alpha.
static HRESULT PngToBitmap(const BYTE* png, size_t size, UINT cx, HBITMAP* out)
{
    ComPtr<IWICImagingFactory> factory;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;
    ComPtr<IWICStream> stream;
    if (FAILED(hr = factory->CreateStream(&stream))) return hr;
    if (FAILED(hr = stream->InitializeFromMemory(const_cast<BYTE*>(png), static_cast<DWORD>(size)))) return hr;
    ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(hr = factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder))) return hr;
    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(hr = decoder->GetFrame(0, &frame))) return hr;
    UINT w = 0, h = 0;
    if (FAILED(hr = frame->GetSize(&w, &h))) return hr;
    if (w == 0 || h == 0 || cx == 0) return E_FAIL;
    const double k = static_cast<double>(cx) / (std::max)(w, h);
    const UINT tw = (std::max)(1u, static_cast<UINT>(w * k + 0.5)), th = (std::max)(1u, static_cast<UINT>(h * k + 0.5));
    ComPtr<IWICBitmapScaler> scaler;
    if (FAILED(hr = factory->CreateBitmapScaler(&scaler))) return hr;
    if (FAILED(hr = scaler->Initialize(frame.Get(), tw, th, WICBitmapInterpolationModeFant))) return hr;
    ComPtr<IWICFormatConverter> converter;
    if (FAILED(hr = factory->CreateFormatConverter(&converter))) return hr;
    if (FAILED(hr = converter->Initialize(scaler.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return hr;

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = static_cast<LONG>(tw);
    bi.bmiHeader.biHeight = -static_cast<LONG>(th); // top-down, as WIC writes rows
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bmp = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!bmp) return E_OUTOFMEMORY;
    hr = converter->CopyPixels(nullptr, tw * 4, tw * 4 * th, static_cast<BYTE*>(bits));
    if (FAILED(hr)) { DeleteObject(bmp); return hr; }
    *out = bmp;
    return S_OK;
}

// ---- the handler object -----------------------------------------------------------------------------------------

class ThumbnailProvider final : public IInitializeWithStream, public IThumbnailProvider
{
    long _refs = 1;
    ComPtr<IStream> _stream;

public:
    ThumbnailProvider() { InterlockedIncrement(&g_objects); }
    ~ThumbnailProvider() { InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB qit[] = {
            QITABENT(ThumbnailProvider, IInitializeWithStream),
            QITABENT(ThumbnailProvider, IThumbnailProvider),
            { nullptr, 0 },
        };
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        long r = InterlockedDecrement(&_refs);
        if (r == 0) delete this;
        return r;
    }

    IFACEMETHODIMP Initialize(IStream* stream, DWORD) override
    {
        if (!stream) return E_INVALIDARG;
        if (_stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        _stream = stream;
        return S_OK;
    }

    IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override
    {
        if (!bitmap || !alpha) return E_POINTER;
        *bitmap = nullptr;
        *alpha = WTSAT_ARGB;
        if (!_stream) return E_UNEXPECTED;
        std::vector<BYTE> file;
        HRESULT hr = ReadAll(_stream.Get(), file);
        if (FAILED(hr)) return hr;
        const BYTE* png = nullptr;
        size_t size = 0;
        if (!FindThumbnail(file, png, size)) return E_FAIL; // no preview: Explorer shows the icon
        return PngToBitmap(png, size, cx, bitmap);
    }
};

class ClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB qit[] = { QITABENT(ClassFactory, IClassFactory), { nullptr, 0 } };
        return QISearch(this, qit, riid, ppv);
    }
    // a static object: lifetime is the DLL's
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* p = new (std::nothrow) ThumbnailProvider();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects);
        return S_OK;
    }
};

static ClassFactory g_factory;

// ---- DLL entry points -------------------------------------------------------------------------------------------

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_SpryThumbnail) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

// regsvr32 (development builds): registers the handler for the current user, as the installer does.
static HRESULT SetValue(const wchar_t* key, const wchar_t* name, const wchar_t* value)
{
    LSTATUS s = RegSetKeyValueW(HKEY_CURRENT_USER, key, name, REG_SZ, value, static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    return HRESULT_FROM_WIN32(s);
}

// the file types whose containers carry a preview: parts and assemblies
static const wchar_t* const Extensions[] = { L".sprt", L".sasm" };

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t clsidKey[128], serverKey[160], shellExKey[160];
    swprintf_s(clsidKey, L"Software\\Classes\\CLSID\\%s", ClsidText);
    swprintf_s(serverKey, L"%s\\InprocServer32", clsidKey);
    HRESULT hr;
    if (FAILED(hr = SetValue(clsidKey, nullptr, L"SpryCAD Thumbnail Handler"))) return hr;
    if (FAILED(hr = SetValue(serverKey, nullptr, path))) return hr;
    if (FAILED(hr = SetValue(serverKey, L"ThreadingModel", L"Apartment"))) return hr;
    for (auto ext : Extensions)
    {
        swprintf_s(shellExKey, L"Software\\Classes\\%s\\ShellEx\\%s", ext, ThumbnailShellEx);
        if (FAILED(hr = SetValue(shellExKey, nullptr, ClsidText))) return hr;
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    wchar_t clsidKey[128], shellExKey[160];
    swprintf_s(clsidKey, L"Software\\Classes\\CLSID\\%s", ClsidText);
    RegDeleteTreeW(HKEY_CURRENT_USER, clsidKey);
    for (auto ext : Extensions)
    {
        swprintf_s(shellExKey, L"Software\\Classes\\%s\\ShellEx\\%s", ext, ThumbnailShellEx);
        RegDeleteKeyW(HKEY_CURRENT_USER, shellExKey);
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
