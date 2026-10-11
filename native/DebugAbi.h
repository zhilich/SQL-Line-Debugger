#include <windows.h>
#include <unknwn.h>
#include <cstdio>
#include <atomic>

// Experimental native COM ABI. x64 only. See README for source and limitations.
struct DebugSpan { DWORD index, start, end; };
struct Symbol { wchar_t* name; wchar_t* type; ULONG flags; int kind; union { wchar_t* value; IUnknown* object; }; };
static_assert(sizeof(Symbol)==32, "Unexpected symbol ABI");
struct Symbols : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Next(ULONG,Symbol*,ULONG*)=0;
    virtual HRESULT STDMETHODCALLTYPE Skip(ULONG)=0;
    virtual HRESULT STDMETHODCALLTYPE Reset()=0;
    virtual HRESULT STDMETHODCALLTYPE Clone(Symbols**)=0;
    virtual HRESULT STDMETHODCALLTYPE GetCount(DWORD*)=0;
};
struct Debug : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Continue(DWORD,DWORD,int)=0;
    virtual HRESULT STDMETHODCALLTYPE StopDebugEvents()=0;
    virtual HRESULT STDMETHODCALLTYPE SetBreakpoints(BOOL,LONG,void*)=0;
    virtual HRESULT STDMETHODCALLTYPE GetCallstack(DWORD,DWORD,LONG*,void**)=0;
    virtual HRESULT STDMETHODCALLTYPE AsyncBreak(DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE RevokeBreak(DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE SetSym(DWORD,DWORD,DWORD,Symbol*)=0;
    virtual HRESULT STDMETHODCALLTYPE GetSyms(DWORD,DWORD,DWORD,USHORT*,Symbol**)=0;
    virtual HRESULT STDMETHODCALLTYPE GetGlobalSym(DWORD,DWORD,wchar_t*,Symbol*)=0;
    virtual HRESULT STDMETHODCALLTYPE LookupTaskIdentifier(DWORD,DWORD,UINT64*)=0;
    virtual HRESULT STDMETHODCALLTYPE StopDebugging()=0;
    virtual HRESULT STDMETHODCALLTYPE CreateManagedDebuggerHost(wchar_t*,wchar_t*)=0;
    virtual HRESULT STDMETHODCALLTYPE ToggleManaged(BOOL)=0;
    virtual HRESULT STDMETHODCALLTYPE Ping()=0;
};
struct Events : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE NewConnection(DWORD,DWORD,DWORD,DWORD,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE NewBatch(DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE NewWorker(DWORD,DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE Push(DWORD,DWORD,const wchar_t*,const wchar_t*,DWORD,DWORD,DWORD,UINT_PTR,ULONG,USHORT,DebugSpan*)=0;
    virtual HRESULT STDMETHODCALLTYPE PushDynamic(DWORD,DWORD,DWORD,UINT_PTR,ULONG,wchar_t*,USHORT,DebugSpan*)=0;
    virtual HRESULT STDMETHODCALLTYPE Step(DWORD,DWORD,DWORD,DWORD,int,DebugSpan,ULONG)=0;
    virtual HRESULT STDMETHODCALLTYPE Pop(DWORD,DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE CloseConnection(DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE CloseBatch(DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE CloseWorker(DWORD,DWORD)=0;
    virtual HRESULT STDMETHODCALLTYPE Trace(DWORD,DWORD,const wchar_t*)=0;
    virtual HRESULT STDMETHODCALLTYPE CallManagedFromTsql(DWORD,DWORD,UINT64)=0;
    virtual HRESULT STDMETHODCALLTYPE ReturnToTsqlFromManaged(DWORD,DWORD,UINT64)=0;
    virtual HRESULT STDMETHODCALLTYPE CallTsqlFromManaged(DWORD,DWORD,UINT64)=0;
    virtual HRESULT STDMETHODCALLTYPE ReturnToManagedFromTsql(DWORD,DWORD,UINT64)=0;
    virtual HRESULT STDMETHODCALLTYPE Ping()=0;
};
// The installed SQL Debugging 130 proxy calls Ping after OnMatch. The older
// published interface omitted this slot; omitting it jumps into adjacent RTTI.
struct Match : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE OnMatch(const wchar_t*,DWORD,Debug*,Events**)=0;
    virtual HRESULT STDMETHODCALLTYPE Ping()=0;
};
struct Server : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Register(const wchar_t*,DWORD,Match*,BOOL,DWORD*,DWORD*)=0;
    virtual HRESULT STDMETHODCALLTYPE Unregister(DWORD)=0;
};
const GUID matchId={0x87bc18db,0xc8b3,0x11d5,{0xae,0x96,0x00,0xb0,0xd0,0xe9,0x3c,0xc1}};
const GUID eventId={0x87bc18dd,0xc8b3,0x11d5,{0xae,0x96,0x00,0xb0,0xd0,0xe9,0x3c,0xc1}};

