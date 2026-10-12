#include "DebugAbi.h"
#include <string>
#include <mutex>
#include <deque>
#include <condition_variable>

// Keep COM callback vtables native to avoid crashes from direct managed
// implementations. All strings passed to managed code are copies.
static std::mutex eventMutex;
static std::condition_variable eventReady;
static std::deque<std::wstring> eventQueue;
static bool eventsClosed = false;
static std::atomic<bool> stopping{false};
static std::atomic<bool> firstBreak{true};
static std::atomic<DWORD> targetSpid{0};
static std::atomic<DWORD> pausedBatch{0},pausedWorker{0};
static std::mutex debugMutex;
static Debug* activeDebug=nullptr;
static std::wstring Quote(const wchar_t* value) {
    std::wstring s=L"\"";
    if(value)for(const wchar_t* p=value;*p;++p) {
        switch(*p) {
        case L'"': s+=L"\\\"";break;
        case L'\\': s+=L"\\\\";break;
        case L'\n': s+=L"\\n";break;
        case L'\r': s+=L"\\r";break;
        case L'\t': s+=L"\\t";break;
        default: if(*p<32){wchar_t hex[7];swprintf_s(hex,L"\\u%04x",*p);s+=hex;}else s+=*p;
        }
    }
    return s+L"\"";
}
static void Emit(const std::wstring& json) {
    {std::lock_guard<std::mutex> lock(eventMutex);eventQueue.push_back(json);}
    eventReady.notify_one();
}
static void Error(const wchar_t* operation,HRESULT hr) {
    Emit(L"{\"kind\":\"error\",\"text\":"+Quote(operation)+L",\"hr\":"+std::to_wstring(hr)+L"}");
}
static std::wstring SpanJson(DebugSpan span) {
    return L"{\"index\":"+std::to_wstring(span.index)+L",\"start\":"+std::to_wstring(span.start)+L",\"end\":"+std::to_wstring(span.end)+L"}";
}
static std::wstring SpansJson(USHORT count,DebugSpan* spans) {
    std::wstring s=L"[";
    for(USHORT i=0;spans&&i<count;++i){if(i)s+=L",";s+=SpanJson(spans[i]);}
    return s+L"]";
}
static void FreeSymbol(Symbol& s) {
    CoTaskMemFree(s.name);CoTaskMemFree(s.type);
    if(s.kind==0)CoTaskMemFree(s.value);else if(s.object)s.object->Release();
}
static std::wstring SymbolJson(Symbol& s) {
    const wchar_t* value=(s.flags&64)?L"NULL":(s.kind==0?s.value:s.kind==1?L"(compound)":L"(binary)");
    return L"{\"name\":"+Quote(s.name)+L",\"type\":"+Quote(s.type)+L",\"value\":"+Quote(value)+L",\"flags\":"+std::to_wstring(s.flags)+L"}";
}
class Sink : public Match, public Events {
    std::atomic<ULONG> refs{1};
public:
    Debug* debug=nullptr;
    ~Sink(){if(debug)debug->Release();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p) override {
        if(!p)return E_POINTER;
        *p=nullptr;
        if(id==IID_IUnknown||id==matchId)*p=static_cast<Match*>(this);
        else if(id==eventId)*p=static_cast<Events*>(this);
        else return E_NOINTERFACE;
        AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG r=--refs;if(!r)delete this;return r;}
    HRESULT STDMETHODCALLTYPE OnMatch(const wchar_t*,DWORD,Debug* d,Events** e) override {
        if(debug)return E_UNEXPECTED; // One target connection per debugger process.
        debug=d;debug->AddRef();
        {std::lock_guard<std::mutex> lock(debugMutex);activeDebug=debug;}
        *e=static_cast<Events*>(this);AddRef();return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NewConnection(DWORD,DWORD,DWORD,DWORD spid,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*,DWORD,DWORD) override {
        targetSpid=spid;Emit(L"{\"kind\":\"connection\",\"spid\":"+std::to_wstring(spid)+L"}");return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NewBatch(DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE NewWorker(DWORD b,DWORD w,DWORD level) override {
        // Some SQL Server builds announce even the ordinary serial worker here.
        Emit(L"{\"kind\":\"trace\",\"text\":"+Quote((L"Worker batch="+std::to_wstring(b)+L" id="+std::to_wstring(w)+L" level="+std::to_wstring(level)).c_str())+L"}");return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Push(DWORD b,DWORD w,const wchar_t* db,const wchar_t* name,DWORD dbid,DWORD oid,DWORD level,UINT_PTR,ULONG,USHORT count,DebugSpan* spans) override {
        Emit(L"{\"kind\":\"push\",\"batch\":"+std::to_wstring(b)+L",\"worker\":"+std::to_wstring(w)+L",\"level\":"+std::to_wstring(level)+L",\"db\":"+Quote(db)+L",\"name\":"+Quote(name)+L",\"dbid\":"+std::to_wstring(dbid)+L",\"oid\":"+std::to_wstring(oid)+L",\"spans\":"+SpansJson(count,spans)+L"}");return S_OK;
    }
    HRESULT STDMETHODCALLTYPE PushDynamic(DWORD b,DWORD w,DWORD level,UINT_PTR,ULONG,wchar_t* text,USHORT count,DebugSpan* spans) override {
        Emit(L"{\"kind\":\"dynamic\",\"batch\":"+std::to_wstring(b)+L",\"worker\":"+std::to_wstring(w)+L",\"level\":"+std::to_wstring(level)+L",\"text\":"+Quote(text)+L",\"spans\":"+SpansJson(count,spans)+L"}");
        if(firstBreak.exchange(false)){HRESULT hr=debug->AsyncBreak(b,w);if(FAILED(hr))Error(L"AsyncBreak",hr);}
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Step(DWORD b,DWORD w,DWORD oid,DWORD level,int reason,DebugSpan span,ULONG) override {
        pausedBatch=b;pausedWorker=w;
        USHORT count=0;Symbol* symbols=nullptr;HRESULT hr=debug->GetSyms(b,w,level,&count,&symbols);
        std::wstring vars=L"[";
        if(SUCCEEDED(hr)&&symbols) {
            for(USHORT i=0;i<count;++i){if(i)vars+=L",";vars+=SymbolJson(symbols[i]);FreeSymbol(symbols[i]);}
            CoTaskMemFree(symbols);
        }else if(FAILED(hr))Error(L"GetSyms",hr);
        vars+=L"]";
        Emit(L"{\"kind\":\"step\",\"batch\":"+std::to_wstring(b)+L",\"worker\":"+std::to_wstring(w)+L",\"oid\":"+std::to_wstring(oid)+L",\"level\":"+std::to_wstring(level)+L",\"reason\":"+std::to_wstring(reason)+L",\"span\":"+SpanJson(span)+L",\"variables\":"+vars+L"}");
        // Managed side owns the pause gate. Continue always steps a real server
        // statement, allowing software breakpoints inside nested routines.
        // Cancellation is a TDS attention from the client. Resume the statement
        // engine to consume it; StopDebugging here poisons subsequent sessions
        // on the tested LocalDB build and means detach, not query cancellation.
        // A paused callback must return to SQL Server immediately. Holding the
        // RPC open times out long human pauses; ResumeDebugger continues later.
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Pop(DWORD b,DWORD w,DWORD level) override {
        Emit(L"{\"kind\":\"pop\",\"batch\":"+std::to_wstring(b)+L",\"worker\":"+std::to_wstring(w)+L",\"level\":"+std::to_wstring(level)+L"}");return S_OK;
    }
    HRESULT STDMETHODCALLTYPE CloseConnection(DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CloseBatch(DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CloseWorker(DWORD,DWORD) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE Trace(DWORD,DWORD,const wchar_t* text) override {Emit(L"{\"kind\":\"trace\",\"text\":"+Quote(text)+L"}");return S_OK;}
    HRESULT STDMETHODCALLTYPE CallManagedFromTsql(DWORD,DWORD,UINT64) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ReturnToTsqlFromManaged(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE CallTsqlFromManaged(DWORD,DWORD,UINT64) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ReturnToManagedFromTsql(DWORD,DWORD,UINT64) override{return S_OK;}
    HRESULT STDMETHODCALLTYPE Ping() override{return S_OK;}
};
static Server* server=nullptr;
static Sink* sink=nullptr;
static DWORD cookie=0;
extern "C" __declspec(dllexport) HRESULT StartDebugger(Server* instance,const wchar_t* machine,DWORD pid) {
    if(server)return E_UNEXPECTED;
    {std::lock_guard<std::mutex> lock(eventMutex);eventQueue.clear();eventsClosed=false;}
    stopping=false;firstBreak=true;targetSpid=0;sink=new Sink();DWORD serverPid=0;
    HRESULT hr=instance->Register(machine,pid,sink,FALSE,&cookie,&serverPid);
    if(SUCCEEDED(hr)){server=instance;server->AddRef();}
    else{sink->Release();sink=nullptr;}
    return hr;
}
extern "C" __declspec(dllexport) void RequestStop() {stopping=true;}
extern "C" __declspec(dllexport) HRESULT ReadDebuggerEvent(wchar_t** text) {
    if(!text)return E_POINTER;*text=nullptr;
    std::unique_lock<std::mutex> lock(eventMutex);
    eventReady.wait(lock,[]{return eventsClosed||!eventQueue.empty();});
    if(eventQueue.empty())return S_FALSE;
    const auto& item=eventQueue.front();
    *text=static_cast<wchar_t*>(CoTaskMemAlloc((item.size()+1)*sizeof(wchar_t)));
    if(!*text)return E_OUTOFMEMORY;
    memcpy(*text,item.c_str(),(item.size()+1)*sizeof(wchar_t));eventQueue.pop_front();return S_OK;
}
extern "C" __declspec(dllexport) HRESULT ResumeDebugger() {
    HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    if(FAILED(init))return init;
    Debug* d=nullptr;
    {std::lock_guard<std::mutex> lock(debugMutex);d=activeDebug;if(d)d->AddRef();}
    HRESULT hr=d?d->Continue(pausedBatch,pausedWorker,0):E_UNEXPECTED;
    if(d)d->Release();CoUninitialize();return hr;
}
extern "C" __declspec(dllexport) HRESULT FinishDebugger() {
    HRESULT result=S_OK;
    if(server){HRESULT hr=server->Unregister(cookie);if(FAILED(hr))result=hr;server->Release();server=nullptr;}
    {std::lock_guard<std::mutex> lock(debugMutex);activeDebug=nullptr;}
    if(sink){sink->Release();sink=nullptr;}
    return result;
}
extern "C" __declspec(dllexport) void CloseDebuggerEvents() {
    {std::lock_guard<std::mutex> lock(eventMutex);eventsClosed=true;}eventReady.notify_all();
}
