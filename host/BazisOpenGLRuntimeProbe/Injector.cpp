#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <string>
#include <iostream>

static DWORD FindBazisPid() {
    PROCESSENTRY32W pe{};
    pe.dwSize = sizeof(pe);
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    DWORD pid = 0;
    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, L"Bazis.exe") == 0) {
                pid = pe.th32ProcessID;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return pid;
}

static std::wstring OwnDirectory() {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring s(path);
    size_t p = s.find_last_of(L"\\/");
    return p == std::wstring::npos ? L"." : s.substr(0, p);
}

int wmain() {
    std::wcout << L"BAZIS OpenGL Runtime Probe x86\n";
    std::wcout << L"--------------------------------\n";

    DWORD pid = FindBazisPid();
    if (!pid) {
        std::wcerr << L"Bazis.exe is not running. Start BAZIS normally, then run this probe.\n";
        return 2;
    }
    std::wcout << L"Found Bazis.exe PID: " << pid << L"\n";

    std::wstring dllPath = OwnDirectory() + L"\\BAZISOpenGLRuntimeProbe.dll";
    DWORD attr = GetFileAttributesW(dllPath.c_str());
    if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY)) {
        std::wcerr << L"Missing helper DLL: " << dllPath << L"\n";
        return 3;
    }

    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                                 PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                                 FALSE, pid);
    if (!process) {
        std::wcerr << L"OpenProcess failed: " << GetLastError() << L". Try Run as administrator.\n";
        return 4;
    }

    const SIZE_T bytes = (dllPath.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        std::wcerr << L"VirtualAllocEx failed: " << GetLastError() << L"\n";
        CloseHandle(process);
        return 5;
    }

    if (!WriteProcessMemory(process, remote, dllPath.c_str(), bytes, nullptr)) {
        std::wcerr << L"WriteProcessMemory failed: " << GetLastError() << L"\n";
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 6;
    }

    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    auto loadLibraryW = reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(kernel32, "LoadLibraryW"));
    if (!loadLibraryW) {
        std::wcerr << L"Could not resolve LoadLibraryW.\n";
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 7;
    }

    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibraryW, remote, 0, nullptr);
    if (!thread) {
        std::wcerr << L"CreateRemoteThread failed: " << GetLastError() << L"\n";
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 8;
    }

    WaitForSingleObject(thread, 10000);
    DWORD remoteResult = 0;
    GetExitCodeThread(thread, &remoteResult);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    CloseHandle(process);

    if (!remoteResult) {
        std::wcerr << L"Helper DLL did not load.\n";
        return 9;
    }

    std::wcout << L"Probe attached successfully.\n";
    std::wcout << L"Now open/refresh the 3D model in BAZIS and rotate it once.\n";
    std::wcout << L"Expected files on Desktop:\n";
    std::wcout << L"  BAZIS-OpenGL-Runtime.json\n";
    std::wcout << L"  BAZIS-OpenGL-RuntimeProbe.log\n";
    std::wcout << L"You can close this console.\n";
    return 0;
}
