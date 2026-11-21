/* Feed the pinned chipset loader its original, packaged Linux firmware. Only
 * this translation unit redirects fopen. Pairing data stays with the upstream
 * Windows port. Omit the optional board config to retain the efuse defaults. */
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "comparison_target.h"

static FILE *open_packaged_patch(const char *name, const char *mode) {
    if (strcmp(name, "./rtl8852cu_fw") || strcmp(mode, "rb")) return NULL;
    HMODULE module = GetModuleHandleW(NULL);
    HRSRC resource = FindResourceW(module, MAKEINTRESOURCEW(101), MAKEINTRESOURCEW(10));
    if (!resource || SizeofResource(module, resource) != 113336) return NULL;
    HGLOBAL loaded = LoadResource(module, resource);
    const void *data = loaded ? LockResource(loaded) : NULL;
    if (!data) return NULL;

    /* BTstack takes a seekable CRT FILE. D asks the CRT to delete the
     * temporary copy on close, including when Windows closes the process. */
    wchar_t directory[MAX_PATH], path[MAX_PATH];
    DWORD count = GetTempPathW(MAX_PATH, directory);
    if (!count || count >= MAX_PATH || !GetTempFileNameW(directory, L"rtk", 0, path)) return NULL;
    FILE *stream = _wfopen(path, L"w+bD");
    if (!stream) {
        DeleteFileW(path);
        return NULL;
    }
    DWORD size = SizeofResource(module, resource);
    if (fwrite(data, 1, size, stream) != size || fseek(stream, 0, SEEK_SET)) {
        fclose(stream);
        return NULL;
    }
    return stream;
}

int comparison_firmware_fixture(void) {
    FILE *stream = open_packaged_patch("./rtl8852cu_fw", "rb");
    if (!stream) return 0;
    unsigned char signature[8], trailer[4];
    const unsigned char expected_trailer[] = {0x51, 0x04, 0xfd, 0x77};
    int ok = fread(signature, 1, sizeof(signature), stream) == sizeof(signature) &&
             !memcmp(signature, "Realtech", sizeof(signature)) &&
             !fseek(stream, -4, SEEK_END) && ftell(stream) == 113332 &&
             fread(trailer, 1, sizeof(trailer), stream) == sizeof(trailer) &&
             !memcmp(trailer, expected_trailer, sizeof(trailer));
    fclose(stream);
    return ok && !open_packaged_patch("./rtl8852cu_config", "rb");
}

#define fopen open_packaged_patch
#include "btstack_chipset_realtek.c"
