#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <gl/GL.h>
#include <cstdio>
#include <string>
#include <sstream>
#include <iomanip>
#include <algorithm>

#pragma comment(lib, "opengl32.lib")

#ifndef GL_SHADING_LANGUAGE_VERSION
#define GL_SHADING_LANGUAGE_VERSION 0x8B8C
#endif
#ifndef GL_MAJOR_VERSION
#define GL_MAJOR_VERSION 0x821B
#endif
#ifndef GL_MINOR_VERSION
#define GL_MINOR_VERSION 0x821C
#endif
#ifndef GL_CONTEXT_FLAGS
#define GL_CONTEXT_FLAGS 0x821E
#endif
#ifndef GL_CONTEXT_PROFILE_MASK
#define GL_CONTEXT_PROFILE_MASK 0x9126
#endif
#ifndef GL_CONTEXT_CORE_PROFILE_BIT
#define GL_CONTEXT_CORE_PROFILE_BIT 0x00000001
#endif
#ifndef GL_CONTEXT_COMPATIBILITY_PROFILE_BIT
#define GL_CONTEXT_COMPATIBILITY_PROFILE_BIT 0x00000002
#endif

using SwapBuffersFn = BOOL (WINAPI*)(HDC);
static SwapBuffersFn g_originalSwapBuffers = nullptr;
static volatile LONG g_captured = 0;

static std::wstring DesktopPath(const wchar_t* fileName) {
    wchar_t profile[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"USERPROFILE", profile, MAX_PATH);
    std::wstring base = (n > 0 && n < MAX_PATH) ? std::wstring(profile) : L".";
    return base + L"\\Desktop\\" + fileName;
}

static std::string JsonEscape(const char* s) {
    if (!s) return "";
    std::ostringstream o;
    for (const unsigned char c : std::string(s)) {
        switch (c) {
            case '\\': o << "\\\\"; break;
            case '"': o << "\\\""; break;
            case '\n': o << "\\n"; break;
            case '\r': o << "\\r"; break;
            case '\t': o << "\\t"; break;
            default:
                if (c < 0x20) {
                    o << "\\u" << std::hex << std::setw(4) << std::setfill('0') << (int)c;
                } else {
                    o << c;
                }
        }
    }
    return o.str();
}

static std::string HexPtr(const void* p) {
    std::ostringstream o;
    o << "0x" << std::hex << std::uppercase << reinterpret_cast<uintptr_t>(p);
    return o.str();
}

static void AppendLog(const char* text) {
    const std::wstring path = DesktopPath(L"BAZIS-OpenGL-RuntimeProbe.log");
    FILE* f = nullptr;
    _wfopen_s(&f, path.c_str(), L"a, ccs=UTF-8");
    if (f) {
        fwprintf(f, L"%S\n", text);
        fclose(f);
    }
}

static void CaptureContext(HDC hdc) {
    if (InterlockedCompareExchange(&g_captured, 1, 0) != 0) return;

    HGLRC rc = wglGetCurrentContext();
    HDC currentDC = wglGetCurrentDC();
    if (!rc) {
        InterlockedExchange(&g_captured, 0);
        return;
    }

    const char* version = reinterpret_cast<const char*>(glGetString(GL_VERSION));
    const char* vendor = reinterpret_cast<const char*>(glGetString(GL_VENDOR));
    const char* renderer = reinterpret_cast<const char*>(glGetString(GL_RENDERER));
    const char* glsl = reinterpret_cast<const char*>(glGetString(GL_SHADING_LANGUAGE_VERSION));

    GLint major = 0, minor = 0, profileMask = 0, contextFlags = 0;
    glGetIntegerv(GL_MAJOR_VERSION, &major);
    glGetIntegerv(GL_MINOR_VERSION, &minor);
    glGetIntegerv(GL_CONTEXT_PROFILE_MASK, &profileMask);
    glGetIntegerv(GL_CONTEXT_FLAGS, &contextFlags);
    while (glGetError() != GL_NO_ERROR) {}

    std::string profile = "unknown/legacy";
    if (profileMask & GL_CONTEXT_COMPATIBILITY_PROFILE_BIT) profile = "compatibility";
    else if (profileMask & GL_CONTEXT_CORE_PROFILE_BIT) profile = "core";

    int pixelFormat = GetPixelFormat(currentDC ? currentDC : hdc);

    SYSTEMTIME st{};
    GetLocalTime(&st);

    std::ostringstream json;
    json << "{\n";
    json << "  \"probe\": \"BAZIS OpenGL Runtime Probe x86\",\n";
    json << "  \"time\": \"" << std::setfill('0') << std::setw(4) << st.wYear << "-"
         << std::setw(2) << st.wMonth << "-" << std::setw(2) << st.wDay << "T"
         << std::setw(2) << st.wHour << ":" << std::setw(2) << st.wMinute << ":" << std::setw(2) << st.wSecond << "\",\n";
    json << "  \"GL_VERSION\": \"" << JsonEscape(version) << "\",\n";
    json << "  \"GL_VENDOR\": \"" << JsonEscape(vendor) << "\",\n";
    json << "  \"GL_RENDERER\": \"" << JsonEscape(renderer) << "\",\n";
    json << "  \"GLSL_VERSION\": \"" << JsonEscape(glsl) << "\",\n";
    json << "  \"major\": " << major << ",\n";
    json << "  \"minor\": " << minor << ",\n";
    json << "  \"profileMask\": " << profileMask << ",\n";
    json << "  \"profile\": \"" << profile << "\",\n";
    json << "  \"contextFlags\": " << contextFlags << ",\n";
    json << "  \"HGLRC\": \"" << HexPtr(rc) << "\",\n";
    json << "  \"HDC\": \"" << HexPtr(currentDC ? currentDC : hdc) << "\",\n";
    json << "  \"pixelFormat\": " << pixelFormat << "\n";
    json << "}\n";

    const std::wstring path = DesktopPath(L"BAZIS-OpenGL-Runtime.json");
    FILE* f = nullptr;
    _wfopen_s(&f, path.c_str(), L"wb");
    if (f) {
        const std::string data = json.str();
        fwrite(data.data(), 1, data.size(), f);
        fclose(f);
        AppendLog("Runtime OpenGL context captured successfully.");
    } else {
        AppendLog("ERROR: could not create BAZIS-OpenGL-Runtime.json");
    }
}

static BOOL WINAPI HookSwapBuffers(HDC hdc) {
    CaptureContext(hdc);
    return g_originalSwapBuffers ? g_originalSwapBuffers(hdc) : FALSE;
}

static bool PatchSwapBuffersIAT(HMODULE module) {
    if (!module) return false;
    auto* base = reinterpret_cast<unsigned char*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return false;
    auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);

    for (; imp->Name; ++imp) {
        auto* firstThunk = reinterpret_cast<IMAGE_THUNK_DATA32*>(base + imp->FirstThunk);
        auto* origThunk = imp->OriginalFirstThunk
            ? reinterpret_cast<IMAGE_THUNK_DATA32*>(base + imp->OriginalFirstThunk)
            : nullptr;
        if (!origThunk) continue;

        for (; origThunk->u1.AddressOfData; ++origThunk, ++firstThunk) {
            if (IMAGE_SNAP_BY_ORDINAL32(origThunk->u1.Ordinal)) continue;
            auto* byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + origThunk->u1.AddressOfData);
            if (!byName || strcmp(reinterpret_cast<const char*>(byName->Name), "SwapBuffers") != 0) continue;

            DWORD oldProtect = 0;
            if (!VirtualProtect(&firstThunk->u1.Function, sizeof(DWORD), PAGE_READWRITE, &oldProtect)) return false;
            g_originalSwapBuffers = reinterpret_cast<SwapBuffersFn>(firstThunk->u1.Function);
            firstThunk->u1.Function = reinterpret_cast<DWORD>(&HookSwapBuffers);
            DWORD dummy = 0;
            VirtualProtect(&firstThunk->u1.Function, sizeof(DWORD), oldProtect, &dummy);
            FlushInstructionCache(GetCurrentProcess(), &firstThunk->u1.Function, sizeof(DWORD));
            return true;
        }
    }
    return false;
}

static DWORD WINAPI Bootstrap(LPVOID) {
    AppendLog("Probe DLL loaded into Bazis.exe; waiting for LibKernel3D.dll.");
    for (int i = 0; i < 120; ++i) {
        HMODULE kernel3d = GetModuleHandleW(L"LibKernel3D.dll");
        if (kernel3d) {
            if (PatchSwapBuffersIAT(kernel3d)) {
                AppendLog("SwapBuffers IAT hook installed in LibKernel3D.dll. Waiting for a rendered frame.");
                return 0;
            }
            AppendLog("ERROR: LibKernel3D.dll found, but SwapBuffers import was not patchable.");
            return 2;
        }
        Sleep(500);
    }
    AppendLog("ERROR: LibKernel3D.dll was not loaded within 60 seconds.");
    return 1;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        HANDLE h = CreateThread(nullptr, 0, Bootstrap, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    }
    return TRUE;
}
