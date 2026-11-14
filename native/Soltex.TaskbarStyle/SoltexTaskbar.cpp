// Soltex taskbar helper (version 2).
//
// Windows 11 draws its taskbar with XAML. The app loads this DLL onto the taskbar's thread (a message hook for a moment);
// there the DLL asks the process's own XAML to attach it through the documented XAML diagnostics API
// (InitializeXamlDiagnosticsEx). Windows then hands it an IXamlDiagnostics site, and the DLL watches the visual tree.
//
// It does two things, both reversible:
//  1. Taskbar look (TranslucentTB-class): swaps the Fill of Taskbar.TaskbarBackground's BackgroundFill/BackgroundStroke
//     rectangles for clear, solid, acrylic or blurred brushes, and puts the original brush back.
//  2. Style rules (Windhawk Taskbar Styler-class): "under-type | type | name | Owner.Property | value". Each matching
//     element gets the value as a local value. The previous local value is kept and restored when the rule goes away.
// Independent implementation; no TranslucentTB or Windhawk code.
//
// Threads: XAML objects belong to their XAML (UI) thread, so all XAML work runs there, from a message-only window the
// DLL creates on that thread. AdviseVisualTreeChange runs on a worker thread, because the call waits for the UI thread.
// The worker also waits for the app's signal, reads the settings and posts to the windows.
//
// Settings: HKCU\<key> values Look, Color, Line and Styles (REG_MULTI_SZ), read when the app signals the named event
// "<event>.<process id>". Report: Status ("targets= applied= look= hr= styled= styleApplied= styleHr= request="), Targets,
// HelperVersion, HelperProcess. "request" echoes the Request value of the settings the pass applied, so the app can tell
// a report about its own change from one written by an earlier pass.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ocidl.h>
#include <oleauto.h>
#include <roapi.h>
#include <winstring.h>
#include <xamlom.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

namespace {

constexpr DWORD HelperVersion = 2;

// {053DF8EC-D4D0-4868-BC22-F1EEFC050065}
constexpr CLSID ClsidTap = {0x053df8ec, 0xd4d0, 0x4868, {0xbc, 0x22, 0xf1, 0xee, 0xfc, 0x05, 0x00, 0x65}};

// Windows.UI.Xaml interfaces used directly (IIDs and method order from Windows.UI.Xaml.winmd).
constexpr IID IidXamlReaderStatics = {0x9891c6bd, 0x534f, 0x4955, {0xb8, 0x5a, 0x8a, 0x8d, 0xc0, 0xdc, 0xa6, 0x02}};
constexpr IID IidSetter = {0xa73ded29, 0xb4ae, 0x4a81, {0xbe, 0x85, 0xe6, 0x90, 0xba, 0x0d, 0x3b, 0x6e}};
constexpr IID IidDependencyObject = {0x5c526665, 0xf60e, 0x4912, {0xaf, 0x59, 0x5f, 0xe0, 0x68, 0x0f, 0x08, 0x9d}};
constexpr IID IidDependencyPropertyStatics = {0x49e5f28f, 0x8259, 0x4d5c, {0xaa, 0xe0, 0x83, 0xd5, 0x6d, 0xbb, 0x68, 0xd9}};
constexpr IID IidBindingExpression = {0x516a19a5, 0xc2fd, 0x4a9e, {0x9f, 0xd3, 0x9a, 0xa4, 0x2f, 0x99, 0x5a, 0x3c}};
constexpr IID IidBindingBase = {0x1589a2ab, 0x3d15, 0x49bc, {0xa4, 0x47, 0x8a, 0x54, 0x48, 0xe5, 0x88, 0x70}};
constexpr IID IidBindingOperationsStatics = {0xe155ef73, 0x95a0, 0x4aab, {0x8c, 0x7d, 0x2a, 0x47, 0xda, 0x07, 0x3c, 0x79}};

struct IXamlReaderStatics : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE Load(HSTRING xaml, IInspectable **result) = 0;
};
struct ISetter : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_Property(IInspectable **property) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_Property(IInspectable *property) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_Value(IInspectable **value) = 0;
};
struct IDependencyObject : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE GetValue(IInspectable *property, IInspectable **value) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetValue(IInspectable *property, IInspectable *value) = 0;
    virtual HRESULT STDMETHODCALLTYPE ClearValue(IInspectable *property) = 0;
    virtual HRESULT STDMETHODCALLTYPE ReadLocalValue(IInspectable *property, IInspectable **value) = 0;
};
struct IDependencyPropertyStatics : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_UnsetValue(IInspectable **value) = 0;
};
struct IBindingExpression : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE get_DataItem(IInspectable **value) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_ParentBinding(IInspectable **binding) = 0;
};
struct IBindingOperationsStatics : IInspectable
{
    virtual HRESULT STDMETHODCALLTYPE SetBinding(IInspectable *target, IInspectable *property, IInspectable *binding) = 0;
};

enum Look : DWORD { LookDefault = 0, LookClear = 1, LookOpaque = 2, LookAcrylic = 3, LookBlur = 4 };

constexpr UINT ApplyMessage = WM_APP + 0x51, RestoreStylesMessage = WM_APP + 0x52;
constexpr wchar_t WindowClass[] = L"SoltexTaskbarHelper2";
constexpr wchar_t Presentation[] = L"xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

struct Settings
{
    DWORD look = LookDefault;
    DWORD color = 0;  // 0xAARRGGBB
    bool line = true; // show the thin line above the taskbar
};

struct Config
{
    wchar_t key[200] = L"Software\\Soltex\\Taskbar";
    wchar_t event[120] = L"Local\\SoltexTaskbar2";
    wchar_t log[MAX_PATH] = L"";
    wchar_t dump[MAX_PATH] = L""; // write the visual tree seen at attach time here (types and names only)
    bool verbose = false;
};

// One style rule. Empty under/type/name match anything, but a rule needs a type or a name.
struct Rule
{
    wchar_t under[128];
    wchar_t type[128];
    wchar_t name[96];
    wchar_t property[96];
    wchar_t value[192];
    int anchor; // index into g_anchors for "under", or -1
};

// An element worth remembering: inside an anchor type (bit mask) and/or within reach of Taskbar.TaskbarBackground.
struct Node
{
    InstanceHandle handle;
    unsigned mask;
    short background; // depth below Taskbar.TaskbarBackground, or -1
    bool used, deleted;
};

// A background rectangle restyled by the taskbar look.
struct Target
{
    InstanceHandle handle;
    DWORD thread; // the XAML thread that owns it
    bool stroke;
    bool inspected;
    bool modified;
    bool originalHandle; // the original Fill is an object we can put back
    bool originalLocal;  // the original Fill was set directly on the element
    unsigned fillIndex;
    IInspectable *original; // held, so XAML cannot release the original brush while ours is shown
    HRESULT result;         // last pass
};

// One element a style rule applies to.
struct Styled
{
    InstanceHandle handle;
    DWORD thread;
    int rule;
    unsigned generation;
    unsigned pass; // last apply pass that tried it
    bool applied;
    IInspectable *property;  // the DependencyProperty (held)
    IInspectable *original;  // its previous local value (held; may be the unset marker or an expression)
    IInspectable *effective; // the value it showed before (held): put back when the local value was an expression
    HRESULT result;          // that cannot be set again, such as a template binding
};

struct Dispatcher
{
    DWORD thread;
    HWND window;
};

constexpr int NodeCapacity = 16384, MaxTargets = 32, MaxDepth = 4, MaxDispatchers = 8, MaxRules = 48, MaxAnchors = 16, MaxStyled = 1024;

Config g_config;
HMODULE g_module;
SRWLOCK g_lock = SRWLOCK_INIT; // guards everything below except the dump buffer
Node g_nodes[NodeCapacity];
int g_nodeCount;
Target g_targets[MaxTargets];
int g_targetCount;
Styled g_styled[MaxStyled];
int g_styledCount;
Rule g_rules[MaxRules];
int g_ruleCount;
wchar_t g_anchors[MaxAnchors][128];
int g_anchorCount;
unsigned g_generation; // bumped whenever the rules are replaced
volatile LONG g_request; // Request value whose rules are in place (set by the worker)
Dispatcher g_dispatchers[MaxDispatchers];
int g_dispatcherCount;
wchar_t g_rulesText[MaxRules * 600]; // the Styles value last applied
IXamlDiagnostics *g_diagnostics;
IVisualTreeService *g_tree;
IVisualTreeService3 *g_advise; // worker thread only
HANDLE g_settingsEvent, g_wake;
volatile LONG g_started;
char *g_dumpBuffer; // UTF-8 lines while the first tree walk runs
size_t g_dumpLength, g_dumpCapacity;

bool Same(const wchar_t *a, const wchar_t *b) { return a && b && wcscmp(a, b) == 0; }

void Log(const wchar_t *format, ...)
{
    if (!g_config.log[0]) return;
    wchar_t text[768];
    SYSTEMTIME now;
    GetLocalTime(&now);
    int prefix = _snwprintf(text, 64, L"%04u-%02u-%02u %02u:%02u:%02u.%03u [%lu] ", now.wYear, now.wMonth, now.wDay,
                            now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentThreadId());
    if (prefix < 0) prefix = 0;
    va_list args;
    va_start(args, format);
    int body = _vsnwprintf(text + prefix, 760 - prefix, format, args);
    va_end(args);
    int length = body < 0 ? 760 : prefix + body;
    text[length++] = L'\r';
    text[length++] = L'\n';
    char utf8[2400];
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text, length, utf8, sizeof utf8, nullptr, nullptr);
    HANDLE file = CreateFileW(g_config.log, FILE_APPEND_DATA | FILE_WRITE_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                              OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) && size.QuadPart > 256 * 1024)
    {
        SetFilePointer(file, 0, nullptr, FILE_BEGIN);
        SetEndOfFile(file);
    }
    SetFilePointer(file, 0, nullptr, FILE_END);
    DWORD written;
    WriteFile(file, utf8, static_cast<DWORD>(bytes), &written, nullptr);
    CloseHandle(file);
}

// "key=...|event=...|log=...|dump=...|verbose=1"
void ParseConfig(const wchar_t *data)
{
    if (!data) return;
    while (*data)
    {
        const wchar_t *end = wcschr(data, L'|');
        size_t length = end ? static_cast<size_t>(end - data) : wcslen(data);
        const wchar_t *equals = static_cast<const wchar_t *>(wmemchr(data, L'=', length));
        if (equals)
        {
            size_t name = static_cast<size_t>(equals - data), valueLength = length - name - 1;
            auto copy = [&](wchar_t *target, size_t capacity) {
                if (valueLength == 0 || valueLength >= capacity) return;
                wmemcpy(target, equals + 1, valueLength);
                target[valueLength] = 0;
            };
            if (name == 3 && wcsncmp(data, L"key", 3) == 0) copy(g_config.key, ARRAYSIZE(g_config.key));
            else if (name == 5 && wcsncmp(data, L"event", 5) == 0) copy(g_config.event, ARRAYSIZE(g_config.event));
            else if (name == 3 && wcsncmp(data, L"log", 3) == 0) copy(g_config.log, ARRAYSIZE(g_config.log));
            else if (name == 4 && wcsncmp(data, L"dump", 4) == 0) copy(g_config.dump, ARRAYSIZE(g_config.dump));
            else if (name == 7 && wcsncmp(data, L"verbose", 7) == 0) g_config.verbose = valueLength == 1 && equals[1] == L'1';
        }
        if (!end) break;
        data = end + 1;
    }
}

Settings ReadSettings()
{
    Settings settings;
    DWORD value, size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, g_config.key, L"Look", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS && value <= LookBlur)
        settings.look = value;
    size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, g_config.key, L"Color", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        settings.color = value;
    size = sizeof value;
    if (RegGetValueW(HKEY_CURRENT_USER, g_config.key, L"Line", RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS)
        settings.line = value != 0;
    return settings;
}

// Reads the Styles value as a double-NUL-terminated list into text (empty list on failure).
void ReadRulesText(wchar_t *text, DWORD capacityBytes)
{
    DWORD size = capacityBytes - 2 * sizeof(wchar_t);
    text[0] = text[1] = 0;
    if (RegGetValueW(HKEY_CURRENT_USER, g_config.key, L"Styles", RRF_RT_REG_MULTI_SZ, nullptr, text, &size) != ERROR_SUCCESS)
        text[0] = text[1] = 0;
}

// "under|type|name|Owner.Property|value" per line.
int ParseRules(const wchar_t *text, Rule *rules, wchar_t (*anchors)[128], int *anchorCount)
{
    int count = 0;
    *anchorCount = 0;
    for (const wchar_t *line = text; *line && count < MaxRules; line += wcslen(line) + 1)
    {
        Rule rule{};
        wchar_t *fields[5] = {rule.under, rule.type, rule.name, rule.property, rule.value};
        size_t capacities[5] = {ARRAYSIZE(rule.under), ARRAYSIZE(rule.type), ARRAYSIZE(rule.name), ARRAYSIZE(rule.property), ARRAYSIZE(rule.value)};
        const wchar_t *field = line;
        bool valid = true;
        for (int i = 0; i < 5; i++)
        {
            const wchar_t *end = i < 4 ? wcschr(field, L'|') : field + wcslen(field);
            if (!end || static_cast<size_t>(end - field) >= capacities[i]) { valid = false; break; }
            wmemcpy(fields[i], field, static_cast<size_t>(end - field));
            fields[i][end - field] = 0;
            field = end + 1;
        }
        if (!valid || (!rule.type[0] && !rule.name[0]) || !rule.property[0]) { Log(L"ignored style rule: %ls", line); continue; }
        rule.anchor = -1;
        if (rule.under[0])
        {
            for (int a = 0; a < *anchorCount; a++)
                if (Same(anchors[a], rule.under)) rule.anchor = a;
            if (rule.anchor < 0 && *anchorCount < MaxAnchors)
            {
                wcscpy(anchors[*anchorCount], rule.under);
                rule.anchor = (*anchorCount)++;
            }
            if (rule.anchor < 0) { Log(L"too many anchors; ignored: %ls", line); continue; }
        }
        rules[count++] = rule;
    }
    return count;
}

DWORD ReadRequest()
{
    DWORD value = 0, size = sizeof value;
    RegGetValueW(HKEY_CURRENT_USER, g_config.key, L"Request", RRF_RT_REG_DWORD, nullptr, &value, &size);
    return value;
}

void WriteStatus(DWORD look, DWORD request)
{
    int targets = 0, applied = 0, styled = 0, styleApplied = 0;
    HRESULT result = S_OK, styleResult = S_OK;
    AcquireSRWLockShared(&g_lock);
    targets = g_targetCount;
    for (int i = 0; i < g_targetCount; i++)
    {
        if (SUCCEEDED(g_targets[i].result)) applied++;
        else if (SUCCEEDED(result)) result = g_targets[i].result;
    }
    styled = g_styledCount;
    for (int i = 0; i < g_styledCount; i++)
    {
        if (g_styled[i].applied) styleApplied++;
        else if (FAILED(g_styled[i].result) && SUCCEEDED(styleResult)) styleResult = g_styled[i].result;
    }
    ReleaseSRWLockShared(&g_lock);

    HKEY key;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, g_config.key, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return;
    wchar_t status[224];
    _snwprintf(status, ARRAYSIZE(status), L"targets=%d applied=%d look=%lu hr=0x%08lX styled=%d styleApplied=%d styleHr=0x%08lX request=%lu", targets,
               applied, look, static_cast<unsigned long>(result), styled, styleApplied, static_cast<unsigned long>(styleResult), request);
    status[ARRAYSIZE(status) - 1] = 0;
    DWORD version = HelperVersion, process = GetCurrentProcessId(), count = static_cast<DWORD>(targets);
    RegSetValueExW(key, L"Targets", 0, REG_DWORD, reinterpret_cast<const BYTE *>(&count), sizeof count);
    RegSetValueExW(key, L"HelperVersion", 0, REG_DWORD, reinterpret_cast<const BYTE *>(&version), sizeof version);
    RegSetValueExW(key, L"HelperProcess", 0, REG_DWORD, reinterpret_cast<const BYTE *>(&process), sizeof process);
    // Status last: the app waits for it.
    RegSetValueExW(key, L"Status", 0, REG_SZ, reinterpret_cast<const BYTE *>(status), static_cast<DWORD>((wcslen(status) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
}

void ReportFailure(HRESULT hr)
{
    HKEY key;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, g_config.key, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return;
    wchar_t status[64];
    _snwprintf(status, ARRAYSIZE(status), L"targets=0 applied=0 look=0 hr=0x%08lX", static_cast<unsigned long>(hr));
    status[ARRAYSIZE(status) - 1] = 0;
    RegSetValueExW(key, L"Status", 0, REG_SZ, reinterpret_cast<const BYTE *>(status), static_cast<DWORD>((wcslen(status) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
}

// ---- visual tree bookkeeping (XAML threads, under g_lock) ----

size_t Slot(InstanceHandle handle) { return static_cast<size_t>(((handle >> 3) * 0x9E3779B97F4A7C15ull) >> 40) & (NodeCapacity - 1); }

Node *FindNode(InstanceHandle handle)
{
    for (size_t i = 0, slot = Slot(handle); i < NodeCapacity; i++, slot = (slot + 1) & (NodeCapacity - 1))
    {
        Node &node = g_nodes[slot];
        if (!node.used && !node.deleted) return nullptr;
        if (node.used && node.handle == handle) return &node;
    }
    return nullptr;
}

void InsertNode(InstanceHandle handle, unsigned mask, short background)
{
    if (g_nodeCount >= NodeCapacity * 3 / 4) return; // full enough: deeper elements are simply not tracked
    for (size_t i = 0, slot = Slot(handle); i < NodeCapacity; i++, slot = (slot + 1) & (NodeCapacity - 1))
    {
        Node &node = g_nodes[slot];
        if (node.used) continue;
        node = {handle, mask, background, true, false};
        g_nodeCount++;
        return;
    }
}

void RemoveNode(InstanceHandle handle)
{
    if (Node *node = FindNode(handle))
    {
        node->used = false;
        node->deleted = true;
        g_nodeCount--;
    }
}

int FindTarget(InstanceHandle handle)
{
    for (int i = 0; i < g_targetCount; i++)
        if (g_targets[i].handle == handle) return i;
    return -1;
}

int FindStyled(InstanceHandle handle, int rule)
{
    for (int i = 0; i < g_styledCount; i++)
        if (g_styled[i].handle == handle && g_styled[i].rule == rule) return i;
    return -1;
}

bool Matches(const Rule &rule, const wchar_t *type, const wchar_t *name, unsigned mask)
{
    return (!rule.type[0] || Same(type, rule.type)) && (!rule.name[0] || Same(name, rule.name)) &&
           (rule.anchor < 0 || (mask & (1u << rule.anchor)));
}

void AppendDump(const ParentChildRelation &relation, const VisualElement &element, VisualMutationType mutation)
{
    wchar_t line[400];
    int length = _snwprintf(line, ARRAYSIZE(line), L"%llu\t%llu\t%d\t%ls\t%ls\n", static_cast<unsigned long long>(relation.Parent),
                            static_cast<unsigned long long>(element.Handle), static_cast<int>(mutation), element.Type ? element.Type : L"",
                            element.Name ? element.Name : L"");
    if (length < 0) return;
    char utf8[1200];
    int bytes = WideCharToMultiByte(CP_UTF8, 0, line, length, utf8, sizeof utf8, nullptr, nullptr);
    if (bytes <= 0) return;
    if (g_dumpLength + static_cast<size_t>(bytes) > g_dumpCapacity)
    {
        size_t capacity = g_dumpCapacity ? g_dumpCapacity * 2 : 1 << 20;
        if (capacity > (64u << 20)) return; // enough for any taskbar
        void *grown = g_dumpBuffer ? HeapReAlloc(GetProcessHeap(), 0, g_dumpBuffer, capacity) : HeapAlloc(GetProcessHeap(), 0, capacity);
        if (!grown) return;
        g_dumpBuffer = static_cast<char *>(grown);
        g_dumpCapacity = capacity;
    }
    memcpy(g_dumpBuffer + g_dumpLength, utf8, static_cast<size_t>(bytes));
    g_dumpLength += static_cast<size_t>(bytes);
}

void ApplyOnThisThread();
void RestoreStylesOnThisThread();

LRESULT CALLBACK DispatchProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == ApplyMessage)
    {
        MSG pending; // several signals may queue up; one pass covers them all
        while (PeekMessageW(&pending, window, ApplyMessage, ApplyMessage, PM_REMOVE)) {}
        ApplyOnThisThread();
        return 0;
    }
    if (message == RestoreStylesMessage)
    {
        RestoreStylesOnThisThread();
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

// The message-only window for the calling XAML thread (created on first use).
void EnsureDispatcher()
{
    DWORD thread = GetCurrentThreadId();
    AcquireSRWLockShared(&g_lock);
    bool known = false;
    for (int i = 0; i < g_dispatcherCount; i++) known |= g_dispatchers[i].thread == thread;
    bool full = g_dispatcherCount == MaxDispatchers;
    ReleaseSRWLockShared(&g_lock);
    if (known || full) return;

    static volatile LONG registered;
    if (!InterlockedExchange(&registered, 1))
    {
        WNDCLASSW windowClass{};
        windowClass.lpfnWndProc = DispatchProc;
        windowClass.hInstance = g_module;
        windowClass.lpszClassName = WindowClass;
        RegisterClassW(&windowClass);
    }
    HWND window = CreateWindowExW(0, WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
    if (!window)
    {
        Log(L"dispatcher window failed: %lu", GetLastError());
        return;
    }
    AcquireSRWLockExclusive(&g_lock);
    if (g_dispatcherCount < MaxDispatchers) g_dispatchers[g_dispatcherCount++] = {thread, window};
    ReleaseSRWLockExclusive(&g_lock);
    Log(L"XAML thread %lu ready", thread);
}

class TreeWatcher final : public IVisualTreeServiceCallback2
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IVisualTreeServiceCallback)) ||
            IsEqualIID(riid, __uuidof(IVisualTreeServiceCallback2)))
        {
            *out = static_cast<IVisualTreeServiceCallback2 *>(this);
            return S_OK; // static object: no reference counting needed
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element, VisualMutationType mutation) override
    {
        bool wake = false, matched = false;
        DWORD thread = GetCurrentThreadId();
        AcquireSRWLockExclusive(&g_lock);
        if (g_dumpBuffer || g_config.dump[0]) AppendDump(relation, element, mutation);
        if (mutation == Add)
        {
            const Node *parent = FindNode(relation.Parent);
            unsigned mask = parent ? parent->mask : 0;
            short background = parent && parent->background >= 0 && parent->background < MaxDepth ? static_cast<short>(parent->background + 1) : -1;
            if (Same(element.Type, L"Taskbar.TaskbarBackground") || Same(element.Name, L"TaskbarBackground")) background = 0;
            for (int a = 0; a < g_anchorCount; a++)
                if (Same(element.Type, g_anchors[a])) mask |= 1u << a;
            if ((mask || background >= 0) && !FindNode(element.Handle)) InsertNode(element.Handle, mask, background);

            bool stroke = Same(element.Name, L"BackgroundStroke");
            if (background > 0 && (stroke || Same(element.Name, L"BackgroundFill")) && Same(element.Type, L"Windows.UI.Xaml.Shapes.Rectangle") &&
                FindTarget(element.Handle) < 0 && g_targetCount < MaxTargets)
            {
                g_targets[g_targetCount++] = {element.Handle, thread, stroke, false, false, false, false, 0, nullptr, S_OK};
                wake = matched = true;
            }
            for (int r = 0; r < g_ruleCount; r++)
            {
                if (!Matches(g_rules[r], element.Type, element.Name, mask) || FindStyled(element.Handle, r) >= 0 || g_styledCount >= MaxStyled) continue;
                g_styled[g_styledCount++] = {element.Handle, thread, r, g_generation, 0, false, nullptr, nullptr, nullptr, S_OK};
                wake = matched = true;
            }
        }
        else if (mutation == Remove)
        {
            RemoveNode(element.Handle);
            int target = FindTarget(element.Handle);
            if (target >= 0)
            {
                if (g_targets[target].original) g_targets[target].original->Release(); // removals arrive on the owning thread
                g_targets[target] = g_targets[--g_targetCount];
            }
            for (int i = g_styledCount - 1; i >= 0; i--)
            {
                if (g_styled[i].handle != element.Handle) continue;
                if (g_styled[i].property) g_styled[i].property->Release();
                if (g_styled[i].original) g_styled[i].original->Release();
                if (g_styled[i].effective) g_styled[i].effective->Release();
                g_styled[i] = g_styled[--g_styledCount];
            }
        }
        ReleaseSRWLockExclusive(&g_lock);
        if (matched) EnsureDispatcher();
        if (wake) SetEvent(g_wake);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override { return S_OK; }
};

TreeWatcher g_watcher;

// ---- XAML work (XAML threads only) ----

void FreeChain(unsigned sourceCount, PropertyChainSource *sources, unsigned valueCount, PropertyChainValue *values)
{
    for (unsigned i = 0; i < sourceCount; i++)
    {
        SysFreeString(sources[i].TargetType);
        SysFreeString(sources[i].Name);
        SysFreeString(sources[i].SrcInfo.FileName);
        SysFreeString(sources[i].SrcInfo.Hash);
    }
    for (unsigned i = 0; i < valueCount; i++)
    {
        SysFreeString(values[i].Type);
        SysFreeString(values[i].DeclaringType);
        SysFreeString(values[i].ValueType);
        SysFreeString(values[i].ItemType);
        SysFreeString(values[i].Value);
        SysFreeString(values[i].PropertyName);
    }
    CoTaskMemFree(sources);
    CoTaskMemFree(values);
}

void ColorText(DWORD color, wchar_t (&text)[16])
{
    _snwprintf(text, 16, L"#%08lX", static_cast<unsigned long>(color));
    text[15] = 0;
}

HRESULT LoadXaml(const wchar_t *markup, IInspectable **result)
{
    *result = nullptr;
    HSTRING className = nullptr, text = nullptr;
    IXamlReaderStatics *reader = nullptr;
    HRESULT hr = WindowsCreateString(L"Windows.UI.Xaml.Markup.XamlReader", 33, &className);
    if (SUCCEEDED(hr)) hr = RoGetActivationFactory(className, IidXamlReaderStatics, reinterpret_cast<void **>(&reader));
    if (SUCCEEDED(hr)) hr = WindowsCreateString(markup, static_cast<UINT32>(wcslen(markup)), &text);
    if (SUCCEEDED(hr)) hr = reader->Load(text, result);
    if (reader) reader->Release();
    WindowsDeleteString(text);
    WindowsDeleteString(className);
    if (g_config.verbose || FAILED(hr)) Log(L"xaml %ls hr=0x%08lX", markup, static_cast<unsigned long>(hr));
    return hr;
}

// Windows' own solid taskbar colour for the current system theme (Settings > Personalization > Colors, "Windows mode").
DWORD ThemeTaskbarColor()
{
    DWORD light = 0, size = sizeof light;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"SystemUsesLightTheme",
                     RRF_RT_REG_DWORD, nullptr, &light, &size) != ERROR_SUCCESS)
        light = 0;
    return light ? 0xFFF3F3F3u : 0xFF202020u;
}

// Builds the brush for a look from XAML markup. A new brush per use: XAML releases a brush (and invalidates its
// diagnostics handle) as soon as nothing shows it, so brushes are never cached.
HRESULT MakeBrush(DWORD look, DWORD color, IInspectable **brush)
{
    wchar_t markup[400], tint[16], fallback[16], opacity[16];
    if (look == LookClear || look == LookOpaque)
    {
        ColorText(look == LookOpaque ? color | 0xFF000000u : color, tint);
        _snwprintf(markup, ARRAYSIZE(markup), L"<SolidColorBrush %ls Color='%ls' />", Presentation, tint);
    }
    else
    {
        // Acrylic: the colour's alpha is the tint strength. Blur: no tint and no luminosity layer.
        DWORD permille = look == LookBlur ? 0 : ((color >> 24) * 1000 + 127) / 255;
        ColorText(color | 0xFF000000u, tint);
        // FallbackColor is what XAML draws when acrylic cannot render (Transparency effects off, Energy Saver). Acrylic
        // falls back to its visible tint colour. Blur has no colour of its own (the colour row is hidden for it), so it
        // falls back to Windows' plain taskbar colour for the current theme rather than a colour the user cannot see.
        ColorText(look == LookBlur ? ThemeTaskbarColor() : color | 0xFF000000u, fallback);
        _snwprintf(opacity, ARRAYSIZE(opacity), L"%lu.%03lu", permille / 1000, permille % 1000);
        opacity[ARRAYSIZE(opacity) - 1] = 0;
        _snwprintf(markup, ARRAYSIZE(markup),
                   L"<AcrylicBrush %ls BackgroundSource='HostBackdrop' TintColor='%ls' TintOpacity='%ls' FallbackColor='%ls'%ls />", Presentation,
                   tint, opacity, fallback, look == LookBlur ? L" TintLuminosityOpacity='0'" : L"");
    }
    markup[ARRAYSIZE(markup) - 1] = 0;
    return LoadXaml(markup, brush);
}

// Shows a new brush for the look on the target.
HRESULT ShowBrush(const Target &target, DWORD look, DWORD color)
{
    IInspectable *brush = nullptr;
    HRESULT hr = MakeBrush(look, color, &brush);
    if (FAILED(hr)) return hr;
    InstanceHandle handle = 0;
    hr = g_diagnostics->GetHandleFromIInspectable(brush, &handle);
    if (FAILED(hr)) hr = g_diagnostics->RegisterInstance(brush, &handle);
    if (SUCCEEDED(hr)) hr = g_tree->SetProperty(target.handle, handle, target.fillIndex);
    brush->Release(); // the rectangle holds it now
    return hr;
}

HRESULT Inspect(Target &target)
{
    unsigned sourceCount = 0, valueCount = 0;
    PropertyChainSource *sources = nullptr;
    PropertyChainValue *values = nullptr;
    HRESULT hr = g_tree->GetPropertyValuesChain(target.handle, &sourceCount, &sources, &valueCount, &values);
    if (FAILED(hr)) return hr;
    hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    for (unsigned i = 0; i < valueCount; i++)
    {
        const PropertyChainValue &value = values[i];
        if (!Same(value.PropertyName, L"Fill") || value.Overridden) continue;
        target.fillIndex = value.Index;
        target.originalHandle = (value.MetadataBits & IsValueHandle) && value.Value && value.Value[0];
        if (target.originalHandle && FAILED(g_diagnostics->GetIInspectableFromHandle(_wcstoui64(value.Value, nullptr, 10), &target.original)))
            target.original = nullptr;
        target.originalLocal = value.PropertyChainIndex < sourceCount && sources[value.PropertyChainIndex].Source == BaseValueSourceLocal;
        Log(L"%ls Fill: index=%u type=%ls local=%d", target.stroke ? L"stroke" : L"fill", value.Index, value.ValueType ? value.ValueType : L"?",
            target.originalLocal ? 1 : 0);
        hr = S_OK;
        break;
    }
    FreeChain(sourceCount, sources, valueCount, values);
    return hr;
}

HRESULT Restore(const Target &target)
{
    if (target.originalLocal && target.original)
    {
        InstanceHandle original = 0;
        HRESULT hr = g_diagnostics->GetHandleFromIInspectable(target.original, &original);
        return SUCCEEDED(hr) ? g_tree->SetProperty(target.handle, original, target.fillIndex) : hr;
    }
    return g_tree->ClearProperty(target.handle, target.fillIndex);
}

// XML-escapes a rule value or property name into a markup attribute.
void Escape(const wchar_t *text, wchar_t *out, size_t capacity)
{
    size_t length = 0;
    for (; *text && length + 7 < capacity; text++)
    {
        const wchar_t *entity = *text == L'&' ? L"&amp;" : *text == L'<' ? L"&lt;" : *text == L'>' ? L"&gt;" : *text == L'\'' ? L"&apos;" : *text == L'"' ? L"&quot;" : nullptr;
        if (entity)
        {
            size_t n = wcslen(entity);
            wmemcpy(out + length, entity, n);
            length += n;
        }
        else out[length++] = *text;
    }
    out[length] = 0;
}

// Parses the rule's property and value through a XAML Setter, so the value is converted to the property's own type.
HRESULT LoadSetter(const Rule &rule, IInspectable **property, IInspectable **value)
{
    *property = *value = nullptr;
    wchar_t name[200], text[400], markup[800];
    Escape(rule.property, name, ARRAYSIZE(name));
    Escape(rule.value, text, ARRAYSIZE(text));
    _snwprintf(markup, ARRAYSIZE(markup), L"<Setter %ls Property='%ls' Value='%ls' />", Presentation, name, text);
    markup[ARRAYSIZE(markup) - 1] = 0;
    IInspectable *object = nullptr;
    HRESULT hr = LoadXaml(markup, &object);
    if (FAILED(hr)) return hr;
    ISetter *setter = nullptr;
    hr = object->QueryInterface(IidSetter, reinterpret_cast<void **>(&setter));
    object->Release();
    if (FAILED(hr)) return hr;
    hr = setter->get_Property(property);
    if (SUCCEEDED(hr)) hr = setter->get_Value(value);
    setter->Release();
    if (FAILED(hr) && *property)
    {
        (*property)->Release();
        *property = nullptr;
    }
    return hr;
}

IInspectable *UnsetValue()
{
    static IInspectable *unset; // per process; the marker is a global singleton
    if (unset) return unset;
    HSTRING className = nullptr;
    IDependencyPropertyStatics *statics = nullptr;
    if (SUCCEEDED(WindowsCreateString(L"Windows.UI.Xaml.DependencyProperty", 34, &className)) &&
        SUCCEEDED(RoGetActivationFactory(className, IidDependencyPropertyStatics, reinterpret_cast<void **>(&statics))))
    {
        statics->get_UnsetValue(&unset);
        statics->Release();
    }
    WindowsDeleteString(className);
    return unset;
}

bool SameObject(IInspectable *a, IInspectable *b)
{
    if (!a || !b) return a == b;
    IUnknown *x = nullptr, *y = nullptr;
    a->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void **>(&x));
    b->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void **>(&y));
    bool same = x && x == y;
    if (x) x->Release();
    if (y) y->Release();
    return same;
}

HRESULT ElementOf(InstanceHandle handle, IDependencyObject **element)
{
    *element = nullptr;
    IInspectable *object = nullptr;
    HRESULT hr = g_diagnostics->GetIInspectableFromHandle(handle, &object);
    if (FAILED(hr)) return hr;
    hr = object->QueryInterface(IidDependencyObject, reinterpret_cast<void **>(element));
    object->Release();
    return hr;
}

HRESULT ApplyStyle(Styled &styled, const Rule &rule)
{
    IDependencyObject *element = nullptr;
    HRESULT hr = ElementOf(styled.handle, &element);
    if (FAILED(hr)) return hr;
    IInspectable *property = nullptr, *value = nullptr;
    hr = LoadSetter(rule, &property, &value);
    if (SUCCEEDED(hr))
    {
        IInspectable *original = nullptr, *effective = nullptr;
        hr = element->ReadLocalValue(property, &original);
        if (SUCCEEDED(hr)) hr = element->GetValue(property, &effective);
        if (SUCCEEDED(hr)) hr = element->SetValue(property, value);
        if (SUCCEEDED(hr))
        {
            styled.property = property;
            styled.original = original;
            styled.effective = effective;
            styled.applied = true;
            property = original = effective = nullptr;
        }
        if (original) original->Release();
        if (effective) effective->Release();
        if (property) property->Release();
        if (value) value->Release();
    }
    element->Release();
    if (g_config.verbose || FAILED(hr))
        Log(L"style %ls#%ls %ls=%ls hr=0x%08lX", rule.type, rule.name, rule.property, rule.value, static_cast<unsigned long>(hr));
    return hr;
}

// Re-attaches the binding a BindingExpression came from. Returns E_NOINTERFACE when the value is not a binding.
HRESULT Rebind(IDependencyObject *element, IInspectable *property, IInspectable *original)
{
    IBindingExpression *expression = nullptr;
    HRESULT hr = original->QueryInterface(IidBindingExpression, reinterpret_cast<void **>(&expression));
    if (FAILED(hr)) return hr;
    IInspectable *binding = nullptr, *bindingBase = nullptr;
    IBindingOperationsStatics *operations = nullptr;
    HSTRING className = nullptr;
    hr = expression->get_ParentBinding(&binding);
    if (SUCCEEDED(hr)) hr = binding->QueryInterface(IidBindingBase, reinterpret_cast<void **>(&bindingBase));
    if (SUCCEEDED(hr)) hr = WindowsCreateString(L"Windows.UI.Xaml.Data.BindingOperations", 38, &className);
    if (SUCCEEDED(hr)) hr = RoGetActivationFactory(className, IidBindingOperationsStatics, reinterpret_cast<void **>(&operations));
    if (SUCCEEDED(hr)) hr = operations->SetBinding(element, property, bindingBase);
    if (operations) operations->Release();
    if (bindingBase) bindingBase->Release();
    if (binding) binding->Release();
    WindowsDeleteString(className);
    expression->Release();
    return hr;
}

// Puts the element back: no local value → clear ours; a binding → re-attach it; a plain value → set it again; anything
// else (e.g. a template binding, which has no public way back) → the exact value the element showed before.
void RestoreStyle(Styled &styled)
{
    if (!styled.applied) return;
    IDependencyObject *element = nullptr;
    if (SUCCEEDED(ElementOf(styled.handle, &element)))
    {
        HRESULT hr;
        if (!styled.original || SameObject(styled.original, UnsetValue())) hr = element->ClearValue(styled.property);
        else if ((hr = Rebind(element, styled.property, styled.original)) == E_NOINTERFACE)
        {
            hr = element->SetValue(styled.property, styled.original);
            if (FAILED(hr) && styled.effective)
            {
                HSTRING name = nullptr;
                styled.original->GetRuntimeClassName(&name);
                hr = element->SetValue(styled.property, styled.effective);
                Log(L"restored the shown value (local value was %ls) hr=0x%08lX", name ? WindowsGetStringRawBuffer(name, nullptr) : L"?",
                    static_cast<unsigned long>(hr));
                WindowsDeleteString(name);
            }
        }
        if (FAILED(hr)) Log(L"restore style hr=0x%08lX", static_cast<unsigned long>(hr));
        element->Release();
    }
    if (styled.property) styled.property->Release();
    if (styled.original) styled.original->Release();
    if (styled.effective) styled.effective->Release();
    styled.property = styled.original = styled.effective = nullptr;
    styled.applied = false;
}

// Puts back every style on this thread (the rules are about to change). XAML calls never run under the lock: XAML may
// report tree changes synchronously, and the watcher takes the lock.
void RestoreStylesOnThisThread()
{
    DWORD thread = GetCurrentThreadId();
    Styled work[256];
    for (;;)
    {
        int count = 0;
        AcquireSRWLockExclusive(&g_lock);
        for (int i = 0; i < g_styledCount && count < 256; i++)
        {
            Styled &styled = g_styled[i];
            if (styled.thread != thread || !styled.applied) continue;
            work[count++] = styled; // ownership of the held values moves to work
            styled.applied = false;
            styled.property = styled.original = styled.effective = nullptr;
        }
        ReleaseSRWLockExclusive(&g_lock);
        for (int i = 0; i < count; i++) RestoreStyle(work[i]);
        if (count < 256) return;
    }
}

void ApplyTargets(DWORD thread, const Settings &settings)
{
    Target work[MaxTargets];
    int count = 0;
    AcquireSRWLockShared(&g_lock);
    for (int i = 0; i < g_targetCount; i++)
        if (g_targets[i].thread == thread) work[count++] = g_targets[i];
    ReleaseSRWLockShared(&g_lock);

    for (int i = 0; i < count; i++)
    {
        Target &target = work[i];
        HRESULT hr = S_OK;
        if (!target.inspected)
        {
            hr = Inspect(target);
            target.inspected = SUCCEEDED(hr);
        }
        if (SUCCEEDED(hr))
        {
            bool keepWindows = target.stroke ? settings.line : settings.look == LookDefault;
            if (keepWindows)
            {
                if (target.modified) hr = Restore(target);
                if (SUCCEEDED(hr)) target.modified = false;
            }
            else
            {
                hr = target.stroke ? ShowBrush(target, LookClear, 0) : ShowBrush(target, settings.look, settings.color);
                if (SUCCEEDED(hr)) target.modified = true;
            }
        }
        target.result = hr;
        if (g_config.verbose || FAILED(hr))
            Log(L"apply %ls look=%lu color=%08lX line=%d hr=0x%08lX", target.stroke ? L"stroke" : L"fill", settings.look,
                static_cast<unsigned long>(settings.color), settings.line ? 1 : 0, static_cast<unsigned long>(hr));
    }

    AcquireSRWLockExclusive(&g_lock);
    for (int i = 0; i < count; i++)
    {
        int index = FindTarget(work[i].handle);
        if (index >= 0) g_targets[index] = work[i];
    }
    ReleaseSRWLockExclusive(&g_lock);
}

// Applies pending styles on this thread one at a time: copy the entry and its rule under the lock, apply without it,
// then record the result. If the rules were replaced meanwhile, the style just applied is put straight back.
void ApplyStyles(DWORD thread)
{
    static volatile LONG passes;
    unsigned pass = static_cast<unsigned>(InterlockedIncrement(&passes));
    Rule rule;
    for (;;)
    {
        Styled work{};
        bool found = false;
        AcquireSRWLockExclusive(&g_lock);
        for (int i = 0; i < g_styledCount && !found; i++)
        {
            Styled &styled = g_styled[i];
            if (styled.thread != thread || styled.applied || styled.pass == pass || FAILED(styled.result) || // failed once: not retried
                styled.generation != g_generation || styled.rule >= g_ruleCount)
                continue;
            styled.pass = pass;
            work = styled;
            rule = g_rules[styled.rule];
            found = true;
        }
        ReleaseSRWLockExclusive(&g_lock);
        if (!found) return;

        work.result = ApplyStyle(work, rule);
        bool orphan = false;
        AcquireSRWLockExclusive(&g_lock);
        int index = work.generation == g_generation ? FindStyled(work.handle, work.rule) : -1;
        if (index >= 0)
        {
            g_styled[index].applied = work.applied;
            g_styled[index].property = work.property;
            g_styled[index].original = work.original;
            g_styled[index].effective = work.effective;
            g_styled[index].result = work.result;
        }
        else orphan = work.applied;
        ReleaseSRWLockExclusive(&g_lock);
        if (orphan) RestoreStyle(work); // rules replaced or element gone while applying
    }
}

void ApplyOnThisThread()
{
    DWORD thread = GetCurrentThreadId();
    DWORD request = static_cast<DWORD>(g_request); // the rules in place when this pass starts
    Settings settings = ReadSettings();
    ApplyTargets(thread, settings);
    ApplyStyles(thread);
    WriteStatus(settings.look, request);
}

// ---- worker thread ----

void PostToXamlThreads()
{
    HWND windows[MaxDispatchers];
    int count = 0;
    AcquireSRWLockShared(&g_lock);
    for (int i = 0; i < g_dispatcherCount; i++) windows[count++] = g_dispatchers[i].window;
    ReleaseSRWLockShared(&g_lock);
    for (int i = 0; i < count; i++) PostMessageW(windows[i], ApplyMessage, 0, 0);
    if (count == 0) WriteStatus(ReadSettings().look, static_cast<DWORD>(g_request)); // nothing found yet: report zero targets
}

void FlushDump()
{
    AcquireSRWLockExclusive(&g_lock);
    char *buffer = g_dumpBuffer;
    size_t length = g_dumpLength;
    g_dumpBuffer = nullptr;
    g_dumpLength = g_dumpCapacity = 0;
    wchar_t path[MAX_PATH];
    wcscpy(path, g_config.dump);
    g_config.dump[0] = 0; // only the first walk
    ReleaseSRWLockExclusive(&g_lock);
    if (!path[0] || !buffer) return;
    HANDLE file = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written;
        WriteFile(file, buffer, static_cast<DWORD>(length), &written, nullptr);
        CloseHandle(file);
    }
    Log(L"tree written: %ls (%llu bytes)", path, static_cast<unsigned long long>(length));
    HeapFree(GetProcessHeap(), 0, buffer);
}

// The worker's view of the rules changed: put every style back, take the new rules, and walk the tree again so the new
// rules find their elements. text is a whole double-NUL-terminated list, the same size as g_rulesText.
void ReplaceRules(const wchar_t (&text)[ARRAYSIZE(g_rulesText)])
{
    static Rule rules[MaxRules];
    static wchar_t anchors[MaxAnchors][128];
    int anchorCount = 0;
    int count = ParseRules(text, rules, anchors, &anchorCount);

    HWND windows[MaxDispatchers];
    int windowCount = 0;
    AcquireSRWLockShared(&g_lock);
    for (int i = 0; i < g_dispatcherCount; i++) windows[windowCount++] = g_dispatchers[i].window;
    ReleaseSRWLockShared(&g_lock);
    for (int i = 0; i < windowCount; i++)
    {
        DWORD_PTR result;
        SendMessageTimeoutW(windows[i], RestoreStylesMessage, 0, 0, SMTO_BLOCK, 5000, &result);
    }

    AcquireSRWLockExclusive(&g_lock);
    for (int i = 0; i < g_styledCount; i++) // anything a timed-out thread did not restore is dropped, not touched here
    {
        if (g_styled[i].applied) Log(L"a style could not be restored on its thread");
    }
    g_styledCount = 0;
    memcpy(g_rules, rules, sizeof(Rule) * static_cast<size_t>(count));
    g_ruleCount = count;
    memcpy(g_anchors, anchors, sizeof anchors);
    g_anchorCount = anchorCount;
    g_generation++;
    memset(g_nodes, 0, sizeof g_nodes);
    g_nodeCount = 0;
    ReleaseSRWLockExclusive(&g_lock);
    // Copy every line, not just the first: wcsncpy would stop at the NUL after line one, and RulesChanged would then see
    // a change on every later signal.
    memcpy(g_rulesText, text, sizeof g_rulesText);

    HRESULT hr = g_advise->UnadviseVisualTreeChange(&g_watcher);
    if (SUCCEEDED(hr)) hr = g_advise->AdviseVisualTreeChange(&g_watcher);
    Log(L"rules replaced: %d rule(s), walk hr=0x%08lX, %d element(s) matched", count, static_cast<unsigned long>(hr), g_styledCount);
}

bool RulesChanged(const wchar_t (&text)[ARRAYSIZE(g_rulesText)])
{
    const wchar_t *a = text, *b = g_rulesText;
    for (;;) // compare double-NUL-terminated lists
    {
        if (wcscmp(a, b) != 0) return true;
        if (!*a) return false;
        a += wcslen(a) + 1;
        b += wcslen(b) + 1;
    }
}

DWORD WINAPI Worker(void *)
{
    static wchar_t text[ARRAYSIZE(g_rulesText)];
    ReadRulesText(text, sizeof text);
    AcquireSRWLockExclusive(&g_lock);
    g_ruleCount = ParseRules(text, g_rules, g_anchors, &g_anchorCount);
    ReleaseSRWLockExclusive(&g_lock);
    memcpy(g_rulesText, text, sizeof g_rulesText);

    HRESULT hr = g_diagnostics->QueryInterface(__uuidof(IVisualTreeService3), reinterpret_cast<void **>(&g_advise));
    if (SUCCEEDED(hr)) hr = g_advise->AdviseVisualTreeChange(&g_watcher);
    Log(L"advise hr=0x%08lX targets=%d styled=%d rules=%d", static_cast<unsigned long>(hr), g_targetCount, g_styledCount, g_ruleCount);
    FlushDump();
    if (FAILED(hr))
    {
        ReportFailure(hr);
        return 0;
    }
    HANDLE waits[2] = {g_settingsEvent, g_wake};
    for (;;)
    {
        DWORD result = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (result != WAIT_OBJECT_0 && result != WAIT_OBJECT_0 + 1) return 0;
        Sleep(30); // let a burst of tree changes settle into one pass
        if (result == WAIT_OBJECT_0)
        {
            DWORD request = ReadRequest();
            ReadRulesText(text, sizeof text);
            if (RulesChanged(text)) ReplaceRules(text);
            InterlockedExchange(&g_request, static_cast<LONG>(request));
        }
        PostToXamlThreads();
    }
}

// The helper holds XAML callbacks for the life of the process, so once working inside a process it never unloads.
void PinSelf()
{
    HMODULE pinned;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCWSTR>(&PinSelf), &pinned);
}

HRESULT Start(IUnknown *site)
{
    if (InterlockedExchange(&g_started, 1)) return S_OK;
    PinSelf();
    HRESULT hr = site->QueryInterface(__uuidof(IXamlDiagnostics), reinterpret_cast<void **>(&g_diagnostics));
    if (FAILED(hr)) return hr;
    BSTR data = nullptr;
    if (SUCCEEDED(g_diagnostics->GetInitializationData(&data)) && data) ParseConfig(data);
    SysFreeString(data);

    wchar_t name[160];
    _snwprintf(name, ARRAYSIZE(name), L"%ls.%lu", g_config.event, GetCurrentProcessId());
    name[ARRAYSIZE(name) - 1] = 0;
    HANDLE existing = OpenEventW(SYNCHRONIZE, FALSE, name);
    if (existing)
    {
        // Another copy of this helper already serves this process; stay idle.
        CloseHandle(existing);
        Log(L"helper already active in this process; idle");
        return S_OK;
    }
    hr = g_diagnostics->QueryInterface(__uuidof(IVisualTreeService), reinterpret_cast<void **>(&g_tree));
    if (FAILED(hr)) return hr;
    g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_settingsEvent = CreateEventW(nullptr, FALSE, TRUE, name); // starts signalled: apply once the tree is known
    if (!g_wake || !g_settingsEvent) return HRESULT_FROM_WIN32(GetLastError());
    Log(L"helper %lu started in process %lu (key=%ls)", HelperVersion, GetCurrentProcessId(), g_config.key);
    HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
    if (!thread) return HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(thread);
    return S_OK;
}

IUnknown *g_site;

class Tap final : public IObjectWithSite
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IObjectWithSite)))
        {
            *out = static_cast<IObjectWithSite *>(this);
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE SetSite(IUnknown *site) override
    {
        if (!site) return S_OK;
        if (!g_site)
        {
            g_site = site;
            g_site->AddRef();
        }
        return Start(site);
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void **out) override
    {
        if (!out) return E_POINTER;
        *out = nullptr;
        return g_site ? g_site->QueryInterface(riid, out) : E_FAIL;
    }
};

Tap g_tap;

class Factory final : public IClassFactory
{
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IClassFactory)))
        {
            *out = static_cast<IClassFactory *>(this);
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown *outer, REFIID riid, void **out) override
    {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        return g_tap.QueryInterface(riid, out);
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }
};

Factory g_factory;

// ---- loading (the app's message hook brings this DLL onto the target's XAML thread) ----

using InitializeXamlDiagnosticsExFn = HRESULT(WINAPI *)(LPCWSTR, DWORD, LPCWSTR, LPCWSTR, CLSID, LPCWSTR);
volatile LONG g_hooked;

// Runs on the XAML thread the app hooked: asks this process's own XAML to attach the helper (the in-process form of
// InitializeXamlDiagnosticsEx). Settings for the helper come from "Local\SoltexTaskbarInit.<process id>".
bool AttachFromInside()
{
    wchar_t name[64], data[1024] = {};
    _snwprintf(name, ARRAYSIZE(name), L"Local\\SoltexTaskbarInit.%lu", GetCurrentProcessId());
    name[ARRAYSIZE(name) - 1] = 0;
    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!map) return false; // not ours to start
    if (const wchar_t *view = static_cast<const wchar_t *>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, sizeof data - sizeof(wchar_t))))
    {
        wcsncpy(data, view, ARRAYSIZE(data) - 1);
        UnmapViewOfFile(view);
    }
    CloseHandle(map);
    ParseConfig(data);

    wchar_t self[MAX_PATH];
    DWORD length = GetModuleFileNameW(g_module, self, ARRAYSIZE(self));
    HMODULE xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    auto initialize = xaml ? reinterpret_cast<InitializeXamlDiagnosticsExFn>(GetProcAddress(xaml, "InitializeXamlDiagnosticsEx")) : nullptr;
    if (!initialize || length == 0 || length >= ARRAYSIZE(self))
    {
        Log(L"XAML diagnostics are not available here");
        ReportFailure(HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND));
        return false;
    }
    // Each XAML core in the process has its own numbered connection; use the first that answers.
    HRESULT hr = E_FAIL;
    for (int i = 1; i <= 16; i++)
    {
        wchar_t endpoint[32];
        _snwprintf(endpoint, ARRAYSIZE(endpoint), L"VisualDiagConnection%d", i);
        endpoint[ARRAYSIZE(endpoint) - 1] = 0;
        hr = initialize(endpoint, GetCurrentProcessId(), L"", self, ClsidTap, data);
        Log(L"attach %ls on thread %lu hr=0x%08lX", endpoint, GetCurrentThreadId(), static_cast<unsigned long>(hr));
        if (hr != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) break;
    }
    if (FAILED(hr)) ReportFailure(hr);
    return SUCCEEDED(hr);
}

} // namespace

// WH_GETMESSAGE hook installed by the app on the target's XAML thread for a moment.
extern "C" LRESULT CALLBACK SoltexTaskbarHook(int code, WPARAM wParam, LPARAM lParam)
{
    // Once per process; a failed attempt (XAML still starting) may be retried by the app's next hook.
    if (code >= 0 && !InterlockedExchange(&g_hooked, 1))
    {
        PinSelf(); // stay loaded after the app removes its hook
        if (!AttachFromInside()) InterlockedExchange(&g_hooked, 0);
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

extern "C" HRESULT STDAPICALLTYPE SoltexDllGetClassObject(REFCLSID clsid, REFIID riid, void **out)
{
    if (!out) return E_POINTER;
    *out = nullptr;
    if (!IsEqualCLSID(clsid, ClsidTap)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, out);
}

extern "C" HRESULT STDAPICALLTYPE SoltexDllCanUnloadNow() { return S_FALSE; }

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void *)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
