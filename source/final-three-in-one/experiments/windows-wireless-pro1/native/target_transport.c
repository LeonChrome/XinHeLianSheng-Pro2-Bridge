/* Reuse upstream BTstack WinUSB with an exact-instance guard. No driver writes. */
#include <Windows.h>
#include <ctype.h>
#include <stdio.h>
#include <string.h>
static char allowed_prefix[512];
int relay_set_instance(const char * instance){
    if(!instance || strlen(instance)<20 || strlen(instance)>450 || _strnicmp(instance,"USB\\VID_",8))return 0;
    if(_strnicmp(instance,"USB\\VID_10D7&PID_B012\\",22) && _strnicmp(instance,"USB\\VID_0BDA&PID_0852\\",22))return 0;
    strcpy(allowed_prefix,"\\\\?\\");size_t n=4;
    for(size_t i=0;instance[i];i++){
        char c=instance[i];
        if(c=='\\')c='#';
        else if(!(isalnum((unsigned char)c)||c=='&'||c=='_'||c=='-'||c=='.'))return 0;
        allowed_prefix[n++]=c;
    }
    allowed_prefix[n++]='#';allowed_prefix[n]=0;return 1;
}
int relay_target_path_allowed(const char * path){return allowed_prefix[0] && path && !_strnicmp(path,allowed_prefix,strlen(allowed_prefix));}
static HANDLE guarded_CreateFileA(LPCSTR path,DWORD access,DWORD sharing,LPSECURITY_ATTRIBUTES attrs,DWORD disposition,DWORD flags,HANDLE template_file){
    if(!relay_target_path_allowed(path)){SetLastError(ERROR_ACCESS_DENIED);return NULL; /* upstream checks !handle */}
    puts("{\"event\":\"USB_EXACT_INSTANCE_OPEN\",\"driver_changed\":false}");
    return CreateFileA(path,access,sharing,attrs,disposition,flags,template_file);
}
#define CreateFileA guarded_CreateFileA
#undef CreateFile
#define CreateFile guarded_CreateFileA
#include "hci_transport_h2_winusb.c"
