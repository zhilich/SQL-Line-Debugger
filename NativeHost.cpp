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
struct Match : IUnknown { virtual HRESULT STDMETHODCALLTYPE OnMatch(const wchar_t*,DWORD,Debug*,Events**)=0; };
struct Server : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE Register(const wchar_t*,DWORD,Match*,BOOL,DWORD*,DWORD*)=0;
    virtual HRESULT STDMETHODCALLTYPE Unregister(DWORD)=0;
};
const GUID matchId={0x87bc18db,0xc8b3,0x11d5,{0xae,0x96,0x00,0xb0,0xd0,0xe9,0x3c,0xc1}};
const GUID eventId={0x87bc18dd,0xc8b3,0x11d5,{0xae,0x96,0x00,0xb0,0xd0,0xe9,0x3c,0xc1}};
static std::atomic<int> steps{0}, tableSymbols{0}, tableChildren{0};
static bool markerSeen=false;
typedef void (__cdecl *InspectCallback)();
static InspectCallback inspectCallback=nullptr;
extern "C" __declspec(dllexport) void SetInspector(InspectCallback callback) {inspectCallback=callback;}
static void FreeSymbol(Symbol& s) {
    CoTaskMemFree(s.name);CoTaskMemFree(s.type);
    if(s.kind==0)CoTaskMemFree(s.value);else if(s.object)s.object->Release();
}
static void ReadSymbol(Symbol& s,int depth) {
    printf("Symbol depth=%d name=%ls type=%ls flags=%lu kind=%d",depth,s.name?s.name:L"",s.type?s.type:L"",s.flags,s.kind);
    if(s.kind==0)printf(" value=%ls",s.value?s.value:L"<null>");
    puts("");fflush(stdout);
    bool table=s.name && wcscmp(s.name,L"@probeTable")==0;
    if(table)++tableSymbols;
    if(s.name&&wcscmp(s.name,L"@probeScalar")==0&&s.kind==0&&s.value&&wcscmp(s.value,L"314159")==0)markerSeen=true;
    if(s.kind==1&&s.object&&depth<3) {
        Symbols* en=static_cast<Symbols*>(s.object);DWORD total=0;HRESULT hr=en->GetCount(&total);
        printf("ChildCount hr=%08lX count=%lu\n",hr,total);fflush(stdout);
        for(DWORD i=0;SUCCEEDED(hr)&&i<total&&i<10;++i) {
            Symbol child{};ULONG fetched=0;hr=en->Next(1,&child,&fetched);
            printf("ChildNext hr=%08lX fetched=%lu\n",hr,fetched);fflush(stdout);
            if(FAILED(hr)||!fetched)break;
            if(table)++tableChildren;ReadSymbol(child,depth+1);FreeSymbol(child);
        }
    }
}
class Sink : public Match,public Events {
    std::atomic<ULONG> refs{1};
public:
    Debug* debug=nullptr;
    bool namesChecked=false;
    ~Sink(){if(debug)debug->Release();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p) override {
        if(!p)return E_POINTER;*p=nullptr;
        if(id==IID_IUnknown||id==matchId)*p=static_cast<Match*>(this);
        else if(id==eventId)*p=static_cast<Events*>(this);
        else return E_NOINTERFACE;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG r=--refs;if(!r)delete this;return r;}
    HRESULT STDMETHODCALLTYPE OnMatch(const wchar_t*,DWORD pid,Debug* d,Events** e) override {
        printf("Native OnMatch pid=%lu\n",pid);fflush(stdout);
        if(debug)debug->Release();debug=d;debug->AddRef();*e=static_cast<Events*>(this);AddRef();return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NewConnection(DWORD,DWORD,DWORD,DWORD spid,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,DWORD,DWORD) override {printf("Native connection spid=%lu\n",spid);fflush(stdout);return S_OK;}
    HRESULT STDMETHODCALLTYPE NewBatch(DWORD b) override{printf("Native batch=%lu\n",b);fflush(stdout);return S_OK;}
    HRESULT STDMETHODCALLTYPE NewWorker(DWORD,DWORD,DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE Push(DWORD b,DWORD w,const wchar_t*,const wchar_t* name,DWORD,DWORD,DWORD level,UINT_PTR,ULONG,USHORT,DebugSpan*) override {
        printf("Native Push %ls level=%lu\n",name,level);fflush(stdout);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE PushDynamic(DWORD b,DWORD w,DWORD level,UINT_PTR,ULONG,wchar_t*,USHORT count,DebugSpan*) override {
        printf("Native PushDynamic level=%lu spans=%hu\n",level,count);fflush(stdout);
        HRESULT hr=debug->AsyncBreak(b,w);printf("Request first break hr=%08lX\n",hr);fflush(stdout);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Step(DWORD b,DWORD w,DWORD object,DWORD level,int reason,DebugSpan span,ULONG) override {
        int n=++steps;printf("Native Step=%d batch=%lu worker=%lu level=%lu reason=%d span=%lu:%lu-%lu\n",n,b,w,level,reason,span.index,span.start,span.end);fflush(stdout);
        USHORT count=0;Symbol* symbols=nullptr;HRESULT hr=debug->GetSyms(b,w,level,&count,&symbols);
        printf("Native GetSyms hr=%08lX count=%hu\n",hr,count);fflush(stdout);
        if(SUCCEEDED(hr)&&symbols){for(USHORT i=0;i<count;++i){ReadSymbol(symbols[i],0);FreeSymbol(symbols[i]);}CoTaskMemFree(symbols);}
        if(markerSeen&&!namesChecked) {
            namesChecked=true;
            if(inspectCallback)inspectCallback();
            const wchar_t* names[]={L"@@SPID",L"#ProbeTemp",L"@probeTable"};
            for(const wchar_t* name:names) {
                Symbol result{};hr=debug->GetGlobalSym(b,w,const_cast<wchar_t*>(name),&result);
                printf("GetGlobalSym name=%ls hr=%08lX\n",name,hr);fflush(stdout);
                if(SUCCEEDED(hr)){ReadSymbol(result,0);FreeSymbol(result);}
            }
        }
        hr=debug->Continue(b,w,n<100?0:3);printf("Native Continue hr=%08lX\n",hr);fflush(stdout);return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Pop(DWORD,DWORD,DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CloseConnection(DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CloseBatch(DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CloseWorker(DWORD,DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE Trace(DWORD,DWORD,const wchar_t* text) override{printf("Native Trace %ls\n",text);fflush(stdout);return S_OK;}
    HRESULT STDMETHODCALLTYPE CallManagedFromTsql(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE ReturnToTsqlFromManaged(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CallTsqlFromManaged(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE ReturnToManagedFromTsql(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE Ping() override{return S_OK;}
};
static Server* registeredServer=nullptr;static Sink* sink=nullptr;static DWORD cookie=0;
extern "C" __declspec(dllexport) HRESULT StartProbe(Server* server,const wchar_t* machine,DWORD pid) {
    sink=new Sink();DWORD serverPid=0;HRESULT hr=server->Register(machine,pid,sink,FALSE,&cookie,&serverPid);
    printf("Native Register hr=%08lX serverPid=%lu\n",hr,serverPid);fflush(stdout);
    if(SUCCEEDED(hr)){registeredServer=server;server->AddRef();}return hr;
}
extern "C" __declspec(dllexport) int FinishProbe() {
    if(sink&&sink->debug){HRESULT hr=sink->debug->StopDebugging();printf("Native StopDebugging=%08lX\n",hr);}
    if(registeredServer){HRESULT hr=registeredServer->Unregister(cookie);printf("Native Unregister=%08lX\n",hr);registeredServer->Release();registeredServer=nullptr;}
    if(sink){sink->Release();sink=nullptr;}
    printf("RESULT steps=%d markerSeen=%d tableSymbols=%d tableChildren=%d\n",steps.load(),markerSeen,tableSymbols.load(),tableChildren.load());fflush(stdout);
    return steps>0&&markerSeen&&tableSymbols>0?steps.load():0;
}
