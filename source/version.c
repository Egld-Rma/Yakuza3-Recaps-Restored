#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "patch.h"

FARPROC version_real[17];

static const char *const version_names[17] = {
    "GetFileVersionInfoA",
    "GetFileVersionInfoByHandle",
    "GetFileVersionInfoExA",
    "GetFileVersionInfoExW",
    "GetFileVersionInfoSizeA",
    "GetFileVersionInfoSizeExA",
    "GetFileVersionInfoSizeExW",
    "GetFileVersionInfoSizeW",
    "GetFileVersionInfoW",
    "VerFindFileA",
    "VerFindFileW",
    "VerInstallFileA",
    "VerInstallFileW",
    "VerLanguageNameA",
    "VerLanguageNameW",
    "VerQueryValueA",
    "VerQueryValueW",
};

#define FORWARD(i, name) \
    __attribute__((naked)) void fwd_##name(void) \
    { \
        __asm__ volatile("jmpq *version_real+8*" #i "(%rip)"); \
    }

FORWARD(0, GetFileVersionInfoA)
FORWARD(1, GetFileVersionInfoByHandle)
FORWARD(2, GetFileVersionInfoExA)
FORWARD(3, GetFileVersionInfoExW)
FORWARD(4, GetFileVersionInfoSizeA)
FORWARD(5, GetFileVersionInfoSizeExA)
FORWARD(6, GetFileVersionInfoSizeExW)
FORWARD(7, GetFileVersionInfoSizeW)
FORWARD(8, GetFileVersionInfoW)
FORWARD(9, VerFindFileA)
FORWARD(10, VerFindFileW)
FORWARD(11, VerInstallFileA)
FORWARD(12, VerInstallFileW)
FORWARD(13, VerLanguageNameA)
FORWARD(14, VerLanguageNameW)
FORWARD(15, VerQueryValueA)
FORWARD(16, VerQueryValueW)

static void load_real(void)
{
    WCHAR path[MAX_PATH];
    UINT n = GetSystemDirectoryW(path, MAX_PATH - 16);
    if (n == 0 || n >= MAX_PATH - 16)
        return;
    lstrcatW(path, L"\\version.dll");
    HMODULE h = LoadLibraryW(path);
    if (!h)
        return;
    for (int i = 0; i < 17; i++)
        version_real[i] = GetProcAddress(h, version_names[i]);
}

static int same(const unsigned char *a, const unsigned char *b, unsigned n)
{
    for (unsigned i = 0; i < n; i++)
        if (a[i] != b[i])
            return 0;
    return 1;
}

static void apply_patch(void)
{
    unsigned char *base = (unsigned char *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.SizeOfImage != IMAGE_SIZE)
        return;
    for (unsigned i = 0; i < REGION_COUNT; i++)
        if (!same(base + regions[i].rva, regions[i].before, regions[i].size))
            return;
    for (unsigned i = 0; i < REGION_COUNT; i++) {
        unsigned char *p = base + regions[i].rva;
        DWORD old;
        if (!VirtualProtect(p, regions[i].size, PAGE_EXECUTE_READWRITE, &old))
            return;
        for (unsigned j = 0; j < regions[i].size; j++)
            p[j] = regions[i].after[j];
        VirtualProtect(p, regions[i].size, old, &old);
    }
    FlushInstructionCache(GetCurrentProcess(), NULL, 0);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(instance);
        load_real();
        apply_patch();
    }
    return TRUE;
}
