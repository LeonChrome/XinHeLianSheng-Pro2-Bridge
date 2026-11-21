/* The upstream WinUSB transport calls CreateFile on every candidate USB
 * interface. Select the physical adapter before it can open any handle. */
#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include "comparison_target.h"

int comparison_matches_path(const char *path);
void comparison_usb_open(int succeeded, DWORD error);

static char target_prefix[220] = COMPARISON_USB_PREFIX "#";
int comparison_matches_path(const char *path) {
    return path && !_strnicmp(path, target_prefix, strlen(target_prefix));
}

#ifdef COMPARISON_REALTEK
static char target_label[220] = COMPARISON_TARGET_LABEL;
static unsigned target_vendor = 0x0bda, target_product = 0x0852;
const char *comparison_target_label(void) { return target_label; }
unsigned comparison_vendor(void) { return target_vendor; }
unsigned comparison_product(void) { return target_product; }
int comparison_uses_0852_firmware(void) { return target_vendor == 0x0bda && target_product == 0x0852; }

/* Device instance IDs and the transport paths both carry the entire serial/
 * port component. A VID/PID match alone cannot select between two radios. */
int comparison_select_instance(const wchar_t *instance) {
    char id[200];
    if (!instance || wcslen(instance) >= sizeof(id)) return 0;
    size_t n = 0;
    for (; instance[n]; ++n) {
        if (instance[n] > 127) return 0;
        id[n] = (char)toupper((unsigned char)instance[n]);
    }
    id[n] = 0;
    if (n < 23 || strncmp(id, "USB\\VID_", 8) || strncmp(id + 12, "&PID_", 5)) return 0;
    for (unsigned i = 0; i < 4; ++i)
        if (!isxdigit((unsigned char)id[8+i]) || !isxdigit((unsigned char)id[17+i])) return 0;
    size_t serial = 21;
    if (!strncmp(id + serial, "&MI_", 4)) {
        if (n < 29 || !isxdigit((unsigned char)id[25]) || !isxdigit((unsigned char)id[26])) return 0;
        serial += 6;
    }
    if (id[serial++] != '\\' || !id[serial]) return 0;
    for (size_t i=serial; i<n; ++i)
        if (!isalnum((unsigned char)id[i]) && !strchr("&_.-",id[i])) return 0;
    unsigned vid, pid;
    if (sscanf(id+8,"%4x&PID_%4x",&vid,&pid)!=2 || (vid==0x10d7 && pid==0xb012) ||
        (vid==0x0bda && (pid!=0x0852 || serial!=22))) return 0;
    target_vendor=vid; target_product=pid;
    snprintf(target_label,sizeof(target_label),"%04X:%04X/%s",vid,pid,id+serial);
    for (size_t i=0; i<n; ++i) if (id[i]=='\\') id[i]='#';
    snprintf(target_prefix,sizeof(target_prefix),"\\\\?\\%s#",id);
    return 1;
}

int comparison_target_fixture(void) {
    if (!comparison_select_instance(L"USB\\VID_0BDA&PID_0852\\SECOND") || !comparison_uses_0852_firmware() ||
        comparison_matches_path(COMPARISON_USB_TEST_PATH) || comparison_matches_path("\\\\?\\USB#VID_0BDA&PID_0852#SECOND2#{g}") ||
        !comparison_matches_path("\\\\?\\usb#vid_0bda&pid_0852#second#{g}")) return 0;
    if (!comparison_select_instance(L"USB\\VID_0A5C&PID_21E8\\6&12&0&3") || comparison_uses_0852_firmware() ||
        comparison_matches_path("\\\\?\\USB#VID_0A5C&PID_21E8#6&12&0&30#{g}")) return 0;
    if (!comparison_select_instance(L"USB\\VID_1234&PID_1234&MI_00\\A") ||
        !comparison_matches_path("\\\\?\\usb#vid_1234&pid_1234&mi_00#a#{g}") ||
        comparison_matches_path("\\\\?\\usb#vid_1234&pid_1234&mi_01#a#{g}")) return 0;
    if (comparison_select_instance(L"USB\\VID_10D7&PID_B012\\A") || comparison_select_instance(L"USB\\VID_0BDA&PID_9999\\A") ||
        comparison_select_instance(L"USB\\VID_1234&PID_5678\\*") || comparison_select_instance(L"USB\\VID_1234&PID_5678\\") ||
        comparison_select_instance(L"USB\\VID_ZZZZ&PID_1234\\A")) return 0;
    return comparison_select_instance(L"USB\\VID_0BDA&PID_0852\\00E04C000001");
}
#endif

static HANDLE open_comparison_adapter(LPCSTR path, DWORD access, DWORD sharing,
        LPSECURITY_ATTRIBUTES attributes, DWORD disposition, DWORD flags, HANDLE template_file) {
    if (!comparison_matches_path(path)) {
        SetLastError(ERROR_ACCESS_DENIED);
        return NULL; /* The pinned port tests !usb_device_handle. */
    }
    HANDLE handle = CreateFileA(path, access, sharing, attributes, disposition, flags, template_file);
    DWORD error = handle == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
    comparison_usb_open(handle != INVALID_HANDLE_VALUE, error);
    if (handle == INVALID_HANDLE_VALUE) {
        SetLastError(error);
        return NULL;
    }
    return handle;
}

#undef CreateFile
#define CreateFile open_comparison_adapter
#include "hci_transport_h2_winusb.c"

#ifdef COMPARISON_REALTEK
/* Composite functions can publish a WinUSB interface class of their own.
 * Select a class only when SetupAPI returns a live interface belonging to
 * the exact requested devnode. CreateFile still checks the whole path. */
static int has_selected_interface(const GUID *guid,const wchar_t *instance) {
    HDEVINFO set=SetupDiGetClassDevsW(guid,instance,NULL,DIGCF_PRESENT|DIGCF_DEVICEINTERFACE);
    if(set==INVALID_HANDLE_VALUE)return 0;
    SP_DEVICE_INTERFACE_DATA iface={.cbSize=sizeof(iface)};
    int found=0;
    for(DWORD i=0;!found && SetupDiEnumDeviceInterfaces(set,NULL,guid,i,&iface);++i){
        DWORD needed=0;
        SetupDiGetDeviceInterfaceDetailW(set,&iface,NULL,0,&needed,NULL);
        if(needed<sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)||needed>65536)continue;
        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail=malloc(needed);
        if(!detail)break;
        detail->cbSize=sizeof(*detail);
        SP_DEVINFO_DATA node={.cbSize=sizeof(node)};
        wchar_t actual[200];
        found=SetupDiGetDeviceInterfaceDetailW(set,&iface,detail,needed,NULL,&node)&&
            SetupDiGetDeviceInstanceIdW(set,&node,actual,200,NULL)&&!_wcsicmp(actual,instance);
        free(detail);
    }
    SetupDiDestroyDeviceInfoList(set);
    return found;
}

void comparison_select_interface_class(const wchar_t *instance) {
    /* The tested 0852 uses its ordinary USB interface. */
    if(comparison_uses_0852_firmware())return;
    wchar_t path[320],values[1025]={0};
    if(swprintf(path,320,L"SYSTEM\\CurrentControlSet\\Enum\\%ls\\Device Parameters",instance)<0)return;
    DWORD bytes=sizeof(values)-2*sizeof(wchar_t);
    if(RegGetValueW(HKEY_LOCAL_MACHINE,path,L"DeviceInterfaceGUIDs",RRF_RT_REG_MULTI_SZ|RRF_RT_REG_SZ,NULL,values,&bytes)!=ERROR_SUCCESS)return;
    HMODULE ole=LoadLibraryExW(L"ole32.dll",NULL,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!ole)return;
    typedef HRESULT (WINAPI *ParseGuid)(LPCWSTR,GUID *);
    ParseGuid parse=(ParseGuid)(void*)GetProcAddress(ole,"CLSIDFromString");
    if(parse)for(const wchar_t *value=values;*value;value+=wcslen(value)+1){
        GUID guid;
        if(wcslen(value)==38 && SUCCEEDED(parse(value,&guid)) && has_selected_interface(&guid,instance)){
            GUID_DEVINTERFACE_USB_DEVICE=guid;
            break;
        }
    }
    FreeLibrary(ole);
}
#endif
