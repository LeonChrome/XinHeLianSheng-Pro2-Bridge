#ifndef PRO2_UTF8_FILES_H
#define PRO2_UTF8_FILES_H
#include <Windows.h>
#include <stdio.h>
#include <stdlib.h>
/* Paths passed by .NET are UTF-8, independently of the user's Windows locale. */
static inline FILE *relay_utf8_fopen(const char *path,const char *mode){
    int pn=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path,-1,NULL,0),mn=MultiByteToWideChar(CP_UTF8,0,mode,-1,NULL,0);
    if(!pn||!mn)return NULL;
    wchar_t*wp=malloc((size_t)pn*sizeof(wchar_t)),*wm=malloc((size_t)mn*sizeof(wchar_t));
    if(!wp||!wm){free(wp);free(wm);return NULL;}
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,path,-1,wp,pn);MultiByteToWideChar(CP_UTF8,0,mode,-1,wm,mn);
    FILE*file=_wfopen(wp,wm);free(wp);free(wm);return file;
}
#define fopen relay_utf8_fopen
#endif
